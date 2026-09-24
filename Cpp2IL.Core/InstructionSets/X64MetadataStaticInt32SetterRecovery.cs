using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only static Int32 stores accepted by the complete native and metadata proof.</summary>
internal static class X64MetadataStaticInt32SetterRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64MetadataStaticInt32SetterProof.Find(method) is not { } evidence)
            return false;
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Stsfld, evidence.Field.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
