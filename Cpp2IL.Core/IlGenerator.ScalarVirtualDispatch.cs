using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void ValidateScalarVirtualDispatch(MethodAnalysisContext method)
    {
        if (ScalarVirtualDispatchRecovery.HasEvidence(method) && !ScalarVirtualDispatchRecovery.IsValidFor(method))
            throw new DecompilerException("Virtual scalar dispatch requires its unchanged complete native call, original slot, receiver, arguments and ordered result effects");
    }
}
