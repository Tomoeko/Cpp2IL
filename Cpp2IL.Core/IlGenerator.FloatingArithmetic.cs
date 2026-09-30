using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitFloatingDivision(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatDivision.TryGet(instruction, out var division))
            throw new DecompilerException("Floating division requires scalar locals and explicit 32/64-bit precision");
        var destination = (LocalVariable)instruction.Operands[0];
        var left = (LocalVariable)instruction.Operands[1];
        var right = (LocalVariable)instruction.Operands[2];
        ValidateFloatingArithmeticTypes([destination, left, right], division.ResultType(locals.Context.AppContext.SystemTypes), locals);
        var code = method.CilMethodBody!.Instructions;
        var precision = division.Width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8;
        LoadLocal(left, method, locals);
        code.Add(precision);
        LoadLocal(right, method, locals);
        code.Add(precision);
        code.Add(CilOpCodes.Div);
        // Native DIVSS/DIVSD round at this operation, before any comparison,
        // subsequent arithmetic or return can observe an extended temporary.
        code.Add(precision);
        StoreToOperand(destination, method, locals);
    }

    private static void EmitFloatingNegativeSelection(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatNegativeSelection.TryGet(instruction, out var selection))
            throw new DecompilerException("Floating negative selection requires scalar locals and explicit 32/64-bit precision");
        var destination = (LocalVariable)instruction.Operands[0];
        var source = (LocalVariable)instruction.Operands[1];
        ValidateFloatingArithmeticTypes([destination, source], selection.ResultType(locals.Context.AppContext.SystemTypes), locals);
        var body = method.CilMethodBody!;
        var code = body.Instructions;
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var snapshot = new CilLocalVariable(selection.Width == 32 ? factory.Single : factory.Double);
        body.LocalVariables.Add(snapshot);
        LoadLocal(source, method, locals);
        code.Add(selection.Width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
        code.Add(CilOpCodes.Stloc, snapshot);
        code.Add(CilOpCodes.Ldloc, snapshot);
        if (selection.Width == 32) code.Add(CilOpCodes.Ldc_R4, 0f);
        else code.Add(CilOpCodes.Ldc_R8, 0d);
        code.Add(CilOpCodes.Clt);
        var unchanged = new CilInstruction(CilOpCodes.Ldloc, snapshot);
        var finish = new CilInstruction(CilOpCodes.Nop);
        code.Add(CilOpCodes.Brfalse, unchanged.CreateLabel());
        code.Add(CilOpCodes.Ldloc, snapshot);
        code.Add(CilOpCodes.Neg);
        code.Add(CilOpCodes.Br, finish.CreateLabel());
        code.Add(unchanged);
        code.Add(finish);
        StoreToOperand(destination, method, locals);
    }

    private static void ValidateFloatingArithmeticTypes(LocalVariable[] operands, TypeAnalysisContext expected, EmissionLocals locals)
    {
        foreach (var operand in operands)
            if (!ReferenceEquals(operand.Type, expected) ||
                locals.ParameterContexts.TryGetValue(operand, out var parameter) &&
                (parameter.IsRef || !ReferenceEquals(parameter.ParameterType, expected) ||
                 !ReferenceEquals(parameter.DefaultParameterType, expected)))
                throw new DecompilerException("Floating arithmetic operand type does not match its explicit native precision");
    }
}
