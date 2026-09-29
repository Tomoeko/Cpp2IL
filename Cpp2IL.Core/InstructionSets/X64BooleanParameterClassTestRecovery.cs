using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the fully proved object-parameter Boolean class test.</summary>
internal static class X64BooleanParameterClassTestRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        method.EnsureRawBytes();
        if (X64BooleanParameterClassTestProof.Find(method, X86Utils.Iterate(method).ToArray()) is not { } target)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldarg_0);
        il.Instructions.Add(CilOpCodes.Isinst, target.ToTypeSignature().ToTypeDefOrRef());
        il.Instructions.Add(CilOpCodes.Ldnull);
        il.Instructions.Add(CilOpCodes.Cgt_Un);
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
