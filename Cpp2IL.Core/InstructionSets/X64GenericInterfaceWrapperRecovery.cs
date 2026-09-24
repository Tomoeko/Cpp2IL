using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only generic interface wrapper calls with complete MethodRef evidence.</summary>
internal static class X64GenericInterfaceWrapperRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64GenericInterfaceWrapperProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldc_I4, evidence.Argument);
        il.Instructions.Add(CilOpCodes.Call, evidence.Target.ToMethodDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
