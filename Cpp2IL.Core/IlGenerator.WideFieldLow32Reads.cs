using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateWideFieldLow32Reads(MethodAnalysisContext method)
    {
        if (WideFieldLow32ReadRecovery.HasEvidence(method) && !WideFieldLow32ReadRecovery.IsValidFor(method))
            throw new DecompilerException("Wide field low32 read proof no longer matches final managed operations.");
    }
}
