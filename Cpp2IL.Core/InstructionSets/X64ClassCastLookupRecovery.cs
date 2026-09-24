using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved field read and reference-preserving class test.</summary>
internal static class X64ClassCastLookupRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        method.EnsureRawBytes();
        if (X64ClassCastLookupProof.Find(method, X86Utils.Iterate(method).ToArray()) is not
            { } proof)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Ldfld, proof.SourceField.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Isinst,
            proof.TargetType.ToTypeSignature().ToTypeDefOrRef());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
