using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identity of the exact target runtime NullCheck operation, not a managed constructor call.
/// Native bytes are immutable input; mutable declaration contexts are rebound at consumption.
/// </summary>
internal sealed class RuntimeNullThrowEvidence : IOperand
{
    private readonly ApplicationAnalysisContext _application;
    private readonly object _binary;
    private readonly MethodAnalysisContext _identity;
    internal ulong NativeTarget { get; }

    private RuntimeNullThrowEvidence(ApplicationAnalysisContext app, ulong target, MethodAnalysisContext identity)
    {
        _application = app;
        _binary = app.Binary;
        _identity = identity;
        NativeTarget = target;
    }

    internal static RuntimeNullThrowEvidence? TryCreate(ApplicationAnalysisContext app, ulong target)
        => X86RuntimeNullThrowProof.TryResolveIdentity(app, target) is { } identity
            ? new(app, target, identity) : null;

    internal bool IsValidFor(ApplicationAnalysisContext app)
        => ReferenceEquals(app, _application) && ReferenceEquals(app.Binary, _binary) &&
           X86RuntimeNullThrowProof.IsSupportedProfile(app) &&
           ReferenceEquals(X86RuntimeNullThrowProof.BindIdentity(app), _identity);

    public override string ToString() => "target-runtime-null-throw";
}
