using System.Linq;
using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the fully proved sealed parameter Boolean/reference class test.</summary>
internal static class X64SealedParameterClassTestRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        if (X64SealedParameterClassTestProof.Find(method, X86Utils.Iterate(method).ToArray()) is not { } target)
            return false;

        X64ParameterClassTestRecovery.EmitProvedTest(definition, target,
            ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType));
        return true;
    }
}
