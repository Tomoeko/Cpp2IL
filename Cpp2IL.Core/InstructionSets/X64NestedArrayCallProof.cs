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
/// Proves the closed Release shape for a class field's reference-array element
/// followed by either a direct no-argument call or one more reference field
/// read and a no-argument call. The checked array access supplies the native
/// unsigned bounds failure, and callvirt supplies the final receiver failure.
/// </summary>
internal static class X64NestedArrayCallProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext? NestedField, MethodAnalysisContext Target);

    internal sealed record Shape(int ArrayOffset, int? NestedOffset,
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
                    out var bindings) || bindings.Count is < 1 or > 8 ||
                bindings.Count(binding => ReferenceEquals(binding, method)) != 1)
                return null;

            var nested = X64Stack28BodyProof.Read(method, 19, 80);
            var direct = nested == null
                ? X64Stack28BodyProof.Read(method, 16, 80) : null;
            var body = nested ?? direct;
            if (body == null ||
                TryProveShape(body, pe) is not { } shape ||
                !CheckHelpersAndExceptions(method, body, shape.NestedOffset != null))
                return null;

            Evidence? selected = null;
            foreach (var binding in bindings)
            {
                if (binding.UnderlyingPointer != method.UnderlyingPointer ||
                    !ReferenceEquals(binding.DeclaringType?.DeclaringAssembly,
                        method.DeclaringType?.DeclaringAssembly) ||
                    (!ReferenceEquals(binding, method) &&
                     (X64Stack28BodyProof.Read(binding, body.Length, 80) is not
                          { } aliasBody || !aliasBody.SequenceEqual(body))) ||
                    Bind(binding, shape) is not { } evidence)
                    return null;
                if (ReferenceEquals(binding, method))
                    selected = evidence;
            }
            return selected;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool CheckHelpersAndExceptions(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body, bool nested)
    {
        var nullCall = nested ? 16 : 13;
        var boundsCall = nested ? 18 : 15;
        return X86RuntimeNullThrowProof.TryIdentify(method.AppContext,
                   body[nullCall].NearBranchTarget) != null &&
               X86RuntimeBoundsThrowProof.TryIdentify(method.AppContext,
                   body[boundsCall].NearBranchTarget) &&
               X86CallerExceptionRegionProof.Check(method, body,
                   new HashSet<ulong> { body[nullCall].IP,
                       body[boundsCall].IP }) == null;
    }

    private static Evidence? Bind(MethodAnalysisContext method, Shape shape)
    {
        if (!OrdinaryCaller(method))
            return null;
        var owner = method.DeclaringType!;
        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ArrayOffset).ToArray();
        if (fields is not [{ } arrayField] ||
            arrayField.Name != arrayField.DefaultName ||
            arrayField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            arrayField.FieldType is not SzArrayTypeAnalysisContext
                { ElementType: var element } ||
            !OrdinaryClass(element) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(arrayField,
                    new LocalVariable("proved-owner",
                        new ManagedRegister(null, "rcx"), owner),
                    shape.ArrayOffset)))
            return null;

        FieldAnalysisContext? nestedField = null;
        var receiverType = element;
        if (shape.NestedOffset is int nestedOffset)
        {
            var nestedFields = element.Fields.Where(field => !field.IsStatic &&
                field.Offset == nestedOffset).ToArray();
            if (nestedFields is not [{ } field] ||
                field.Name != field.DefaultName ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !OrdinaryClass(field.FieldType) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(field,
                        new LocalVariable("proved-element",
                            new ManagedRegister(null, "rcx"), element),
                        nestedOffset)))
                return null;
            nestedField = field;
            receiverType = field.FieldType;
        }

        if (!method.AppContext.MethodsByAddress.TryGetValue(
                shape.TargetAddress, out var targets))
            return null;
        var candidates = targets.Where(target =>
            ReferenceEquals(target.DeclaringType, receiverType) &&
            OrdinaryTarget(target, receiverType)).ToArray();
        return candidates is [var selected] &&
               targets.Count(target => ReferenceEquals(target, selected)) == 1
            ? new Evidence(arrayField, nestedField, selected) : null;
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 },
            PackingSizeIsDefault: true, ClassSizeIsDefault: true } &&
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName &&
        type.Namespace == type.DefaultNamespace &&
        type.GenericParameters.Count == 0 &&
        !type.IsValueType && !type.IsInterface;

    private static bool OrdinaryCaller(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { } owner || !OrdinaryClass(owner) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            method.Definition is not { GenericContainer: null,
                parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [{ } rawParameter] ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
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
        return !owner.Properties.Any(property =>
            ReferenceEquals(property.Getter, method) ||
            ReferenceEquals(property.Setter, method));
    }

    private static bool OrdinaryTarget(MethodAnalysisContext method,
        TypeAnalysisContext owner)
    {
        if (method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            method.Definition is not { GenericContainer: null,
                parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(method.DeclaringType, owner) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Parameters.Count != 0 ||
            (definition.InternalParameterData?.Length ?? 0) != 0)
            return false;

        var receiver = new LocalVariable("proved-receiver",
            new ManagedRegister(null, "rcx"), owner);
        return NullCheckedCall.TryGet(new Cpp2IL.Core.ISIL.Instruction(0,
            Cpp2IL.Core.ISIL.OpCode.CallVoid,
            new List<IOperand> { method, receiver, new Immediate(0) }),
            out var target, out _) && ReferenceEquals(target, method);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body, PE pe)
    {
        if (body.Count is not (16 or 19) || pe.PointerSizeBytes != 8)
            return null;
        var nested = body.Count == 19;
        var tail = nested ? 15 : 12;
        var nullCall = nested ? 16 : 13;
        var boundsCall = nested ? 18 : 15;
        if (!X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !FieldLoad(body[1], NativeRegister.R8, NativeRegister.RCX,
                out var arrayOffset) ||
            !RegisterPair(body[2], Code.Test_rm64_r64,
                NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[3], Code.Je_rel8_64, body[nullCall].IP) ||
            !ArrayLengthCompare(body[4], pe) ||
            !Branch(body[5], Code.Jae_rel8_64, body[boundsCall].IP) ||
            !RegisterPair(body[6], Code.Movsxd_r64_rm32,
                NativeRegister.RAX, NativeRegister.EDX) ||
            !ArrayElementLoad(body[7], pe) ||
            !RegisterPair(body[8], Code.Test_rm64_r64,
                NativeRegister.RCX, NativeRegister.RCX) ||
            !Branch(body[9], Code.Je_rel8_64, body[nullCall].IP) ||
            !RegisterPair(body[nested ? 13 : 10], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !X64Stack28BodyProof.Stack(body[tail - 1], Mnemonic.Add) ||
            body[tail].Code != Code.Jmp_rel32_64 ||
            body[tail].Op0Kind != OpKind.NearBranch64 ||
            body[tail].NearBranchTarget == 0 ||
            body[nullCall].Code != Code.Call_rel32_64 ||
            body[nullCall].Op0Kind != OpKind.NearBranch64 ||
            body[nullCall + 1].Code != Code.Int3 ||
            body[boundsCall].Code != Code.Call_rel32_64 ||
            body[boundsCall].Op0Kind != OpKind.NearBranch64 ||
            arrayOffset is < 16 or > 0x1000 - 8 ||
            arrayOffset % 8 != 0)
            return null;

        int? nestedOffset = null;
        if (nested)
        {
            if (!FieldLoad(body[10], NativeRegister.RCX,
                    NativeRegister.RCX, out var offset) ||
                !RegisterPair(body[11], Code.Test_rm64_r64,
                    NativeRegister.RCX, NativeRegister.RCX) ||
                !Branch(body[12], Code.Je_rel8_64, body[nullCall].IP) ||
                offset is < 16 or > 0x1000 - 8 || offset % 8 != 0)
                return null;
            nestedOffset = offset;
        }
        return new Shape(arrayOffset, nestedOffset,
            body[tail].NearBranchTarget);
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
        instruction.Op0Register == NativeRegister.RCX &&
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
