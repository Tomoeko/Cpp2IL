using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateSmallAggregateFieldGetters(MethodAnalysisContext method)
    {
        if (SmallAggregateFieldGetterRecovery.HasEvidence(method) &&
            !SmallAggregateFieldGetterRecovery.IsValidFor(method))
            throw new DecompilerException("Small aggregate field getter proof no longer matches final managed operations.");
    }
}
