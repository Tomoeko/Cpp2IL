using System;

namespace Cpp2IL.Core.Reporting;

public sealed class IncompleteRecoveryException(string message, RecoveryReport report) : Exception(message)
{
    public RecoveryReport Report { get; } = report;
}
