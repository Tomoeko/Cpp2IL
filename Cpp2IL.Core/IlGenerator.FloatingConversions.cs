using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void EmitFloatingConversion(Instruction instruction, MethodDefinition method, EmissionLocals locals)
    {
        if (!FloatConversion.TryGet(instruction, out var conversion))
            throw new DecompilerException("Floating conversion requires scalar local operands and different explicit 32/64-bit widths");
        var destination = (LocalVariable)instruction.Operands[0];
        var source = (LocalVariable)instruction.Operands[1];
        var types = locals.Context.AppContext.SystemTypes;
        if (!HasExactType(source, conversion.SourceType(types)) ||
            !HasExactType(destination, conversion.ResultType(types)))
            throw new DecompilerException("Floating conversion operand type does not match its explicit native width");

        LoadLocal(source, method, locals);
        // Fix incoming binary32 precision before widening on CLI implementations
        // whose evaluation stack may retain a wider intermediate representation.
        if (conversion.SourceWidth == 32)
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Conv_R4);
        method.CilMethodBody!.Instructions.Add(conversion.ResultWidth == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8);
        StoreToOperand(destination, method, locals);

        bool HasExactType(LocalVariable local, TypeAnalysisContext expected) =>
            ReferenceEquals(local.Type, expected) &&
            (!locals.ParameterContexts.TryGetValue(local, out var parameter) ||
                !parameter.IsRef && ReferenceEquals(parameter.ParameterType, expected) &&
                ReferenceEquals(parameter.DefaultParameterType, expected));
    }
}
