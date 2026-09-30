using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64BooleanArrayFillLoopRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64BooleanArrayFillLoopProof.Find(method) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var array = new CilLocalVariable(evidence.ArrayField.FieldType.ToTypeSignature());
        var index = new CilLocalVariable(method.AppContext.SystemTypes.SystemInt32Type.ToTypeSignature());
        body.LocalVariables.Add(array);
        body.LocalVariables.Add(index);
        var store = new CilInstruction(CilOpCodes.Ldloc, array);
        var test = new CilInstruction(CilOpCodes.Ldloc, index);
        var instructions = body.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Stloc, array);
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Stloc, index);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(test));
        instructions.Add(store);
        instructions.Add(CilOpCodes.Ldloc, index);
        instructions.Add(evidence.UsesParameter ? CilOpCodes.Ldarg_1 : CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Stelem_I1);
        // Native scheduling moves this unchecked local increment ahead of the
        // byte store after its guards. Keeping it here preserves all observable
        // effects and leaves the managed index expression straightforward.
        instructions.Add(CilOpCodes.Ldloc, index);
        instructions.Add(CilOpCodes.Ldc_I4_1);
        instructions.Add(CilOpCodes.Add);
        instructions.Add(CilOpCodes.Stloc, index);
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Stloc, array);
        // Both the entry and every post-store reload must fail on null before
        // deciding whether another iteration is needed, including the last one.
        instructions.Add(test);
        instructions.Add(CilOpCodes.Ldloc, array);
        instructions.Add(CilOpCodes.Ldlen);
        instructions.Add(CilOpCodes.Conv_I4);
        instructions.Add(CilOpCodes.Blt, new CilInstructionLabel(store));
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
