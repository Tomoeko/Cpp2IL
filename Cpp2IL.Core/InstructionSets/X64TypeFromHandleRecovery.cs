using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Emits the original type token and bound System.Type static call. Keeping the
/// call preserves the target's class-initialization behavior.
/// </summary>
internal static class X64TypeFromHandleRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        method.EnsureRawBytes();
        if (X64TypeFromHandleProof.Find(method, X86Utils.Iterate(method).ToArray())
            is not { } proof)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldtoken,
            proof.TargetType.ToTypeSignature().ToTypeDefOrRef());
        il.Instructions.Add(CilOpCodes.Call,
            proof.GetTypeFromHandle.ToMethodDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
