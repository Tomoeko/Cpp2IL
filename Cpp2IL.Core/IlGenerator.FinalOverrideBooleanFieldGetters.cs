using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateFinalOverrideBooleanFieldGetters(MethodAnalysisContext method)
    {
        if (FinalOverrideBooleanFieldGetterRecovery.HasEvidence(method) &&
            !FinalOverrideBooleanFieldGetterRecovery.IsValidFor(method))
            throw new DecompilerException("Final override Boolean field getter proof no longer matches final managed operations.");
    }
}
