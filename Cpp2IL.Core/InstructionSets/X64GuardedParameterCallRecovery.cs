using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64GuardedParameterCallRecovery
{
    internal static void Emit(MethodDefinition definition, FieldAnalysisContext receiver,
        MethodAnalysisContext target)
    {
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var instructions = il.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, receiver.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Callvirt, target.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
    }
}
