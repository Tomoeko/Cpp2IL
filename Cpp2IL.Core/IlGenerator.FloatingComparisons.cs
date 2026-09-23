using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitFloatingComparison(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (instruction.IntegerBitWidth != 0 || instruction.Operands is not
            [LocalVariable destination, LocalVariable left, LocalVariable right,
                Immediate { Value: 32 or 64 } width, Immediate { Value: >= 0 and <= 15 } mask])
            throw new DecompilerException("Floating comparison requires typed local operands and explicit 32/64-bit width and outcome mask");

        var types = locals.Context.AppContext.SystemTypes;
        var expectedType = width.Value == 32 ? types.SystemSingleType : types.SystemDoubleType;
        if (!ReferenceEquals(destination.Type, types.SystemBooleanType))
            throw new DecompilerException("Floating comparison destination must be Boolean");
        if (!HasExactFloatingType(left) || !HasExactFloatingType(right))
            throw new DecompilerException("Floating comparison operand type does not match its explicit native width");

        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var factory = method.DeclaringModule!.CorLibTypeFactory;
        var snapshotType = width.Value == 32 ? factory.Single : factory.Double;
        var leftSnapshot = new CilLocalVariable(snapshotType);
        var rightSnapshot = new CilLocalVariable(snapshotType);
        body.LocalVariables.Add(leftSnapshot);
        body.LocalVariables.Add(rightSnapshot);

        // Evaluate each input once before deriving multiple Boolean terms. The conversion fixes
        // storage precision even on a CLI runtime whose evaluation stack uses wider floating values.
        Capture(left, leftSnapshot);
        Capture(right, rightSnapshot);

        // Floating .un means unordered, not an unsigned reinterpretation of the IEEE bit pattern.
        // Keep all four outcomes distinct; complementing an ordered relation includes NaN.
        switch (mask.Value)
        {
            case 0: instructions.Add(CilOpCodes.Ldc_I4_0); break;
            case 1: Compare(CilOpCodes.Clt); break;
            case 2: Compare(CilOpCodes.Ceq); break;
            case 3: Compare(CilOpCodes.Cgt_Un); Negate(); break;
            case 4: Compare(CilOpCodes.Cgt); break;
            case 5: Compare(CilOpCodes.Clt); Compare(CilOpCodes.Cgt); instructions.Add(CilOpCodes.Or); break;
            case 6: Compare(CilOpCodes.Clt_Un); Negate(); break;
            case 7: Unordered(); Negate(); break;
            case 8: Unordered(); break;
            case 9: Compare(CilOpCodes.Clt_Un); break;
            case 10: Compare(CilOpCodes.Clt); Compare(CilOpCodes.Cgt); instructions.Add(CilOpCodes.Or); Negate(); break;
            case 11: Compare(CilOpCodes.Cgt); Negate(); break;
            case 12: Compare(CilOpCodes.Cgt_Un); break;
            case 13: Compare(CilOpCodes.Ceq); Negate(); break;
            case 14: Compare(CilOpCodes.Clt); Negate(); break;
            case 15: instructions.Add(CilOpCodes.Ldc_I4_1); break;
        }
        StoreToOperand(destination, method, locals);
        return;

        bool HasExactFloatingType(LocalVariable operand) => ReferenceEquals(operand.Type, expectedType) &&
            (!locals.ParameterContexts.TryGetValue(operand, out var parameter) ||
             !parameter.IsRef && ReferenceEquals(parameter.ParameterType, expectedType) &&
             ReferenceEquals(parameter.DefaultParameterType, expectedType));

        void Capture(LocalVariable operand, CilLocalVariable snapshot)
        {
            LoadLocal(operand, method, locals);
            instructions.Add(width.Value == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
            instructions.Add(CilOpCodes.Stloc, snapshot);
        }

        void Compare(CilOpCode opcode)
        {
            instructions.Add(CilOpCodes.Ldloc, leftSnapshot);
            instructions.Add(CilOpCodes.Ldloc, rightSnapshot);
            instructions.Add(opcode);
        }

        void Negate()
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Ceq);
        }

        void Unordered()
        {
            Compare(CilOpCodes.Clt_Un);
            Compare(CilOpCodes.Cgt_Un);
            instructions.Add(CilOpCodes.And);
        }
    }
}
