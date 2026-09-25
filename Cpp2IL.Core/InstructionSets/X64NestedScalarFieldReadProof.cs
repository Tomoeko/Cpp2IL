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
/// Proves an instance Boolean or Int32 read through one reference field.
/// Its complete native body has a terminal, proved runtime null throw for a
/// missing child; the managed second field read retains that exception.
/// </summary>
internal static class X64NestedScalarFieldReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ReceiverField,
        FieldAnalysisContext ValueField);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.OverrideReturnType != null || method.Parameters.Count != 0 ||
            method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn } definition ||
            rawReturn.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or
                Il2CppTypeEnum.IL2CPP_TYPE_I4) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType,
                rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN
                    ? app.SystemTypes.SystemBooleanType : app.SystemTypes.SystemInt32Type) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method))
            return null;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Getter, method)).ToArray();
        if ((method.Attributes & MethodAttributes.SpecialName) != 0)
        {
            if (properties is not [{ } property] || property.Setter != null ||
                property.Definition is not { } rawProperty ||
                !ReferenceEquals(rawProperty.Getter, definition) ||
                property.Name != property.DefaultName ||
                method.Name != "get_" + property.Name ||
                property.Attributes != property.DefaultAttributes ||
                property.OverridePropertyType != null || property.IsStatic ||
                !ReferenceEquals(property.PropertyType, method.ReturnType) ||
                rawProperty.RawPropertyType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawPropertyType ||
                rawPropertyType.Type != rawReturn.Type)
                return null;
        }
        else if (properties.Length != 0)
            return null;

        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End <= start || region.End - start is < 20 or > 64 ||
            !unwind.MatchesUnwind(start, region.End, 4, 0, new byte[] { 4, 0x42 }))
            return null;

        var body = X86Utils.Iterate(method).ToArray();
        if (body.Length != 8 || body[0].IP != start ||
            !FileBackedExecutableBody(method, pe, unwind, start,
                body[^1].NextIP, region.End) ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !Stack(body[0], Mnemonic.Sub) ||
            !ReferenceLoad(body[1], out var receiverOffset) ||
            !Test(body[2], NativeRegister.RAX) ||
            body[3].Mnemonic != Mnemonic.Je || body[3].Op0Kind != OpKind.NearBranch64 ||
            body[3].NearBranchTarget != body[7].IP ||
            !ScalarLoad(body[4], rawReturn.Type, out var valueOffset) ||
            !Stack(body[5], Mnemonic.Add) ||
            body[6].Code != Code.Retnq || body[6].OpCount != 0 ||
            body[7].Code != Code.Call_rel32_64 || body[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[7].IP }) != null)
            return null;

        var receivers = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)receiverOffset &&
            field.BackingData?.Field.RawFieldType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (receivers is not [{ } receiverField] ||
            receiverField.Name != receiverField.DefaultName ||
            receiverField.FieldType is not { Definition: { GenericContainer: null } } child ||
            !NullCheckedCall.IsReferenceClass(child))
            return null;
        var ownerLocal = new LocalVariable("proved-owner",
            new ManagedRegister(null, "proved-owner"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(receiverField, ownerLocal, (int)receiverOffset)))
            return null;

        var values = child.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)valueOffset &&
            ReferenceEquals(field.FieldType, method.ReturnType) &&
            field.BackingData?.Field.RawFieldType is
                { NumMods: 0, Byref: 0, Pinned: 0 } rawField &&
            rawField.Type == rawReturn.Type).ToArray();
        if (values is not [{ } valueField] || valueField.Name != valueField.DefaultName ||
            !ReferenceEquals(child, owner) &&
            (valueField.Visibility != FieldAttributes.Public || child.DeclaringType != null ||
             child.Visibility != TypeAttributes.Public))
            return null;
        var childLocal = new LocalVariable("proved-child",
            new ManagedRegister(null, "proved-child"), child);
        if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                new FieldReference(valueField, childLocal, (int)valueOffset),
                rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ? 8 : 32))
            return null;

        return new Evidence(receiverField, valueField);
    }

    private static bool FileBackedExecutableBody(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, ulong start, ulong bodyEnd, ulong regionEnd)
    {
        if (bodyEnd <= start || bodyEnd > regionEnd ||
            regionEnd - bodyEnd > 16 ||
            (ulong)method.RawBytes.Length != bodyEnd - start ||
            start < unwind.ImageBase ||
            start - unwind.ImageBase > uint.MaxValue - (uint)(regionEnd - start) + 1)
            return false;
        var length = checked((int)(bodyEnd - start));
        var regionLength = checked((int)(regionEnd - start));
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(regionEnd - 1, false);
        var image = pe.GetRawBinaryContent();
        return first >= 0 && last == first + regionLength - 1 &&
               first <= image.Length - regionLength &&
               method.RawBytes.AsSpan().SequenceEqual(image.Slice((int)first, length)) &&
               X64NativePaddingProof.HasInt3Padding(pe, bodyEnd, regionEnd) &&
               Enumerable.Range(0, regionLength).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                       unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) == first + offset);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool ReferenceLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RAX &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8 &&
               // A null `this` must fault before the child-field read.
               offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool ScalarLoad(NativeInstruction instruction, Il2CppTypeEnum type,
        out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == (type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN
                   ? Code.Movzx_r32_rm8 : Code.Mov_r32_rm32) &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.EAX &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RAX &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() ==
                   (type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ? 1 : 4) &&
               offset <= int.MaxValue;
    }
}
