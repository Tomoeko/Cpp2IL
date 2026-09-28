using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64OwnerIndexedEnumArrayReadRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64OwnerIndexedEnumArrayReadProof.Find(method) is not { } evidence)
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
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldfld,
            evidence.IndexField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldelem,
            evidence.ElementType.ToTypeSignature().ToTypeDefOrRef());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
