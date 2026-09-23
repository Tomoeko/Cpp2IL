using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits a guarded instance call only after its complete native field-load shape is proved.</summary>
internal static class X64GuardedFieldCallRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GuardedFieldCallProof.Find(method) is not { } evidence)
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
        foreach (var argument in evidence.ArgumentFields)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            instructions.Add(CilOpCodes.Ldfld, argument.ToFieldDescriptor());
        }
        instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
