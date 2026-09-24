using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved immediate-base call and inherited Int32 write.</summary>
internal static class X64InheritedInt32ConstructorRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64InheritedInt32ConstructorProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Call,
            evidence.BaseConstructor.ToMethodDescriptor());
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldc_I4, evidence.Value);
        il.Instructions.Add(CilOpCodes.Stfld, evidence.Field.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
