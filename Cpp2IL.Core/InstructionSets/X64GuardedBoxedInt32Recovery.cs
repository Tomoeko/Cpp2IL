using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved sealed-type guard and Int32 comparison.</summary>
internal static class X64GuardedBoxedInt32Recovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (!X64GuardedBoxedInt32Proof.Find(method))
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var int32 = method.AppContext.SystemTypes.SystemInt32Type
            .ToTypeSignature().ToTypeDefOrRef();
        var falseResult = new CilInstruction(CilOpCodes.Ldc_I4_0);
        var instructions = il.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Isinst, int32);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(falseResult));
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Unbox_Any, int32);
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Ceq);
        instructions.Add(CilOpCodes.Ret);
        instructions.Add(falseResult);
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
