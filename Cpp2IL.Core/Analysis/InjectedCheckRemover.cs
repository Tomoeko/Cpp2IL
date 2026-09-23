using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Compatibility entry points for explicit null/bounds checks. An exception type and a
/// comparison do not prove that a later managed access preserves the same exception,
/// condition and ordering. Keep the guard until that access has positive provenance.
/// </summary>
public static class InjectedCheckRemover
{
    public static void Run(MethodAnalysisContext method) { }

    public static void Run(ISILControlFlowGraph cfg) { }
}
