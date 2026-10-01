using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ScalarStaticConstructorRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64ScalarStaticConstructorProof.TryAuthenticate(method, out var proof) ||
            !X64ScalarStaticConstructorProof.MatchesOutput(method, definition)) return false;
        var body = new CilMethodBody
        {
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        body.Instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)proof.ValueBits));
        body.Instructions.Add(CilOpCodes.Stsfld, proof.StaticField.ToFieldDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
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
