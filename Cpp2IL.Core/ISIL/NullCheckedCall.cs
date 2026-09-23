using System;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.ISIL;

/// <summary>Shared callvirt eligibility for the bounded runtime-null-guard transformation.</summary>
internal static class NullCheckedCall
{
    public static bool TryGet(Instruction instruction, out MethodAnalysisContext target, out LocalVariable receiver)
    {
        target = null!;
        receiver = null!;
        if (!instruction.IsCall || instruction.IntegerBitWidth != 0 || instruction.Operands.Count == 0 ||
            instruction.Operands[0] is not MethodAnalysisContext candidate || candidate.IsStatic || candidate.IsVirtual ||
            candidate.Name is ".ctor" or ".cctor" || candidate.Name != candidate.DefaultName ||
            candidate.Attributes != candidate.DefaultAttributes || candidate.ImplAttributes != candidate.DefaultImplAttributes ||
            (candidate.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (candidate.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            candidate.GenericParameters.Count != 0 || candidate.Definition?.GenericContainer != null ||
            candidate.DeclaringType is not { } owner || !IsReferenceClass(owner) ||
            candidate.OverrideReturnType != null ||
            (!candidate.IsVoid && !IsOrdinaryValue(candidate.ReturnType)))
            return false;

        var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;
        var expected = receiverIndex + 1 + candidate.Parameters.Count;
        // Native managed calls also carry MethodInfo. Only a proved null metadata argument is
        // accepted here; later CallArgumentTrimmer removes it from the managed invocation.
        if (instruction.Operands.Count != expected &&
            !(instruction.Operands.Count == expected + 1 && instruction.Operands[expected] is Immediate { Value: 0 }))
            return false;
        if (instruction.Operands[receiverIndex] is not LocalVariable value ||
            !HasUnchangedReferenceBase(value.Type, owner))
            return false;
        if (instruction.OpCode == OpCode.Call &&
            (instruction.Operands[1] is not LocalVariable result || candidate.IsVoid ||
            !SameOrdinaryType(result.Type, candidate.ReturnType)))
            return false;

        for (var index = 0; index < candidate.Parameters.Count; index++)
        {
            var parameter = candidate.Parameters[index];
            if (parameter.ParameterIndex != index || !ReferenceEquals(parameter.DeclaringMethod, candidate) ||
                parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                !IsOrdinaryValue(parameter.ParameterType) ||
                !CanLoadWithoutEffects(instruction.Operands[receiverIndex + 1 + index], parameter.ParameterType))
                return false;
        }

        target = candidate;
        receiver = value;
        return true;
    }

    public static bool IsReferenceClass(TypeAnalysisContext type) =>
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance && type.GenericParameters.Count == 0 &&
        (type.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING) &&
        type.Attributes == type.DefaultAttributes && ReferenceEquals(type.BaseType, type.DefaultBaseType);

    private static bool HasUnchangedReferenceBase(TypeAnalysisContext? receiver, TypeAnalysisContext owner)
    {
        // A nonvirtual call to a base method still uses the derived receiver's null check.
        // Follow only original, ordinary class inheritance; changed bases or cycles are not evidence.
        var seen = new System.Collections.Generic.HashSet<TypeAnalysisContext>();
        for (var type = receiver; type != null && seen.Add(type); type = type.BaseType)
        {
            if (!IsReferenceClass(type))
                return false;
            if (ReferenceEquals(type, owner))
                return true;
        }
        return false;
    }

    private static bool IsOrdinaryValue(TypeAnalysisContext type) =>
        IsReferenceClass(type) || IsBoundedArrayReference(type) || IsNumeric(type);

    public static bool CanLoadWithoutEffects(IOperand operand, TypeAnalysisContext expected) => operand switch
    {
        LocalVariable local => SameOrdinaryType(local.Type, expected) && IsOrdinaryValue(expected),
        Immediate number => IsInteger(expected) || number.Value == 0 &&
            (IsReferenceClass(expected) || IsBoundedArrayReference(expected)),
        FloatLiteral => ReferenceEquals(expected, expected.AppContext.SystemTypes.SystemSingleType),
        DoubleLiteral => ReferenceEquals(expected, expected.AppContext.SystemTypes.SystemDoubleType),
        _ => false,
    };

    internal static bool SameOrdinaryType(TypeAnalysisContext? left, TypeAnalysisContext? right) =>
        ReferenceEquals(left, right) ||
        left is SzArrayTypeAnalysisContext leftArray && right is SzArrayTypeAnalysisContext rightArray &&
        IsBoundedArrayReference(leftArray) && IsBoundedArrayReference(rightArray) &&
        ReferenceEquals(leftArray.ElementType, rightArray.ElementType);

    private static bool IsBoundedArrayReference(TypeAnalysisContext type) =>
        type is SzArrayTypeAnalysisContext array &&
        !array.ElementType.IsGenericInstance && array.ElementType.GenericParameters.Count == 0 &&
        array.ElementType.Type is
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
            Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
            Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
            Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8 or
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
            Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;

    private static bool IsNumeric(TypeAnalysisContext type) => IsInteger(type) ||
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemSingleType) ||
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemDoubleType);

    private static bool IsInteger(TypeAnalysisContext type)
    {
        var types = type.AppContext.SystemTypes;
        return ReferenceEquals(type, types.SystemBooleanType) || ReferenceEquals(type, types.SystemCharType) ||
               ReferenceEquals(type, types.SystemSByteType) || ReferenceEquals(type, types.SystemByteType) ||
               ReferenceEquals(type, types.SystemInt16Type) || ReferenceEquals(type, types.SystemUInt16Type) ||
               ReferenceEquals(type, types.SystemInt32Type) || ReferenceEquals(type, types.SystemUInt32Type) ||
               ReferenceEquals(type, types.SystemInt64Type) || ReferenceEquals(type, types.SystemUInt64Type);
    }
}
