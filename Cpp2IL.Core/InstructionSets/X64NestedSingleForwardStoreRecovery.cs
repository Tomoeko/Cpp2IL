using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits a call to the proved Single setter after loading its owner field.</summary>
internal static class X64NestedSingleForwardStoreRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64NestedSingleForwardStoreProof.Find(method) is not { } evidence)
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
        instructions.Add(CilOpCodes.Callvirt, evidence.Setter.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
