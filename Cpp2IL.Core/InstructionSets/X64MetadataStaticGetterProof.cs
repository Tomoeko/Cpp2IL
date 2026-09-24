using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete 2021.3.35f1 x64 getter whose only reachable result is a
/// non-thread-static field of its own nongeneric TypeInfo. The native metadata
/// guard is retained as evidence: ldsfld performs the corresponding class
/// initialization, and no managed class constructor is present to reorder.
/// </summary>
internal static class X64MetadataStaticGetterProof
{
    internal sealed record Evidence(FieldAnalysisContext Field, ulong TypeInfoSlot);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryGetter(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !OrdinaryOwner(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 36 or > 96)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 11 or > 32 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[10].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(11).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[10].NextIP, region.End) ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }) ||
            !X86MetadataGuardProof.FindUnresolvedInitializationGuards(method, native.Take(11).ToArray())
                .Contains(native[1].IP) ||
            native[2].NearBranchTarget != native[6].IP ||
            native[4].Code != Code.Call_rel32_64 ||
            !RipLoad(native[6], out var slot) ||
            slot != native[3].IPRelativeMemoryAddress ||
            !FieldLoadPair(native[7], native[8], out var fieldOffset, out var loadSize) ||
            !Stack(native[9], Mnemonic.Add) ||
            native[10].Code != Code.Retnq || native[10].OpCount != 0 ||
            X86CallerExceptionRegionProof.Check(method, native.Take(11).ToArray(),
                new HashSet<ulong>()) != null)
            return null;

        var flag = native[1].IPRelativeMemoryAddress;
        if (slot <= flag && flag - slot < 8 ||
            !FileBackedWritableData(pe, unwind, slot, 8) ||
            !ZeroInitializedWritableData(unwind, flag, 1))
            return null;

        var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(slot);
        if (usage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
            !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), owner) ||
            fieldOffset > int.MaxValue || fieldOffset + loadSize > owner.Definition.RawSizes.static_fields_size)
            return null;

        var fields = owner.Fields.Where(field => field.IsStatic && field.Offset == (long)fieldOffset)
            .ToArray();
        if (fields is not [{ } matched] || !UnchangedField(matched, method.ReturnType,
                definition.RawReturnType, loadSize))
            return null;
        return new Evidence(matched, slot);
    }

    private static bool OrdinaryGetter(MethodAnalysisContext method) =>
        method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.Parameters.Count == 0 &&
        method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

    private static bool OrdinaryOwner(TypeAnalysisContext owner) =>
        !owner.IsValueType && !owner.IsInterface && !owner.IsGenericInstance &&
        owner.GenericParameters.Count == 0 && owner.Definition is
            { HasCctor: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } &&
        owner.Methods.All(method => method.Name != ".cctor") &&
        owner.Name == owner.DefaultName && owner.OverrideNamespace == null &&
        owner.Attributes == owner.DefaultAttributes &&
        ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
        (owner.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout;

    private static bool UnchangedField(FieldAnalysisContext field, TypeAnalysisContext returnType,
        Il2CppType returnRawType, uint loadSize)
    {
        var app = field.AppContext;
        var raw = field.BackingData?.Field.RawFieldType;
        var type = field.FieldType;
        var nativeInteger = raw?.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I => ReferenceEquals(type, app.SystemTypes.SystemIntPtrType),
            Il2CppTypeEnum.IL2CPP_TYPE_U => ReferenceEquals(type, app.SystemTypes.SystemUIntPtrType),
            _ => false,
        };
        var reference = raw?.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
            NullCheckedCall.IsReferenceClass(type);
        var int32 = raw?.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => ReferenceEquals(type, app.SystemTypes.SystemInt32Type),
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => ReferenceEquals(type, app.SystemTypes.SystemUInt32Type),
            _ => false,
        };
        return field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
               field.OverrideFieldType == null && field.Offset == field.DefaultOffset &&
               field.Offset >= 0 && field.IsStatic &&
               (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA |
                                    FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) == 0 &&
               field.StaticArrayInitialValue.Length == 0 &&
               raw is { NumMods: 0, Byref: 0, Pinned: 0 } &&
               returnRawType.Type == raw.Type &&
               ReferenceEquals(type, returnType) &&
               ((loadSize == 8 && (nativeInteger || reference)) || (loadSize == 4 && int32));
    }

    private static bool FieldLoadPair(NativeInstruction staticFieldsLoad, NativeInstruction fieldLoad,
        out ulong fieldOffset, out uint loadSize)
    {
        if (FieldLoad(staticFieldsLoad, NativeRegister.RAX,
                (ulong)Il2CppClassLayout.StaticFieldsOffset64) &&
            FieldLoad(fieldLoad, NativeRegister.RAX, out fieldOffset))
        {
            loadSize = 8;
            return true;
        }

        if (StaticFieldsPointerToRcx(staticFieldsLoad) && DwordFieldLoadToEax(fieldLoad, out fieldOffset))
        {
            loadSize = 4;
            return true;
        }

        fieldOffset = 0;
        loadSize = 0;
        return false;
    }

    private static bool StaticFieldsPointerToRcx(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RCX && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.MemoryDisplacement64 == (ulong)Il2CppClassLayout.StaticFieldsOffset64;

    private static bool DwordFieldLoadToEax(NativeInstruction instruction, out ulong fieldOffset)
    {
        fieldOffset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r32_rm32 && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.EAX && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 4;
    }

    private static bool FileBackedWritableData(PE pe, X64UnwindProof.Index unwind,
        ulong address, uint length)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - length ||
            address + length - 1 < unwind.ImageBase)
            return false;
        var firstRva = address - unwind.ImageBase;
        var lastRva = address + length - 1 - unwind.ImageBase;
        if (lastRva > uint.MaxValue)
            return false;
        for (var rva = firstRva; rva <= lastRva; rva++)
            if (!unwind.IsWritableFileBackedRva((uint)rva))
                return false;
        var first = pe.MapVirtualAddressToRaw(address, false);
        var last = pe.MapVirtualAddressToRaw(address + length - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               last - first == length - 1;
    }

    private static bool ZeroInitializedWritableData(X64UnwindProof.Index unwind,
        ulong address, uint length)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - length ||
            address + length - 1 - unwind.ImageBase > uint.MaxValue)
            return false;
        for (var current = address; current < address + length; current++)
        {
            var rva = (uint)(current - unwind.ImageBase);
            if (!unwind.IsWritableZeroInitializedRva(rva))
                return false;
        }
        return true;
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool RipLoad(NativeInstruction instruction, out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RAX && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8;
    }

    private static bool FieldLoad(NativeInstruction instruction, NativeRegister basis, ulong offset) =>
        FieldLoad(instruction, basis, out var found) && found == offset;

    private static bool FieldLoad(NativeInstruction instruction, NativeRegister basis, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RAX && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == basis && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8;
    }
}
