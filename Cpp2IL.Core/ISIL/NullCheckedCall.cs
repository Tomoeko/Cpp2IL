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
            !ReferenceEquals(candidate.ReturnType, candidate.DefaultReturnType) ||
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
            !ReferenceEquals(value.Type, owner))
            return false;
        if (instruction.OpCode == OpCode.Call &&
            (instruction.Operands[1] is not LocalVariable result || candidate.IsVoid ||
             !ReferenceEquals(result.Type, candidate.ReturnType)))
            return false;

        for (var index = 0; index < candidate.Parameters.Count; index++)
        {
            var parameter = candidate.Parameters[index];
            if (parameter.ParameterIndex != index || !ReferenceEquals(parameter.DeclaringMethod, candidate) ||
                parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
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

    private static bool IsOrdinaryValue(TypeAnalysisContext type) => IsReferenceClass(type) || IsNumeric(type);

    public static bool CanLoadWithoutEffects(IOperand operand, TypeAnalysisContext expected) => operand switch
    {
        LocalVariable local => ReferenceEquals(local.Type, expected) && IsOrdinaryValue(expected),
        Immediate number => IsInteger(expected) || number.Value == 0 && IsReferenceClass(expected),
        FloatLiteral => ReferenceEquals(expected, expected.AppContext.SystemTypes.SystemSingleType),
        DoubleLiteral => ReferenceEquals(expected, expected.AppContext.SystemTypes.SystemDoubleType),
        _ => false,
    };

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
