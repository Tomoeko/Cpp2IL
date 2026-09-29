using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateReferenceFieldAddressStores(MethodAnalysisContext method)
    {
        if (ReferenceFieldAddressStoreRecovery.HasEvidence(method) &&
            !ReferenceFieldAddressStoreRecovery.IsValidFor(method))
            throw new DecompilerException("Reference-field address store proof no longer matches final managed operations.");
    }
}
