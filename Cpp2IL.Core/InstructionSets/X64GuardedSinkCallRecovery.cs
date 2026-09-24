using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only sink calls accepted by the complete native and metadata proof.</summary>
internal static class X64GuardedSinkCallRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GuardedSinkCallProof.Find(method) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        body.Instructions.Add(CilOpCodes.Ldstr, evidence.Literal);
        body.Instructions.Add(CilOpCodes.Ldarg_1);
        body.Instructions.Add(CilOpCodes.Call, evidence.SinkMethod.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
