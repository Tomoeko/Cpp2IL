namespace Cpp2IL.Core.Reporting;

/// <summary>
/// Describes what the recovery pipeline produced, not whether it reproduced the input behavior.
/// </summary>
public enum MethodRecoveryDisposition
{
    NotProcessed,
    Emitted,
    Partial,
    Failed,
    NoNativeBody,
    EmptyAnalysis,
    SkippedMethodSize,
    ExcludedReferenceAssembly,
    NoManagedBody,
    ExcludedInjectedMethod,
}
