using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitFloatingTruncation(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatTruncation.TryGet(instruction, out var conversion) ||
            !conversion.HasCanonicalTypes(instruction, locals.Context.AppContext.SystemTypes))
            throw new DecompilerException("Floating truncation requires canonical Double and signed Int32/Int64 locals, source width 64 and matching result width 32/64");

        var destination = (LocalVariable)instruction.Operands[0];
        var source = (LocalVariable)instruction.Operands[1];
        if (!HasUnchangedParameterType(source) || !HasUnchangedParameterType(destination))
            throw new DecompilerException("Floating truncation cannot reinterpret a changed or by-reference managed parameter");

        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var snapshot = new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.Double);
        body.LocalVariables.Add(snapshot);
        LoadLocal(source, method, locals);
        instructions.Add(CilOpCodes.Conv_R8);
        instructions.Add(CilOpCodes.Stloc, snapshot);

        var fallback = conversion.ResultBits == 32
            ? new CilInstruction(CilOpCodes.Ldc_I4, int.MinValue)
            : new CilInstruction(CilOpCodes.Ldc_I8, long.MinValue);
        var joined = new CilInstruction(CilOpCodes.Nop);
        // These powers of two are exact binary64 values. In particular, casting long.MaxValue
        // to double rounds upward and must not be used as an inclusive conversion bound.
        var limit = conversion.ResultBits == 32 ? 2147483648d : 9223372036854775808d;
        instructions.Add(CilOpCodes.Ldloc, snapshot);
        instructions.Add(CilOpCodes.Ldc_R8, -limit);
        instructions.Add(CilOpCodes.Blt_Un, new CilInstructionLabel(fallback));
        instructions.Add(CilOpCodes.Ldloc, snapshot);
        instructions.Add(CilOpCodes.Ldc_R8, limit);
        instructions.Add(CilOpCodes.Bge_Un, new CilInstructionLabel(fallback));
        instructions.Add(CilOpCodes.Ldloc, snapshot);
        instructions.Add(conversion.ResultBits == 32 ? CilOpCodes.Conv_I4 : CilOpCodes.Conv_I8);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(joined));
        instructions.Add(fallback);
        instructions.Add(joined);
        StoreToOperand(destination, method, locals);
        return;

        // Floating .un branches include NaN. The fallback also covers fractions just below
        // Int32.MinValue whose truncation is MIN; this models result bits, not MXCSR status.
        // conv.i4/i8 only see the safe interval, avoiding runtime-specific invalid conversions.
        bool HasUnchangedParameterType(LocalVariable operand) =>
            !locals.ParameterContexts.TryGetValue(operand, out var parameter) ||
            !parameter.IsRef && ReferenceEquals(parameter.ParameterType, operand.Type) &&
            ReferenceEquals(parameter.DefaultParameterType, operand.Type);
    }
}
