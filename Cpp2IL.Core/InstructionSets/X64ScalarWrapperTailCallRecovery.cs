using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ScalarWrapperTailCallRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64ScalarWrapperTailCallProof.Find(method) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        definition.CilMethodBody = body;
        var il = body.Instructions;
        var field = evidence.Field.ToFieldDescriptor();

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldflda, field);
        if (evidence.HasValueArgument)
        {
            il.Add(CilOpCodes.Ldarga, definition.Parameters[0]);
            il.Add(CilOpCodes.Ldfld, field);
        }
        il.Add(CilOpCodes.Call, evidence.Target.ToMethodDescriptor());
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
