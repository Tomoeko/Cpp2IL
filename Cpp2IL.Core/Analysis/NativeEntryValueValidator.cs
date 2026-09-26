using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

internal static class NativeEntryValueValidator
{
    internal const string EvidenceKey = "NativeEntryValueValidation";
    internal sealed record Result(int UnprovedValueCount);

    internal static void Record(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext))
            return;
        var unproved = FindUnprovedValues(method.ControlFlowGraph!, method.ParameterLocals);
        method.PutExtraData(EvidenceKey, new Result(unproved.Count));
    }

    // Run while definitions still have SSA versions. A version -1 read denotes
    // an incoming machine value, which must be a declared parameter. Native
    // frame storage reached by address has separate initialization/alias proofs.
    internal static IReadOnlyList<LocalVariable> FindUnprovedValues(ISILControlFlowGraph graph,
        ICollection<LocalVariable> parameters)
    {
        var instructions = graph.Instructions;
        var storage = OperandEffects.LocalsWithMutableStorage(instructions);
        return instructions.SelectMany(OperandEffects.ReadLocals).Distinct()
            .Where(local => local.Register.Version == -1 &&
                !parameters.Contains(local) && !storage.Contains(local)).ToArray();
    }
}
