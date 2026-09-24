using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
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
/// Proves a complete exact-target setter that writes its unchanged Int32 argument
/// to one non-thread-static field in its own TypeInfo static storage. The saved
/// RBX value carries the argument across the authenticated metadata initializer.
/// </summary>
internal static class X64MetadataStaticInt32SetterProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(FieldAnalysisContext Field, ulong TypeInfoSlot);
    internal sealed record Shape(ulong Flag, ulong TypeInfoSlot, ulong Initializer,
        ulong FieldOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            !OrdinaryMethod(method, method.AppContext) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;
        method.EnsureRawBytes();
        return Find(method, X86Utils.Iterate(method).ToArray());
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryMethod(method, app) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !X64MetadataStaticGetterProof.OrdinaryOwner(owner) ||
            owner.Namespace != owner.DefaultNamespace ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End - start != 59 ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame))
            return null;

        method.EnsureRawBytes();
        if (method.RawBytes.Length < 59 || decoded.Count < 14 ||
            !FileBackedExecutableBody(method, pe, unwind, start, region.End) ||
            !decoded.SequenceEqual(X86Utils.Iterate(method)))
            return null;

        var exactBody = X86Utils.Iterate(method.RawBytes.AsSpan().Slice(0, 59), start, false);
        if (exactBody.Count != 14 || !decoded.Take(14).SequenceEqual(exactBody))
            return null;
        var body = decoded.Take(14).ToArray();
        if (body[0].IP != start || body[^1].NextIP != region.End ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            Enumerable.Range(1, 58).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            TryProveShape(body) is not { } shape ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;

        if (shape.TypeInfoSlot <= shape.Flag && shape.Flag - shape.TypeInfoSlot < 8 ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind,
                shape.TypeInfoSlot, 8) ||
            !X64MetadataStaticGetterProof.ZeroInitializedWritableData(unwind,
                shape.Flag, 1) ||
            app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata !=
                shape.Initializer ||
            !X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                shape.Initializer) ||
            app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot) is not
                { Type: MetadataUsageType.TypeInfo, IsValid: true } usage ||
            !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), owner) ||
            shape.FieldOffset > int.MaxValue ||
            shape.FieldOffset + 4 > owner.Definition!.RawSizes.static_fields_size)
            return null;

        var fields = owner.Fields.Where(field => field.IsStatic &&
            field.Offset == (long)shape.FieldOffset).ToArray();
        if (fields is not [{ } matched] || !UnchangedField(matched, app) ||
            !OtherFieldsDoNotOverlap(owner, matched))
            return null;
        return new Evidence(matched, shape.TypeInfoSlot);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 14 ||
            body[0].Code != Code.Push_r64 || body[0].Op0Register != NativeRegister.RBX ||
            !Stack(body[1], Mnemonic.Sub) ||
            !RipByteComparison(body[2]) ||
            !RegisterCopy(body[3], NativeRegister.EBX, NativeRegister.ECX) ||
            body[4].Code != Code.Jne_rel8_64 ||
            body[4].NearBranchTarget != body[8].IP ||
            !RipLea(body[5], NativeRegister.RCX) ||
            body[6].Code != Code.Call_rel32_64 || body[6].NearBranchTarget == 0 ||
            !RipByteStoreOne(body[7]) ||
            body[7].IPRelativeMemoryAddress != body[2].IPRelativeMemoryAddress ||
            !RipLoad(body[8], NativeRegister.RAX) ||
            body[8].IPRelativeMemoryAddress != body[5].IPRelativeMemoryAddress ||
            !StaticFieldsPointer(body[9]) ||
            !DwordStore(body[10]) ||
            body[10].MemoryDisplacement64 > int.MaxValue ||
            !Stack(body[11], Mnemonic.Add) ||
            body[12].Code != Code.Pop_r64 || body[12].Op0Register != NativeRegister.RBX ||
            body[13].Code != Code.Retnq || body[13].OpCount != 0)
            return null;

        return new Shape(body[2].IPRelativeMemoryAddress,
            body[5].IPRelativeMemoryAddress, body[6].NearBranchTarget,
            body[10].MemoryDisplacement64);
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        ApplicationAnalysisContext app)
    {
        if (!method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.OverrideReturnType != null || method.Parameters is not [var parameter] ||
            method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [var rawParameter] ||
            !ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type))
            return false;
        return true;
    }

    private static bool UnchangedField(FieldAnalysisContext field,
        ApplicationAnalysisContext app) =>
        field.IsStatic && field.Offset >= 0 && field.Offset == field.DefaultOffset &&
        field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
        field.OverrideFieldType == null && !field.UseOverrideConstantValue &&
        field.RawIl2CppCustomAttributeData.Length == 0 &&
        field.CustomAttributes is not { Count: > 0 } &&
        ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt32Type) &&
        field.BackingData?.Field.RawFieldType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } &&
        (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal | FieldAttributes.HasFieldRVA |
                             FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) == 0 &&
        field.StaticArrayInitialValue.Length == 0;

    private static bool OtherFieldsDoNotOverlap(TypeAnalysisContext owner,
        FieldAnalysisContext stored)
    {
        foreach (var other in owner.Fields.Where(field => field.IsStatic &&
                     !ReferenceEquals(field, stored) &&
                     (field.Attributes & FieldAttributes.Literal) == 0))
        {
            var width = StaticStorageWidth(other.FieldType,
                owner.AppContext.Binary.PointerSizeBytes);
            if (width <= 0 || other.Offset < 0 ||
                other.Offset != other.DefaultOffset || other.OverrideFieldType != null ||
                NarrowFieldEqualityProof.StorageRangesOverlap(stored.Offset, 4,
                    other.Offset, width))
                return false;
        }
        return true;
    }

    private static long StaticStorageWidth(TypeAnalysisContext type, int pointerSize) =>
        type.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or
                Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or
                Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
                Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
                Il2CppTypeEnum.IL2CPP_TYPE_R8 => 8,
            _ when type.IsEnumType => StaticStorageWidth(type.EnumUnderlyingType!, pointerSize),
            _ when !type.IsValueType => pointerSize,
            _ => TypeSizes.UnboxedSize(type, pointerSize),
        };

    private static bool FileBackedExecutableBody(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind, ulong start, ulong end)
    {
        if (start < unwind.ImageBase || end != start + 59 ||
            start - unwind.ImageBase > uint.MaxValue - 58)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var bytes = pe.GetRawBinaryContent();
        return first >= 0 && last == first + 58 && first <= bytes.Length - 59 &&
               method.RawBytes.AsSpan().Slice(0, 59)
                   .SequenceEqual(bytes.Slice((int)first, 59)) &&
               Enumerable.Range(0, 59).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(
                       start + (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                       first + offset);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool RipMemory(NativeInstruction instruction, int operand, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == width;

    private static bool RipByteComparison(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipByteStoreOne(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool RegisterCopy(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == Code.Mov_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool RipLea(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool RipLoad(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        RipMemory(instruction, 1, 8);

    private static bool StaticFieldsPointer(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RDX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 ==
            (ulong)Il2CppClassLayout.StaticFieldsOffset64 &&
        instruction.MemorySize.GetSize() == 8;

    private static bool DwordStore(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_rm32_r32 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.EBX;
}
