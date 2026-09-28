using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits a proved guarded Boolean-array read from an instance field.</summary>
internal static class X64FieldBooleanArrayReadRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64FieldBooleanArrayReadProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldfld,
            evidence.ArrayField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldarg_1);
        il.Instructions.Add(CilOpCodes.Ldelem_U1);
        // Native SETNE returns exactly 0 or 1 even for a noncanonical element byte.
        il.Instructions.Add(CilOpCodes.Ldc_I4_0);
        il.Instructions.Add(CilOpCodes.Ceq);
        il.Instructions.Add(CilOpCodes.Ldc_I4_0);
        il.Instructions.Add(CilOpCodes.Ceq);
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
