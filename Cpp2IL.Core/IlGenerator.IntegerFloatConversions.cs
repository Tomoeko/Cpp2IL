using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitIntegerFloatConversion(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!IntegerFloatConversion.HasCanonicalTypes(instruction, locals.Context.AppContext.SystemTypes))
            throw new DecompilerException("Int32-to-Single conversion requires canonical signed Int32 and Single locals");
        var source = (LocalVariable)instruction.Operands[1];
        var destination = (LocalVariable)instruction.Operands[0];
        foreach (var operand in new[] { source, destination })
            if (locals.ParameterContexts.TryGetValue(operand, out var parameter) &&
                (parameter.IsRef || !ReferenceEquals(parameter.ParameterType, operand.Type) ||
                 !ReferenceEquals(parameter.DefaultParameterType, operand.Type)))
                throw new DecompilerException("Int32-to-Single conversion cannot reinterpret a changed or by-reference parameter");

        LoadLocal(source, method, locals);
        // Keep the signed low32 value explicit before binary32 rounding. In
        // particular, do not widen or reinterpret it as an unsigned integer.
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Conv_I4);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Conv_R4);
        StoreToOperand(destination, method, locals);
    }
}
