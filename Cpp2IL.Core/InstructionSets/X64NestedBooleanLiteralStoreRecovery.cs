using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved null-guarded Boolean field assignment.</summary>
internal static class X64NestedBooleanLiteralStoreRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64NestedBooleanLiteralStoreProof.Find(method) is not { } evidence)
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
        instructions.Add(evidence.Value ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
        if (evidence.ValueSetter is { } setter)
            instructions.Add(CilOpCodes.Call, setter.ToMethodDescriptor());
        else
            instructions.Add(CilOpCodes.Stfld, evidence.ValueField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
