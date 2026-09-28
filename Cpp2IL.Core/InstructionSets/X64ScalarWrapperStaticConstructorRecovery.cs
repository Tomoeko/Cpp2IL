using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ScalarWrapperStaticConstructorRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64ScalarWrapperStaticConstructorProof.Find(method) is not { } proof)
            return false;

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
        if (proof.ScalarField.FieldType.Type ==
            LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_U4)
            il.Add(CilOpCodes.Ldc_I4, unchecked((int)proof.ValueBits));
        else
            il.Add(CilOpCodes.Ldc_I8, unchecked((long)proof.ValueBits));
        il.Add(CilOpCodes.Stfld, proof.ScalarField.ToFieldDescriptor());
        il.Add(CilOpCodes.Ldloc, value);
        il.Add(CilOpCodes.Stsfld, proof.StaticField.ToFieldDescriptor());
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
