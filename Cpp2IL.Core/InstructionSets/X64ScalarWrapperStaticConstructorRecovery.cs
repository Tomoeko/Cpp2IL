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
        if (!X64ScalarWrapperStaticConstructorProof.TryAuthenticate(method, out var proof) ||
            !X64ScalarStaticConstructorProof.MatchesDeclaration(method, definition))
            return false;

        var owner = method.DeclaringType!;
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        var value = new CilLocalVariable(owner.ToTypeSignature());
        body.LocalVariables.Add(value);
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldloca, value);
        il.Add(CilOpCodes.Initobj, owner.ToTypeSignature().ToTypeDefOrRef());
        il.Add(CilOpCodes.Ldloca, value);
        switch (proof.ScalarField.FieldType.Type)
        {
            case LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_I2:
                il.Add(CilOpCodes.Ldc_I4, (int)unchecked((short)proof.ValueBits));
                break;
            case LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_U4:
            case LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_I4:
                il.Add(CilOpCodes.Ldc_I4, unchecked((int)proof.ValueBits));
                break;
            case LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_U8:
                il.Add(CilOpCodes.Ldc_I8, unchecked((long)proof.ValueBits));
                break;
            default:
                return false;
        }
        il.Add(CilOpCodes.Stfld, proof.ScalarField.ToFieldDescriptor());
        il.Add(CilOpCodes.Ldloc, value);
        il.Add(CilOpCodes.Stsfld, proof.StaticField.ToFieldDescriptor());
        il.Add(CilOpCodes.Ret);
        var previous = definition.CilMethodBody;
        definition.CilMethodBody = body;
        try { body.VerifyLabels(); body.ComputeMaxStack(); }
        catch
        {
            definition.CilMethodBody = previous;
            throw;
        }
        return true;
    }
}
