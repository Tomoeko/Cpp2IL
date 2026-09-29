using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateOpenGenericEarlyFields(MethodAnalysisContext method)
    {
        if (OpenGenericEarlyFieldProof.HasEvidence(method) &&
            !OpenGenericEarlyFieldProof.IsValidFor(method))
            throw new DecompilerException("Open generic early-field proof no longer matches the final operations.");
    }
}
