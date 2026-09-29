using System.Linq;
using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the fully proved object-parameter Boolean class test.</summary>
internal static class X64BooleanParameterClassTestRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        if (X64BooleanParameterClassTestProof.Find(method, X86Utils.Iterate(method).ToArray()) is not { } target)
            return false;

        X64ParameterClassTestRecovery.EmitProvedTest(definition, target, booleanResult: true);
        return true;
    }
}
