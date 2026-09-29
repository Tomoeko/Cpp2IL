using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateFinalInterfaceBooleanFieldGetters(MethodAnalysisContext method)
    {
        if (FinalInterfaceBooleanFieldGetterRecovery.HasEvidence(method) &&
            !FinalInterfaceBooleanFieldGetterRecovery.IsValidFor(method))
            throw new DecompilerException("Final interface Boolean field getter proof no longer matches final managed operations.");
    }
}
