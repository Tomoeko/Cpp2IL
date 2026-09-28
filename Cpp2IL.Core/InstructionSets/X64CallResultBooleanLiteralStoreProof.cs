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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the complete side-effect call, direct reference getter, and guarded
/// Boolean literal store. A folded getter is selected only by its unchanged
/// receiver class and its own exact field-getter body.
/// </summary>
internal static class X64CallResultBooleanLiteralStoreProof
{
    internal sealed record Evidence(MethodAnalysisContext Effect, MethodAnalysisContext Getter,
        FieldAnalysisContext Field, bool Value);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.IsStatic || method.IsVirtual || !method.IsVoid ||
                !HasOrdinaryMethodIdentity(method) ||
                method.OverrideReturnType != null || method.Parameters.Count != 0 ||
                method.GenericParameters.Count != 0 ||
                method.Definition is not { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue)
                return null;

            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
                region.End <= region.Start || region.End - region.Start is < 35 or > 64)
                return null;

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            if (body.Length != 15 || !MatchesBody(body, out var offset, out var value) ||
                body[0].IP != region.Start || body[^1].NextIP + 1 != region.End ||
                method.RawBytes.Length != checked((int)(body[^1].NextIP - region.Start)) ||
                body.Where((instruction, index) => index > 0 &&
                    instruction.IP != body[index - 1].NextIP).Any() ||
                body.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                    method.RawBytes.AsSpan(), region.Start) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
                !unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    [6, 0x32, 2, 0x30]) ||
                app.MethodsByAddress.Keys.Any(address =>
                    address > region.Start && address < region.End) ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[14].NearBranchTarget) == null ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[14].IP }) != null)
                return null;

            if (!app.MethodsByAddress.TryGetValue(body[4].NearBranchTarget,
                    out var effectBindings) || effectBindings is not [var effect] ||
                !ValidEffect(effect, owner) ||
                !app.MethodsByAddress.TryGetValue(body[7].NearBranchTarget,
                    out var getterBindings) || getterBindings.Count == 0)
                return null;

            var applicableGetters = getterBindings.Where(candidate =>
                ReferenceEquals(candidate.DeclaringType, owner) &&
                ValidGetter(candidate, owner)).ToArray();
            if (applicableGetters is not [var getter] ||
                !CallResultNullGuardProof.HasUnambiguousTarget(getter, owner) ||
                !CallResultNullGuardProof.HasExactReferenceGetterBody(getter))
                return null;

            var receiver = getter.ReturnType;
            if (receiver is not { Definition: { GenericContainer: null } } ||
                receiver.IsGenericInstance || receiver.GenericParameters.Count != 0 ||
                receiver.Attributes != receiver.DefaultAttributes ||
                receiver.DeclaringType != null || receiver.Visibility != TypeAttributes.Public ||
                !ReferenceEquals(receiver.DeclaringAssembly, owner.DeclaringAssembly))
                return null;

            var fields = receiver.Fields.Where(field => !field.IsStatic &&
                field.Offset == checked((int)offset) &&
                ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) &&
                field.BackingData?.Field.RawFieldType is
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
            if (fields is not [{ } matched] ||
                matched.Name != matched.DefaultName ||
                matched.Visibility != FieldAttributes.Public ||
                (matched.Attributes & FieldAttributes.InitOnly) != 0 ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new FieldReference(matched,
                        new LocalVariable("proved-call-result",
                            new ManagedRegister(null, "rax"), receiver),
                        checked((int)offset)), 8))
                return null;

            return new Evidence(effect, getter, matched, value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidEffect(MethodAnalysisContext target, TypeAnalysisContext owner) =>
        !target.IsStatic && !target.IsVirtual && target.IsVoid &&
        HasOrdinaryMethodIdentity(target) &&
        ReferenceEquals(target.DeclaringType, owner) &&
        target.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        target.Parameters.Count == 0 && target.GenericParameters.Count == 0 &&
        target.Name == target.DefaultName &&
        target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(target);

    // A constructor requires its own proved base-call semantics. This proof
    // emits ordinary direct calls and cannot replace a constructor body.
    internal static bool HasOrdinaryMethodIdentity(MethodAnalysisContext method) =>
        method.Name is not (".ctor" or ".cctor") &&
        method.DefaultName is not (".ctor" or ".cctor");

    private static bool ValidGetter(MethodAnalysisContext target, TypeAnalysisContext owner) =>
        !target.IsStatic && !target.IsVirtual && !target.IsVoid &&
        ReferenceEquals(target.DeclaringType, owner) &&
        target.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        target.Parameters.Count == 0 && target.GenericParameters.Count == 0 &&
        target.Name == target.DefaultName &&
        target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes &&
        NullCheckedCall.IsReferenceClass(target.ReturnType) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target,
            requireUniqueBinding: false) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(target);

    private static bool MatchesBody(IReadOnlyList<NativeInstruction> body,
        out ulong fieldOffset, out bool value)
    {
        fieldOffset = body[10].MemoryDisplacement64;
        value = body[10].Immediate8 == 1;
        return body[0].Code == Code.Push_r64 &&
               body[0].Op0Register == NativeRegister.RBX &&
               Stack(body[1], Mnemonic.Sub) &&
               Registers(body[2], Mnemonic.Xor, NativeRegister.EDX, NativeRegister.EDX) &&
               Registers(body[3], Mnemonic.Mov, NativeRegister.RBX, NativeRegister.RCX) &&
               Call(body[4]) &&
               Registers(body[5], Mnemonic.Xor, NativeRegister.EDX, NativeRegister.EDX) &&
               Registers(body[6], Mnemonic.Mov, NativeRegister.RCX, NativeRegister.RBX) &&
               Call(body[7]) &&
               Registers(body[8], Mnemonic.Test, NativeRegister.RAX, NativeRegister.RAX) &&
               body[9].Mnemonic == Mnemonic.Je &&
               body[9].Op0Kind == OpKind.NearBranch64 &&
               body[9].NearBranchTarget == body[14].IP &&
               body[10].Code == Code.Mov_rm8_imm8 &&
               body[10].Op0Kind == OpKind.Memory &&
               body[10].MemoryBase == NativeRegister.RAX &&
               body[10].MemoryIndex == NativeRegister.None &&
               body[10].MemorySize.GetSize() == 1 &&
               body[10].Op1Kind == OpKind.Immediate8 &&
               body[10].Immediate8 is 0 or 1 && fieldOffset <= int.MaxValue &&
               Stack(body[11], Mnemonic.Add) &&
               body[12].Code == Code.Pop_r64 &&
               body[12].Op0Register == NativeRegister.RBX &&
               body[13].Code == Code.Retnq && body[13].OpCount == 0 &&
               Call(body[14]);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool Registers(NativeInstruction instruction, Mnemonic mnemonic,
        NativeRegister destination, NativeRegister source) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
