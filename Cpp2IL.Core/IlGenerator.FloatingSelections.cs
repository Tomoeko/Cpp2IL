using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitFloatingProjection(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatProjection.TryGet(instruction, out var projection))
            throw new DecompilerException("Floating projection requires typed local operands, positive zero and explicit width");
        var expectedType = projection.ResultType(locals.Context.AppContext.SystemTypes);
        var destination = (LocalVariable)instruction.Operands[0];
        ValidateType(destination);
        if (instruction.Operands[1] is LocalVariable source)
        {
            ValidateType(source);
            LoadLocal(source, method, locals);
            method.CilMethodBody!.Instructions.Add(projection.Width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
        }
        else
            LoadOperand(instruction.Operands[1], method, locals);
        StoreToOperand(destination, method, locals);
        return;

        void ValidateType(LocalVariable operand)
        {
            if (!ReferenceEquals(operand.Type, expectedType) ||
                locals.ParameterContexts.TryGetValue(operand, out var parameter) &&
                (parameter.IsRef || !ReferenceEquals(parameter.ParameterType, expectedType) ||
                 !ReferenceEquals(parameter.DefaultParameterType, expectedType)))
                throw new DecompilerException("Floating projection operand type does not match its explicit native width");
        }
    }

    private static void EmitFloatingSelection(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatSelection.TryGet(instruction, out var selection))
            throw new DecompilerException("Floating selection requires typed local operands and explicit width and selection mode");
        var destination = (LocalVariable)instruction.Operands[0];
        var left = (LocalVariable)instruction.Operands[1];
        var right = (LocalVariable)instruction.Operands[2];
        var expectedType = selection.ResultType(locals.Context.AppContext.SystemTypes);
        foreach (var operand in new[] { destination, left, right })
            if (!ReferenceEquals(operand.Type, expectedType) ||
                locals.ParameterContexts.TryGetValue(operand, out var parameter) &&
                (parameter.IsRef || !ReferenceEquals(parameter.ParameterType, expectedType) ||
                 !ReferenceEquals(parameter.DefaultParameterType, expectedType)))
                throw new DecompilerException("Floating selection operand type does not match its explicit native width");

        var body = method.CilMethodBody!;
        var code = body.Instructions;
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var snapshotType = selection.Width == 32 ? factory.Single : factory.Double;
        var leftSnapshot = new CilLocalVariable(snapshotType);
        var rightSnapshot = new CilLocalVariable(snapshotType);
        body.LocalVariables.Add(leftSnapshot);
        body.LocalVariables.Add(rightSnapshot);
        Capture(left, leftSnapshot);
        Capture(right, rightSnapshot);

        var chooseLeft = new CilInstruction(CilOpCodes.Ldloc, leftSnapshot);
        var finish = new CilInstruction(CilOpCodes.Nop);
        code.Add(CilOpCodes.Ldloc, leftSnapshot);
        code.Add(CilOpCodes.Ldloc, rightSnapshot);
        code.Add(selection.Maximum ? CilOpCodes.Cgt : CilOpCodes.Clt);
        code.Add(CilOpCodes.Brtrue, chooseLeft.CreateLabel());
        // Ordered comparison is false for equal and unordered inputs. Load the
        // original second input, including its zero sign and quiet-NaN payload.
        code.Add(CilOpCodes.Ldloc, rightSnapshot);
        code.Add(CilOpCodes.Br, finish.CreateLabel());
        code.Add(chooseLeft);
        code.Add(finish);
        StoreToOperand(destination, method, locals);
        return;

        void Capture(LocalVariable operand, CilLocalVariable snapshot)
        {
            LoadLocal(operand, method, locals);
            code.Add(selection.Width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
            code.Add(CilOpCodes.Stloc, snapshot);
        }
    }
}
