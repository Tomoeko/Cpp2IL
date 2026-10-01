using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Retains the guarded, original external immediate-base constructor call.</summary>
internal static class X64RetainedAncestorConstructorRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GuardedBaseConstructorProof.FindRetainedAncestor(method) is not { } evidence)
            return false;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Call, evidence.BaseConstructor.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        definition.CilMethodBody = body;
        return true;
    }
}
