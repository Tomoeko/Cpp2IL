using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ScalarWrapperConversionRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64ScalarWrapperConversionProof.TryAuthenticate(method, out var proof)) return false;
        var owner = method.DeclaringType!;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        definition.CilMethodBody = body;
        var value = new CilLocalVariable(owner.ToTypeSignature());
        body.LocalVariables.Add(value);
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldloca, value);
        il.Add(CilOpCodes.Initobj, owner.ToTypeSignature().ToTypeDefOrRef());
        il.Add(CilOpCodes.Ldloca, value);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Stfld, proof.Field.ToFieldDescriptor());
        il.Add(CilOpCodes.Ldloc, value);
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
