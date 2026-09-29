using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateNarrowScalarFieldGetters(MethodAnalysisContext method)
    {
        if (NarrowScalarFieldGetterRecovery.HasEvidence(method) &&
            !NarrowScalarFieldGetterRecovery.IsValidFor(method))
            throw new DecompilerException("Narrow scalar field getter proof no longer matches final managed operations.");
    }
}
