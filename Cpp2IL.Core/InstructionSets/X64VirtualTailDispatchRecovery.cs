using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits one proven virtual tail as a null-checking managed dispatch.</summary>
internal static class X64VirtualTailDispatchRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64VirtualTailDispatchProof.Find(method,
                X86Utils.Iterate(method).ToArray()) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        definition.CilMethodBody = body;
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Callvirt, evidence.Target.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
