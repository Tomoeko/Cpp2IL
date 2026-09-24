using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the by-value struct call only after the closed ABI proof succeeds.</summary>
internal static class X64GuardedStructParameterCallRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GuardedStructParameterCallProof.Find(method) is not { } evidence)
            return false;
        X64GuardedParameterCallRecovery.Emit(definition, evidence.ReceiverField, evidence.Target);
        return true;
    }
}
