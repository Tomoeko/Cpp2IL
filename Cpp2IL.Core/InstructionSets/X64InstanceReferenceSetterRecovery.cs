using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only instance reference setters accepted by the complete proof.</summary>
internal static class X64InstanceReferenceSetterRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64InstanceReferenceSetterProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldarg_1);
        il.Instructions.Add(CilOpCodes.Stfld, evidence.Field.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
