using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the closed Boolean parameter store through an instance array field.</summary>
internal static class X64FieldParameterBooleanArrayStoreRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64FieldParameterBooleanArrayStoreProof.Find(method) is not { } evidence)
            return false;
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldarg_1);
        il.Instructions.Add(CilOpCodes.Ldarg_2);
        il.Instructions.Add(CilOpCodes.Stelem_I1);
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
