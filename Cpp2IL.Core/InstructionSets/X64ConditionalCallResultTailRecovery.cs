using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ConditionalCallResultTailRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64ConditionalCallResultTailProof.Find(method) is not { } evidence)
            return false;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var finish = new CilInstruction(CilOpCodes.Ret);
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Call, evidence.Producer.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Callvirt, evidence.Predicate.ToMethodDescriptor());
        body.Instructions.Add(evidence.CallsWhenTrue ? CilOpCodes.Brfalse : CilOpCodes.Brtrue,
            new CilInstructionLabel(finish));
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Call, evidence.Producer.ToMethodDescriptor());
        body.Instructions.Add(evidence.LiteralValue ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
        body.Instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        body.Instructions.Add(finish);
        return true;
    }
}
