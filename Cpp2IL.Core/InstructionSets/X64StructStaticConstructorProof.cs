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
/// Proves a complete exact-target struct static constructor that increments one
/// external Int32 static field and stores an Int32 and negative zero to its own
/// static fields. The compiler metadata guard is checked as part of the complete
/// native body; it is not treated as a general narrow comparison.
/// </summary>
internal static class X64StructStaticConstructorProof
{
    internal sealed record Evidence(FieldAnalysisContext Witness, FieldAnalysisContext Marker,
        FieldAnalysisContext Bias, int MarkerValue, uint BiasBits);

    internal sealed record Shape(ulong Flag, ulong OwnerSlot, ulong WitnessSlot,
        ulong Initializer, ulong WitnessOffset, ulong MarkerOffset, ulong BiasOffset,
        int MarkerValue, uint BiasBits);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryConstructor(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !OrdinaryStruct(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start != 106 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }))
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length != 19 || method.RawBytes.Length != 106 ||
            rawStart < 0 || rawEnd < rawStart || rawEnd - rawStart != 105 ||
            rawEnd >= pe.GetRawBinaryContent().Length ||
            !pe.GetRawBinaryContent().Slice(checked((int)rawStart), 106)
                .SequenceEqual(method.RawBytes.AsSpan()) ||
            native[0].IP != region.Start || native[^1].NextIP != region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            TryProveShape(native) is not { } shape ||
            X86CallerExceptionRegionProof.Check(method, native, new HashSet<ulong>()) != null)
            return null;

        if (shape.OwnerSlot == shape.WitnessSlot ||
            !Disjoint(shape.Flag, 1, shape.OwnerSlot, 8) ||
            !Disjoint(shape.Flag, 1, shape.WitnessSlot, 8) ||
            !Disjoint(shape.OwnerSlot, 8, shape.WitnessSlot, 8) ||
            !X64MetadataStaticGetterProof.ZeroInitializedWritableData(unwind, shape.Flag, 1) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.OwnerSlot, 8) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.WitnessSlot, 8) ||
            app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata !=
                shape.Initializer ||
            !X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind, shape.Initializer))
            return null;

        var ownerUsage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.OwnerSlot);
        var witnessUsage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.WitnessSlot);
        if (ownerUsage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
            witnessUsage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
            !ReferenceEquals(app.ResolveIl2CppType(ownerUsage.AsType()), owner) ||
            app.ResolveIl2CppType(witnessUsage.AsType()) is not { } witnessType ||
            !OrdinaryWitness(witnessType, owner) ||
            shape.WitnessOffset != 0 || shape.MarkerOffset != 0 || shape.BiasOffset != 4 ||
            owner.Definition.RawSizes.static_fields_size != 8 ||
            witnessType.Definition!.RawSizes.static_fields_size != 4)
            return null;

        var ownStatic = owner.Fields.Where(field => field.IsStatic).ToArray();
        var witnessStatic = witnessType.Fields.Where(field => field.IsStatic).ToArray();
        if (ownStatic.Length != 2 || witnessStatic.Length != 1 ||
            ownStatic.SingleOrDefault(field => field.Offset == (long)shape.MarkerOffset) is not { } marker ||
            ownStatic.SingleOrDefault(field => field.Offset == (long)shape.BiasOffset) is not { } bias ||
            witnessStatic.SingleOrDefault(field => field.Offset == (long)shape.WitnessOffset) is not { } witness ||
            !UnchangedField(marker, app.SystemTypes.SystemInt32Type, Il2CppTypeEnum.IL2CPP_TYPE_I4) ||
            !UnchangedField(bias, app.SystemTypes.SystemSingleType, Il2CppTypeEnum.IL2CPP_TYPE_R4) ||
            !UnchangedField(witness, app.SystemTypes.SystemInt32Type, Il2CppTypeEnum.IL2CPP_TYPE_I4) ||
            (witness.Attributes & FieldAttributes.FieldAccessMask) is not
                (FieldAttributes.Public or FieldAttributes.Assembly or FieldAttributes.FamORAssem))
            return null;

        return new Evidence(witness, marker, bias, shape.MarkerValue, shape.BiasBits);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 19 ||
            !Stack(body[0], Mnemonic.Sub) ||
            !RipByteComparison(body[1]) ||
            body[2].Code != Code.Jne_rel8_64 || body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[8].IP ||
            !RipLea(body[3], NativeRegister.RCX) ||
            !DirectCall(body[4]) ||
            !RipLea(body[5], NativeRegister.RCX) ||
            !DirectCall(body[6]) ||
            body[4].NearBranchTarget != body[6].NearBranchTarget ||
            !RipByteStoreOne(body[7]) ||
            body[7].IPRelativeMemoryAddress != body[1].IPRelativeMemoryAddress ||
            !RipPointerLoad(body[8], NativeRegister.RAX) ||
            body[8].IPRelativeMemoryAddress != body[5].IPRelativeMemoryAddress ||
            !StaticFieldsPointer(body[9]) ||
            !DwordIncrement(body[10]) ||
            !RipPointerLoad(body[11], NativeRegister.RAX) ||
            body[11].IPRelativeMemoryAddress != body[3].IPRelativeMemoryAddress ||
            !StaticFieldsPointer(body[12]) ||
            !DwordStore(body[13]) ||
            !RipPointerLoad(body[14], NativeRegister.RAX) ||
            body[14].IPRelativeMemoryAddress != body[3].IPRelativeMemoryAddress ||
            !StaticFieldsPointer(body[15]) ||
            !DwordStore(body[16]) ||
            body[10].MemoryDisplacement64 > int.MaxValue ||
            body[13].MemoryDisplacement64 > int.MaxValue ||
            body[16].MemoryDisplacement64 > int.MaxValue ||
            body[16].Immediate32 != 0x8000_0000 ||
            !Stack(body[17], Mnemonic.Add) ||
            body[18].Code != Code.Retnq || body[18].OpCount != 0)
            return null;

        return new Shape(body[1].IPRelativeMemoryAddress,
            body[3].IPRelativeMemoryAddress, body[5].IPRelativeMemoryAddress,
            body[4].NearBranchTarget, body[10].MemoryDisplacement64,
            body[13].MemoryDisplacement64, body[16].MemoryDisplacement64,
            unchecked((int)body[13].Immediate32), body[16].Immediate32);
    }

    private static bool OrdinaryConstructor(MethodAnalysisContext method) =>
        method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Name == ".cctor" && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.Parameters.Count == 0 &&
        method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

    private static bool OrdinaryStruct(TypeAnalysisContext owner)
    {
        if (!owner.IsValueType || owner.IsEnumType || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            (owner.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Definition is not { GenericContainer: null, HasCctor: true,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0 } })
            return false;
        var instance = owner.Fields.Where(field => !field.IsStatic)
            .OrderBy(field => field.Offset).ToArray();
        return instance is [{ Offset: 0 }, { Offset: 4 }] &&
            TypeSizes.UnboxedSize(owner, 8) == 8 &&
            instance.All(field => UnchangedField(field,
                owner.AppContext.SystemTypes.SystemSingleType, Il2CppTypeEnum.IL2CPP_TYPE_R4, false));
    }

    private static bool OrdinaryWitness(TypeAnalysisContext witness, TypeAnalysisContext owner) =>
        !ReferenceEquals(witness, owner) &&
        ReferenceEquals(witness.DeclaringAssembly, owner.DeclaringAssembly) &&
        !witness.IsValueType && !witness.IsInterface && !witness.IsGenericInstance &&
        witness.GenericParameters.Count == 0 &&
        witness.Name == witness.DefaultName && witness.Namespace == witness.DefaultNamespace &&
        witness.Attributes == witness.DefaultAttributes &&
        ReferenceEquals(witness.BaseType, witness.DefaultBaseType) &&
        witness.Definition is { GenericContainer: null, HasCctor: false,
            PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        witness.Methods.All(method => method.Name != ".cctor") &&
        (witness.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout;

    private static bool UnchangedField(FieldAnalysisContext field, TypeAnalysisContext type,
        Il2CppTypeEnum rawType, bool isStatic = true) =>
        field.IsStatic == isStatic &&
        field.Name == field.DefaultName &&
        field.Offset == field.DefaultOffset && field.Offset >= 0 &&
        field.Attributes == field.DefaultAttributes &&
        field.OverrideFieldType == null && !field.UseOverrideConstantValue &&
        field.RawIl2CppCustomAttributeData.Length == 0 &&
        field.CustomAttributes is not { Count: > 0 } &&
        ReferenceEquals(field.FieldType, type) &&
        field.BackingData?.Field.RawFieldType is
            { NumMods: 0, Byref: 0, Pinned: 0 } raw && raw.Type == rawType &&
        (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA |
                             FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) == 0 &&
        field.StaticArrayInitialValue.Length == 0;

    private static bool Disjoint(ulong first, ulong firstLength, ulong second, ulong secondLength) =>
        first <= ulong.MaxValue - firstLength && second <= ulong.MaxValue - secondLength &&
        (first + firstLength <= second || second + secondLength <= first);

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool RipByteComparison(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipByteStoreOne(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && RipMemory(instruction, 1, 0);

    private static bool RipPointerLoad(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && RipMemory(instruction, 1, 8);

    private static bool RipMemory(NativeInstruction instruction, int operand, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
        (width == 0 || instruction.MemorySize.GetSize() == width);

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool StaticFieldsPointer(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RCX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 8 &&
        instruction.MemoryDisplacement64 == (ulong)Il2CppClassLayout.StaticFieldsOffset64;

    private static bool DwordIncrement(NativeInstruction instruction) =>
        instruction.Code == Code.Inc_rm32 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 4;

    private static bool DwordStore(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_rm32_imm32 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 4 && instruction.Op1Kind == OpKind.Immediate32;
}
