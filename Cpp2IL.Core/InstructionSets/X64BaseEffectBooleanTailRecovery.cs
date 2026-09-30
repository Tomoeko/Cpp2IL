using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64BaseEffectBooleanTailRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64BaseEffectBooleanTailProof.Find(method) is not { } evidence)
            return false;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var instructions = body.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, evidence.Effect.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, evidence.Producer.ToMethodDescriptor());
        instructions.Add(evidence.Value ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
