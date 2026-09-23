using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitIntegerExtension(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!IntegerExtension.TryGet(instruction, out var extension) ||
            !extension.HasCanonicalTypes(instruction, locals.Context.AppContext))
            throw new DecompilerException("Integer extension requires canonical integer locals, explicit source width 8/16/32, result width 32/64 and sign flag 0/1");

        var destination = (LocalVariable)instruction.Operands[0];
        var source = (LocalVariable)instruction.Operands[1];
        if (!HasUnchangedParameterType(source) || !HasUnchangedParameterType(destination))
            throw new DecompilerException("Integer extension cannot reinterpret a changed or by-reference managed parameter");

        var instructions = method.CilMethodBody!.Instructions;
        LoadLocal(source, method, locals);
        // First discard high bits. A widening conversion alone would retain bits outside the
        // stated source width, and using conv.i8 for a zero-filled 32-bit result would sign-fill.
        instructions.Add((extension.SourceBits, extension.Signed) switch
        {
            (8, true) => CilOpCodes.Conv_I1,
            (8, false) => CilOpCodes.Conv_U1,
            (16, true) => CilOpCodes.Conv_I2,
            (16, false) => CilOpCodes.Conv_U2,
            (32, true) => CilOpCodes.Conv_I4,
            _ => CilOpCodes.Conv_U4,
        });
        if (extension.ResultBits == 64)
            instructions.Add(extension.Signed ? CilOpCodes.Conv_I8 : CilOpCodes.Conv_U8);
        StoreToOperand(destination, method, locals);
        return;

        bool HasUnchangedParameterType(LocalVariable operand) =>
            !locals.ParameterContexts.TryGetValue(operand, out var parameter) ||
            !parameter.IsRef && ReferenceEquals(parameter.ParameterType, operand.Type) &&
            ReferenceEquals(parameter.DefaultParameterType, operand.Type);
    }
}
