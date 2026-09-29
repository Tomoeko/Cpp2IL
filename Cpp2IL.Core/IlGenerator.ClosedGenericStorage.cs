using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateClosedGenericStorage(MethodAnalysisContext method)
    {
        if (ClosedGenericStorageRecovery.HasEvidence(method) && !ClosedGenericStorageRecovery.IsValidFor(method))
            throw new DecompilerException("Closed generic storage proof no longer matches the final constructor operations.");
    }
}
