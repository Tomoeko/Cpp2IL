using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>Canonical Boolean specialization of the shared literal-store proof.</summary>
internal static class BooleanLiteralFieldStoreProof
{
    internal static bool IsValidFor(MethodAnalysisContext method, Instruction operation,
        FieldReference access, Immediate literal) =>
        ReferenceEquals(access.Field.FieldType, method.AppContext.SystemTypes.SystemBooleanType) &&
        LiteralFieldStoreProof.IsValidFor(method, operation, access, literal);
}
