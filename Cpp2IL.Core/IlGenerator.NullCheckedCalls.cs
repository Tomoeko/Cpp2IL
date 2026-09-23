using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void ValidateCallSemantics(MethodAnalysisContext context)
    {
        foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode == OpCode.RuntimeNullThrow)
                throw new DecompilerException("Uncoalesced target runtime null guard has no faithful standalone managed emission");
            if (instruction.CallSemantics == CallSemantics.Direct)
                continue;
            if (instruction.CallSemantics != CallSemantics.NullCheckedInstance || !instruction.IsCall ||
                !NullCheckedCall.TryGet(instruction, out var target, out _) ||
                !ReferenceEquals(target.AppContext, context.AppContext))
                throw new DecompilerException("Null-checked invocation marker requires a bound nonvirtual reference-instance call with unchanged signature and pure arguments");
        }
        foreach (var fieldRead in context.NullCheckedFieldReads)
            if (!fieldRead.IsValidFor(context))
                throw new DecompilerException("Null-checked field read marker requires its unchanged instance field and receiver");
    }

    private static void ValidateNullCheckedParameterTypes(Instruction call, EmissionLocals locals)
    {
        foreach (var local in OperandEffects.ReadLocals(call))
        {
            if (locals.ParameterContexts.TryGetValue(local, out var parameter) &&
                (parameter.IsRef || !ReferenceEquals(parameter.ParameterType, local.Type) ||
                 !ReferenceEquals(parameter.DefaultParameterType, local.Type)))
                throw new DecompilerException("Null-checked invocation cannot reinterpret a changed or by-reference managed parameter");
            if (local.IsThis && !ReferenceEquals(local.Type, locals.Context.DeclaringType))
                throw new DecompilerException("Null-checked invocation cannot reinterpret the current instance parameter");
        }
    }
}
