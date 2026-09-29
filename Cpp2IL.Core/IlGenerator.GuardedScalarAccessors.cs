using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateGuardedScalarAccessors(MethodAnalysisContext method)
    {
        if (GuardedScalarAccessorRecovery.HasEvidence(method) && !GuardedScalarAccessorRecovery.IsValidFor(method))
            throw new DecompilerException("Guarded scalar accessor proof no longer matches final managed operations.");
    }
}
