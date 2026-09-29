using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateOpenGenericPrefixFields(MethodAnalysisContext method)
    {
        if (OpenGenericPrefixFieldLayoutProof.HasEvidence(method) &&
            !OpenGenericPrefixFieldLayoutProof.IsValidFor(method))
            throw new DecompilerException("Open generic prefix-field proof no longer matches the final operations.");
    }
}
