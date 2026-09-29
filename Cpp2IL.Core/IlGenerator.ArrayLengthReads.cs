using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateArrayLengthReads(MethodAnalysisContext method)
    {
        if (!ArrayLengthReadRecovery.IsValidFor(method))
            throw new DecompilerException("Guarded array Length proof no longer matches final managed operations.");
    }
}
