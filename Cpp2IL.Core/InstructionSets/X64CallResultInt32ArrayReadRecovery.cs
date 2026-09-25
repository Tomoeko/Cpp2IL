using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64CallResultInt32ArrayReadRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64CallResultInt32ArrayReadProof.Find(method) is not { } evidence)
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
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldarg_2);
        instructions.Add(CilOpCodes.Ldelem_I4);
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
