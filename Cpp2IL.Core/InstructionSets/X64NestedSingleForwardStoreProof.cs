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
/// Binds a complete null-guarded x64 Single store to one public, nonvirtual
/// setter whose entire native body performs that store and returns.
/// </summary>
internal static class X64NestedSingleForwardStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext ReceiverField,
        FieldAnalysisContext ValueField, MethodAnalysisContext Setter);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || !method.IsVoid ||
            method.OverrideReturnType != null || method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !HasUnchangedSingleParameter(method, definition) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer,
            method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 24 or > 48)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method)
            .TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 8 or > 24 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[7].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(8).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[7].NextIP, region.End) ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0,
                new byte[] { 4, 0x42 }) ||
            !ReceiverLoad(native[1], out var receiverOffset) ||
            !Test(native[2], NativeRegister.RAX) ||
            native[3].Mnemonic != Mnemonic.Je ||
            native[3].Op0Kind != OpKind.NearBranch64 ||
            native[3].NearBranchTarget != native[7].IP ||
            !SingleStore(native[4], NativeRegister.RAX, out var valueOffset) ||
            !Stack(native[5], Mnemonic.Add) ||
            native[6].Code != Code.Retnq || native[6].OpCount != 0 ||
            native[7].Code != Code.Call_rel32_64 ||
            native[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, native[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native.Take(8).ToArray(),
                new HashSet<ulong> { native[7].IP }) != null)
            return null;

        var receivers = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)receiverOffset &&
            field.BackingData?.Field.RawFieldType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (receivers is not [{ } receiverField] ||
            receiverField.Name != receiverField.DefaultName ||
            receiverField.FieldType is not { Definition: { GenericContainer: null,
                HasCctor: false } } target ||
            target.Name != target.DefaultName ||
            target.Namespace != target.DefaultNamespace ||
            !NullCheckedCall.IsReferenceClass(target))
            return null;
        var ownerLocal = new LocalVariable("proved-owner",
            new ManagedRegister(null, "proved-owner"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(receiverField, ownerLocal, (int)receiverOffset)))
            return null;

        var values = target.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)valueOffset &&
            ReferenceEquals(field.FieldType, app.SystemTypes.SystemSingleType) &&
            field.BackingData?.Field.RawFieldType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (values is not [{ } valueField] ||
            valueField.Name != valueField.DefaultName ||
            valueField.Visibility != FieldAttributes.Private ||
            valueField.Attributes != valueField.DefaultAttributes)
            return null;
        var targetLocal = new LocalVariable("proved-receiver",
            new ManagedRegister(null, "proved-receiver"), target);
        if (!NarrowFieldEqualityProof.HasUnchangedSingleFieldLayout(
                new FieldReference(valueField, targetLocal, (int)valueOffset)))
            return null;

        var setters = target.Methods.Where(candidate =>
            ValidSetterMetadata(candidate, target, app) &&
            HasExactSetterBody(candidate, pe, unwind, (int)valueOffset)).ToArray();
        return setters is [{ } setter]
            ? new Evidence(receiverField, valueField, setter)
            : null;
    }

    private static bool HasUnchangedSingleParameter(MethodAnalysisContext method,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition)
    {
        if (definition.InternalParameterData is not [var raw] ||
            raw.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, raw) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Name != parameter.DefaultName ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideAttributes != null ||
            parameter.OverrideParameterType != null ||
            parameter.UseOverrideDefaultValue ||
            !ReferenceEquals(parameter.ParameterType,
                method.AppContext.SystemTypes.SystemSingleType))
            return false;
        return true;
    }

    private static bool ValidSetterMetadata(MethodAnalysisContext setter,
        TypeAnalysisContext target, ApplicationAnalysisContext app)
    {
        if (!ReferenceEquals(setter.DeclaringType, target) ||
            !ReferenceEquals(setter.AppContext, app) ||
            setter.Name != setter.DefaultName ||
            setter.Name is ".ctor" or ".cctor" ||
            setter.IsStatic || setter.IsVirtual || !setter.IsVoid ||
            setter.GenericParameters.Count != 0 ||
            setter.Attributes != setter.DefaultAttributes ||
            setter.ImplAttributes != setter.DefaultImplAttributes ||
            (setter.Attributes & MethodAttributes.MemberAccessMask) !=
                MethodAttributes.Public ||
            (setter.Attributes & (MethodAttributes.Abstract |
                                  MethodAttributes.PinvokeImpl)) != 0 ||
            (setter.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            setter.OverrideReturnType != null ||
            !ReferenceEquals(setter.ReturnType, app.SystemTypes.SystemVoidType) ||
            setter.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, target.Definition) ||
            !HasUnchangedSingleParameter(setter, definition) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(setter) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(setter) ||
            setter.UnderlyingPointer is 0 or ulong.MaxValue)
            return false;
        return true;
    }

    private static bool HasExactSetterBody(MethodAnalysisContext setter,
        PE pe, X64UnwindProof.Index unwind, int fieldOffset)
    {
        try
        {
            setter.EnsureRawBytes();
            var start = setter.UnderlyingPointer;
            if (setter.RawBytes.Length < 6 ||
                start > ulong.MaxValue - (ulong)setter.RawBytes.Length)
                return false;
            var body = X86Utils.Iterate(setter.RawBytes.AsSpan(), start, false);
            if (body.Count < 2 || body[0].IP != start ||
                !SingleStore(body[0], NativeRegister.RCX, out var offset) ||
                offset != (ulong)fieldOffset ||
                body[1].Code != Code.Retnq || body[1].OpCount != 0 ||
                body[1].IP != body[0].NextIP ||
                body.Take(2).Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None))
                return false;

            var leafEnd = body[1].NextIP;
            var index = 2;
            var paddedEnd = leafEnd;
            while (index < body.Count && body[index].Code == Code.Int3)
            {
                if (body[index].IP != paddedEnd || body[index].Length != 1 ||
                    paddedEnd - leafEnd >= 16)
                    return false;
                paddedEnd = body[index].NextIP;
                index++;
            }
            if (paddedEnd - start > (ulong)setter.RawBytes.Length ||
                !X64NativePaddingProof.HasInt3Padding(pe, leafEnd, paddedEnd) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                    setter.RawBytes.AsSpan().Slice(0,
                        checked((int)(paddedEnd - start))), start) ||
                unwind.ClassifySpan(start, paddedEnd).Kind !=
                    X64UnwindProof.SpanKind.NoEntry ||
                Enumerable.Range(1, checked((int)(paddedEnd - start - 1)))
                    .Any(offsetInBody => setter.AppContext.MethodsByAddress
                        .ContainsKey(start + (ulong)offsetInBody)) ||
                X86CallerExceptionRegionProof.Check(setter,
                    body.Take(2).ToArray(), new HashSet<ulong>()) != null)
                return false;
            if (index == body.Count)
                return paddedEnd == start + (ulong)setter.RawBytes.Length;

            var next = body[index];
            return paddedEnd > leafEnd && next.IP == paddedEnd &&
                   !next.IsInvalid && next.CodeSize == CodeSize.Code64 &&
                   unwind.ClassifySpan(paddedEnd, next.NextIP) is
                       { Kind: X64UnwindProof.SpanKind.HandlerFree,
                           Start: var nextStart, RootStart: var nextRoot } &&
                   nextStart == paddedEnd && nextRoot == paddedEnd;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool ReceiverLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op0Register == NativeRegister.RAX &&
            instruction.Op1Kind == OpKind.Memory &&
            instruction.MemoryBase == NativeRegister.RCX &&
            instruction.MemoryIndex == NativeRegister.None &&
            instruction.MemorySize.GetSize() == 8 && offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool SingleStore(NativeInstruction instruction,
        NativeRegister receiver, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Movss_xmmm32_xmm &&
            instruction.Op0Kind == OpKind.Memory &&
            instruction.MemoryBase == receiver &&
            instruction.MemoryIndex == NativeRegister.None &&
            instruction.MemorySize.GetSize() == 4 &&
            instruction.Op1Kind == OpKind.Register &&
            instruction.Op1Register == NativeRegister.XMM1 &&
            offset <= 0x1000 - 4;
    }
}
