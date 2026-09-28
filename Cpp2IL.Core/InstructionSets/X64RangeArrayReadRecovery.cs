using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64RangeArrayReadRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64RangeArrayReadProof.Find(method) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var array = new CilLocalVariable(evidence.ArrayField.FieldType.ToTypeSignature());
        body.LocalVariables.Add(array);
        var instructions = body.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld, evidence.ArrayField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Stloc, array);
        instructions.Add(CilOpCodes.Ldloc, array);
        instructions.Add(CilOpCodes.Ldc_I4_0);
        instructions.Add(CilOpCodes.Ldloc, array);
        instructions.Add(CilOpCodes.Ldlen);
        instructions.Add(CilOpCodes.Conv_I4);
        instructions.Add(CilOpCodes.Call, evidence.Range.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldelem_Ref);
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
