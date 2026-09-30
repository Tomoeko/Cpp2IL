using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64CallResultBooleanFalseTailRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64CallResultBooleanFalseTailProof.Find(method) is not { } evidence)
            return false;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(evidence.ChecksProducerReceiver ? CilOpCodes.Callvirt : CilOpCodes.Call,
            evidence.Producer.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ldc_I4_0);
        body.Instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
