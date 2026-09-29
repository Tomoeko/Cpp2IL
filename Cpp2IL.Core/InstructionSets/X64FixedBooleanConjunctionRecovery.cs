using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits two proved fixed Boolean-array reads in native effect order.</summary>
internal static class X64FixedBooleanConjunctionRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64FixedBooleanConjunctionProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var secondRead = new CilInstruction(CilOpCodes.Ldarg_0);
        var instructions = il.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, evidence.First.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Ldelem_U1);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(secondRead));
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Ret);
        instructions.Add(secondRead);
        instructions.Add(CilOpCodes.Ldfld, evidence.Second.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Ldelem_U1);
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Ceq);
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
