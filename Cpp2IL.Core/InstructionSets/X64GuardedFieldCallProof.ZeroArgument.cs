using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedFieldCallProof
{
    // The direct tail jump can share its native address with unrelated methods. The
    // receiver field's original class and unchanged signature must select one binding.
    private static Evidence? FindZeroArgumentInt32(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || method.IsStatic ||
            method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters.Count != 0 ||
            method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer == 0)
            return null;

        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End <= start || region.End - start is < 24 or > 48 ||
            !unwind.MatchesUnwind(start, region.End, 4, 0, new byte[] { 4, 0x42 }))
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).ToArray();
        if (native.Length != 8 || native[0].IP != start ||
            native[^1].NextIP != start + (ulong)method.RawBytes.Length ||
            native[^1].NextIP > region.End || region.End - native[^1].NextIP > 16 ||
            Enumerable.Range(1, method.RawBytes.Length - 1).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan(), start) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[^1].NextIP, region.End) ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            !Stack(native[0], Mnemonic.Sub) ||
            !FieldLoad(native[1], NativeRegister.RCX, NativeRegister.RCX, 8,
                out var receiverOffset) || receiverOffset > 0x1000 - 8 ||
            !Test(native[2], NativeRegister.RCX) ||
            native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
            native[3].NearBranchTarget != native[7].IP ||
            !Zero(native[4], NativeRegister.EDX) ||
            !Stack(native[5], Mnemonic.Add) ||
            native[6].Code != Code.Jmp_rel32_64 ||
            native[6].Op0Kind != OpKind.NearBranch64 ||
            native[7].Code != Code.Call_rel32_64 ||
            native[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, native[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native,
                new HashSet<ulong> { native[7].IP }) != null ||
            !UniqueField(owner, receiverOffset, null, out var receiverField) ||
            receiverField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NullCheckedCall.IsReferenceClass(receiverField.FieldType) ||
            !app.MethodsByAddress.TryGetValue(native[6].NearBranchTarget,
                out var bindings))
            return null;

        var candidates = bindings.Where(target =>
            target.UnderlyingPointer == native[6].NearBranchTarget &&
            ReferenceEquals(target.DeclaringType, receiverField.FieldType) &&
            !target.IsStatic && target.Parameters.Count == 0 &&
            ReferenceEquals(target.ReturnType, method.ReturnType) &&
            RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target,
                requireUniqueBinding: false) &&
            !RuntimeNullGuardCoalescer.HasOutputOptions(target) &&
            CallEligible(target, receiverField, Array.Empty<FieldAnalysisContext>())).ToArray();
        return candidates is [var target] ?
            new Evidence(target, receiverField, Array.Empty<FieldAnalysisContext>()) : null;
    }
}
