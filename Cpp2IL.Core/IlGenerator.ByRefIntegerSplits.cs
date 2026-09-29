using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateByRefIntegerSplits(MethodAnalysisContext method)
    {
        if (ByRefIntegerSplitRecovery.HasEvidence(method) && !ByRefIntegerSplitRecovery.IsValidFor(method))
            throw new DecompilerException("Byref integer split proof no longer matches final managed operations.");
    }
}
