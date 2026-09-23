using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the one-parameter enum call only after the closed native tail call is proved.</summary>
internal static class X64GuardedEnumParameterCallRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GuardedEnumParameterCallProof.Find(method) is not { } evidence)
            return false;
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var instructions = il.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, evidence.ReceiverField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
