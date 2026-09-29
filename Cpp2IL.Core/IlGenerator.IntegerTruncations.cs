using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateIntegerTruncations(MethodAnalysisContext method)
    {
        if (IntegerTruncationRecovery.HasEvidence(method) && !IntegerTruncationRecovery.IsValidFor(method))
            throw new DecompilerException("Native integer truncation proof no longer matches final managed operations.");
    }
}
