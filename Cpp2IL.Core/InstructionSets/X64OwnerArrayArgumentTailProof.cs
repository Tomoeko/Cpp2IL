using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves an owner call with one reference argument loaded through its checked
/// reference array. The owner remains in RCX; the element field supplies RDX.
/// Both terminal helpers and the direct managed tail target are authenticated.
/// </summary>
internal static class X64OwnerArrayArgumentTailProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext ArgumentField, MethodAnalysisContext Target);

    internal sealed record Shape(int ArrayOffset, int ArgumentOffset,
        ulong TargetAddress);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings is not [var bound] ||
                !ReferenceEquals(bound, method) ||
                X64Stack28BodyProof.Read(method, 17, 80) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[14].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[16].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[14].IP, body[16].IP }) != null)
                return null;
            return Bind(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static Evidence? Bind(MethodAnalysisContext method, Shape shape)
    {
        if (!OrdinaryCaller(method))
            return null;
        var owner = method.DeclaringType!;
        var arrayFields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ArrayOffset).ToArray();
        if (arrayFields is not [{ } arrayField] ||
            arrayField.Name != arrayField.DefaultName ||
            arrayField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawArray ||
            arrayField.FieldType is not SzArrayTypeAnalysisContext
                { ElementType: var element } ||
            !OrdinaryClass(element) ||
            rawArray.GetEncapsulatedType() is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawElement ||
            !ReferenceEquals(rawElement.AsClass(), element.Definition) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(arrayField,
                    new LocalVariable("proved-owner",
                        new ManagedRegister(null, "rcx"), owner),
                    shape.ArrayOffset)))
            return null;

        var argumentFields = element.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ArgumentOffset).ToArray();
        if (argumentFields is not [{ } argumentField] ||
            argumentField.Name != argumentField.DefaultName ||
            argumentField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawArgument ||
            !OrdinaryClass(argumentField.FieldType) ||
            !ReferenceEquals(rawArgument.AsClass(),
                argumentField.FieldType.Definition) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(argumentField,
                    new LocalVariable("proved-element",
                        new ManagedRegister(null, "rdx"), element),
                    shape.ArgumentOffset)))
            return null;

        if (!method.AppContext.MethodsByAddress.TryGetValue(shape.TargetAddress,
                out var targets) || targets is not [var target] ||
            !OrdinaryTarget(target, owner, argumentField.FieldType) ||
            target.UnderlyingPointer != shape.TargetAddress)
            return null;
        return new Evidence(arrayField, argumentField, target);
    }

    internal static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null, HasCctor: false,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 },
            PackingSizeIsDefault: true, ClassSizeIsDefault: true } &&
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName &&
        type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        type.GenericParameters.Count == 0 &&
        !type.IsValueType && !type.IsInterface &&
        type is not GenericInstanceTypeAnalysisContext;

    internal static bool OrdinaryTarget(MethodAnalysisContext method,
        TypeAnalysisContext owner, TypeAnalysisContext valueType)
    {
        if (!ReferenceEquals(method.DeclaringType, owner) ||
            !OrdinaryMethod(method, owner) ||
            method.Definition is not { parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [{ } rawParameter] ||
            rawParameter.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(rawParameter.RawType.AsClass(), valueType.Definition) ||
            method.Parameters is not [{ } parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            !ReferenceEquals(parameter.ParameterType, valueType))
            return false;
        return true;
    }

    private static bool OrdinaryCaller(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { } owner || !OrdinaryClass(owner) ||
            !OrdinaryMethod(method, owner) ||
            method.Definition is not { parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [{ } rawParameter] ||
            rawParameter.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Parameters is not [{ } parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            !ReferenceEquals(parameter.ParameterType,
                method.AppContext.SystemTypes.SystemInt32Type))
            return false;
        return true;
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        TypeAnalysisContext owner) =>
        method.UnderlyingPointer is not (0 or ulong.MaxValue) &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName &&
        method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract |
            MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
            MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding: false) &&
        method.Definition is { GenericContainer: null,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        !owner.Properties.Any(property =>
            ReferenceEquals(property.Getter, method) ||
            ReferenceEquals(property.Setter, method));

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        PE pe)
    {
        if (body.Count != 17 || pe.PointerSizeBytes != 8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !FieldLoad(body[1], NativeRegister.R8, NativeRegister.RCX,
                out var arrayOffset) ||
            !RegisterPair(body[2], Code.Test_rm64_r64,
                NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[3], Code.Je_rel8_64, body[14].IP) ||
            !ArrayLengthCompare(body[4], pe) ||
            !Branch(body[5], Code.Jae_rel8_64, body[16].IP) ||
            !RegisterPair(body[6], Code.Movsxd_r64_rm32,
                NativeRegister.RAX, NativeRegister.EDX) ||
            !ArrayElementLoad(body[7], pe) ||
            !RegisterPair(body[8], Code.Test_rm64_r64,
                NativeRegister.RDX, NativeRegister.RDX) ||
            !Branch(body[9], Code.Je_rel8_64, body[14].IP) ||
            !FieldLoad(body[10], NativeRegister.RDX, NativeRegister.RDX,
                out var argumentOffset) ||
            !RegisterPair(body[11], Code.Xor_r32_rm32,
                NativeRegister.R8D, NativeRegister.R8D) ||
            !X64Stack28BodyProof.Stack(body[12], Mnemonic.Add) ||
            body[13].Code != Code.Jmp_rel32_64 ||
            body[13].Op0Kind != OpKind.NearBranch64 ||
            body[13].NearBranchTarget == 0 ||
            body[14].Code != Code.Call_rel32_64 ||
            body[14].Op0Kind != OpKind.NearBranch64 ||
            body[15].Code != Code.Int3 || body[15].Length != 1 ||
            body[16].Code != Code.Call_rel32_64 ||
            body[16].Op0Kind != OpKind.NearBranch64 ||
            arrayOffset is < 16 or > 0x1000 - 8 ||
            argumentOffset is < 16 or > 0x1000 - 8 ||
            arrayOffset % 8 != 0 || argumentOffset % 8 != 0)
            return null;
        return new Shape(arrayOffset, argumentOffset,
            body[13].NearBranchTarget);
    }

    private static bool FieldLoad(NativeInstruction instruction,
        NativeRegister result, NativeRegister source, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != result ||
            instruction.Op1Kind != OpKind.Memory ||
            instruction.MemoryBase != source ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != 8 ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool ArrayLengthCompare(NativeInstruction instruction,
        PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.R8 &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe);

    private static bool ArrayElementLoad(NativeInstruction instruction,
        PE pe) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RDX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.R8 &&
        instruction.MemoryIndex == NativeRegister.RAX &&
        instruction.MemoryIndexScale == 8 &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.MemoryDisplacement64 ==
            Il2CppArrayUtils.GetFirstItemOffset(pe);

    private static bool RegisterPair(NativeInstruction instruction, Code code,
        NativeRegister first, NativeRegister second) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == first &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == second;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;
}
