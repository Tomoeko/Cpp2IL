using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the complete guarded static and instance Int32 addition.</summary>
internal static class X64MetadataStaticObjectInt32AddRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64MetadataStaticObjectInt32AddProof.Find(method) is not { } evidence)
            return false;
        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldsfld, evidence.StaticField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldfld, evidence.InstanceField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Add);
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
