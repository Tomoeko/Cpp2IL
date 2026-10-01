using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits a proved Boolean store through a guarded array element.</summary>
internal static class X64ArrayElementBooleanStoreRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64ArrayElementBooleanStoreProof.Find(method) is not { } evidence)
            return false;
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        CilLocalVariable? capturedArray = null;
        if (evidence.OwnerEffectField is { } ownerEffect)
        {
            if (evidence.CapturesArrayBeforeOwnerEffect)
            {
                capturedArray = new CilLocalVariable(evidence.ArrayField.FieldType.ToTypeSignature());
                il.LocalVariables.Add(capturedArray);
                il.Instructions.Add(CilOpCodes.Ldarg_0);
                il.Instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
                il.Instructions.Add(CilOpCodes.Stloc, capturedArray);
            }
            // This effect is observable even when the following checked access
            // throws. Preserve the independently proved native capture order.
            il.Instructions.Add(CilOpCodes.Ldarg_0);
            il.Instructions.Add(evidence.OwnerEffectValue ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
            il.Instructions.Add(CilOpCodes.Stfld, ownerEffect.ToFieldDescriptor());
        }
        if (capturedArray != null)
            il.Instructions.Add(CilOpCodes.Ldloc, capturedArray);
        else
        {
            il.Instructions.Add(CilOpCodes.Ldarg_0);
            il.Instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
        }
        il.Instructions.Add(CilOpCodes.Ldarg_1);
        il.Instructions.Add(CilOpCodes.Ldelem_Ref);
        il.Instructions.Add(evidence.Value ? CilOpCodes.Ldc_I4_1 : CilOpCodes.Ldc_I4_0);
        il.Instructions.Add(CilOpCodes.Stfld, evidence.ValueField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
