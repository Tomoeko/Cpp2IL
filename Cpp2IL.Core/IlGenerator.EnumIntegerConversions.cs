using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateEnumIntegerConversions(MethodAnalysisContext method)
    {
        if (EnumIntegerConversionRecovery.HasEvidence(method) && !EnumIntegerConversionRecovery.IsValidFor(method))
            throw new DecompilerException("Enum integer conversion proof no longer matches final managed operations.");
    }
}
