using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    /// <summary>
    /// A native pointer-sized zero test of an unchanged managed reference parameter has the
    /// same truth value as a managed reference/null comparison. It must not enter
    /// integer arithmetic emission: a class or array has no integer stack width.
    /// </summary>
    private static bool TryEmitReferenceNullComparison(ManagedInstruction comparison,
        MethodDefinition method, EmissionLocals locals)
    {
        var context = locals.Context;
        if (!IsReferenceNullComparisonShape(comparison,
                context.AppContext.SystemTypes.SystemBooleanType, out var reference) ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(context.AppContext) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(context,
                requireUniqueBinding: false) ||
            (!RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(context) &&
             !X86ReferenceNullReturnProof.IsApplicable(context,
                 X86Utils.Iterate(context).ToArray())) ||
            context.ControlFlowGraph is not { } graph ||
            !graph.Instructions.Contains(comparison) ||
            !locals.Parameters.ContainsKey(reference) ||
            !HasUnchangedParameterStorage(graph, reference) ||
            !UnchangedReferenceParameter(context, locals, reference, reference.Type!))
            return false;

        var instructions = method.CilMethodBody!.Instructions;
        LoadOperand(reference, method, locals);
        LoadOperand(new Immediate(0), method, locals, reference.Type);
        instructions.Add(CilOpCodes.Ceq);
        if (comparison.OpCode == OpCode.CheckNotEqual)
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Ceq);
        }
        StoreToOperand(comparison.Operands[0], method, locals);
        return true;
    }

    internal static bool IsReferenceNullComparisonShape(ManagedInstruction comparison,
        TypeAnalysisContext booleanType, out LocalVariable reference)
    {
        reference = null!;
        if (comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual,
                IntegerBitWidth: 64, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable { Type: { } resultType }, LocalVariable value,
                    Immediate { Value: 0 }] } ||
            !ReferenceEquals(resultType, booleanType) || value.IsMethodInfo ||
            value.Type is not { } referenceType || !IsUnchangedManagedReference(referenceType))
            return false;
        reference = value;
        return true;
    }

    internal static bool HasUnchangedParameterStorage(ISILControlFlowGraph graph,
        LocalVariable parameter) =>
        !graph.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, parameter)) &&
        !OperandEffects.LocalsWithMutableStorage(graph.Instructions).Contains(parameter);

    private static bool UnchangedReferenceParameter(MethodAnalysisContext context,
        EmissionLocals locals, LocalVariable local, TypeAnalysisContext type)
    {
        if (!ReferenceEquals(local.Type, type) || !IsUnchangedManagedReference(type))
            return false;
        if (local.IsThis)
            return false;
        return locals.ParameterContexts.TryGetValue(local, out var parameter) &&
               parameter.ParameterIndex >= 0 && parameter.ParameterIndex < context.Parameters.Count &&
               ReferenceEquals(context.Parameters[parameter.ParameterIndex], parameter) &&
               ReferenceEquals(parameter.DeclaringMethod, context) &&
               context.Definition?.InternalParameterData is { } nativeParameters &&
               parameter.ParameterIndex < nativeParameters.Length &&
               ReferenceEquals(nativeParameters[parameter.ParameterIndex], parameter.Definition) &&
               !parameter.IsRef &&
               parameter.OverrideParameterType == null &&
               NullCheckedCall.SameOrdinaryType(parameter.ParameterType, type) &&
               NullCheckedCall.SameOrdinaryType(parameter.DefaultParameterType, type) &&
               parameter.Attributes == parameter.DefaultAttributes &&
               parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool IsUnchangedManagedReference(TypeAnalysisContext type)
        => NullCheckedCall.IsReferenceClass(type) || NullCheckedCall.IsBoundedArrayReference(type);
}
