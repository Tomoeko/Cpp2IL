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
/// Proves a complete x64 null diamond that stores a Boolean literal through one
/// unchanged reference field of an instance. The null arm must call the proved
/// nonreturning target-runtime helper; no arbitrary native body is lowered here.
/// </summary>
internal static class X64NestedBooleanLiteralStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext ReceiverField, FieldAnalysisContext ValueField,
        bool Value);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || method.IsStatic || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            !method.IsVoid || method.OverrideReturnType != null || method.Parameters.Count > 1 ||
            method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.parameterCount > 1 ||
            !HasSupportedParameters(method, definition) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 24 or > 48)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 8 or > 24 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[7].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(8).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[7].NextIP, region.End) ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }) ||
            !FieldLoad(native[1], out var receiverOffset) ||
            !Test(native[2], NativeRegister.RAX) ||
            native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
            native[3].NearBranchTarget != native[7].IP ||
            !BooleanStore(native[4], out var valueOffset, out var value) ||
            !Stack(native[5], Mnemonic.Add) ||
            native[6].Code != Code.Retnq || native[6].OpCount != 0 ||
            native[7].Code != Code.Call_rel32_64 || native[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, native[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native.Take(8).ToArray(),
                new HashSet<ulong> { native[7].IP }) != null)
            return null;

        var receivers = owner.Fields.Where(field => !field.IsStatic && field.Offset == (long)receiverOffset &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (receivers is not [{ } receiverField] ||
            receiverField.Name != receiverField.DefaultName ||
            receiverField.FieldType is not { Definition: { GenericContainer: null } } box ||
            !NullCheckedCall.IsReferenceClass(box))
            return null;
        var ownerLocal = new LocalVariable("proved-owner", new ManagedRegister(null, "proved-owner"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(receiverField, ownerLocal, (int)receiverOffset)))
            return null;

        var values = box.Fields.Where(field => !field.IsStatic && field.Offset == (long)valueOffset &&
            ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (values is not [{ } valueField] || valueField.Name != valueField.DefaultName ||
            (valueField.Attributes & FieldAttributes.InitOnly) != 0 ||
            !ReferenceEquals(box, owner) &&
            (valueField.Visibility != FieldAttributes.Public || box.DeclaringType != null ||
                box.Visibility != TypeAttributes.Public))
            return null;
        var boxLocal = new LocalVariable("proved-receiver", new ManagedRegister(null, "proved-receiver"), box);
        if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                new FieldReference(valueField, boxLocal, (int)valueOffset), 8))
            return null;

        return new Evidence(receiverField, valueField, value);
    }

    private static bool HasSupportedParameters(MethodAnalysisContext method,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition)
    {
        if (!RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return false;
        if (method.Parameters.Count == 0)
            return true;
        if (method.Parameters is not [var ignored] ||
            definition.InternalParameterData is not [var raw] ||
            ignored.ParameterIndex != 0 ||
            !ReferenceEquals(ignored.DeclaringMethod, method) ||
            !ReferenceEquals(ignored.Definition, raw) || ignored.IsRef ||
            ignored.Name != ignored.DefaultName ||
            ignored.Attributes != ignored.DefaultAttributes ||
            ignored.OverrideAttributes != null || ignored.OverrideParameterType != null ||
            ignored.UseOverrideDefaultValue ||
            raw.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            ignored.ParameterType is not { Definition: { GenericContainer: null } } type ||
            !NullCheckedCall.IsReferenceClass(type))
            return false;

        // The complete native body below reads RCX for `this` but never reads
        // the second argument register, RDX. Keep the ignored argument within
        // the ordinary single-register reference ABI.
        return true;
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool FieldLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
            instruction.Op0Register == NativeRegister.RAX && instruction.Op1Kind == OpKind.Memory &&
            instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
            instruction.MemorySize.GetSize() == 8 &&
            // A null `this` must fault before the Boolean store. Keep the whole read
            // within Windows' low invalid-address range, well below its 64-KiB limit.
            // https://learn.microsoft.com/en-us/shows/inside/access-violation-c0000005-read-or-write
            offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool BooleanStore(NativeInstruction instruction, out ulong offset, out bool value)
    {
        offset = instruction.MemoryDisplacement64;
        value = instruction.Immediate8 == 1;
        return instruction.Code == Code.Mov_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
            instruction.MemoryBase == NativeRegister.RAX && instruction.MemoryIndex == NativeRegister.None &&
            instruction.MemorySize.GetSize() == 1 && instruction.Op1Kind == OpKind.Immediate8 &&
            instruction.Immediate8 is 0 or 1 && offset <= int.MaxValue;
    }
}
