using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Retains a proved throw sequence without claiming an unqualified helper ABI.</summary>
internal static class X64InstanceByrefThrowRecovery
{
    internal static bool TryGeneratePartial(MethodAnalysisContext method, MethodDefinition output,
        out string[] reasons)
    {
        reasons = [];
        method.EnsureRawBytes();
        if (X64TerminalManagedThrowProof.FindPartialInstanceByref(method,
                X86Utils.Iterate(method).ToArray()) is not { } proof ||
            !MatchesOutput(method, output) ||
            proof.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } outputConstructor ||
            !MatchesOutput(proof.Constructor, outputConstructor)) return false;

        var constructor = proof.Constructor.ToMethodDescriptor();
        var body = new CilMethodBody
        {
            InitializeLocals = false,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        body.Instructions.Add(CilOpCodes.Newobj, constructor);
        body.Instructions.Add(CilOpCodes.Throw);
        var previous = output.CilMethodBody;
        output.CilMethodBody = body;
        try { body.VerifyLabels(); body.ComputeMaxStack(); }
        catch
        {
            output.CilMethodBody = previous;
            throw;
        }
        reasons =
        [
            "THROW-HELPER-ABI: The incoming byref argument registers are not qualified through the complete native metadata and allocation helper chain."
        ];
        return true;
    }

    private static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition output)
    {
        var owner = method.DeclaringType!.GetExtraData<TypeDefinition>("AsmResolverType");
        var ordinal = method.DeclaringType.Methods.IndexOf(method);
        if (!ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output) ||
            owner == null || !ReferenceEquals(output.DeclaringType, owner) ||
            ordinal < 0 || ordinal >= owner.Methods.Count || !ReferenceEquals(owner.Methods[ordinal], output) ||
            output.Name != method.Name ||
            output.Signature is not { HasThis: true, ExplicitThis: false, GenericParameterCount: 0 } signature ||
            output.GenericParameters.Count != 0 || signature.ParameterTypes.Count != method.Parameters.Count ||
            (ushort)output.Attributes != (ushort)method.Attributes ||
            (ushort)output.ImplAttributes != (ushort)method.ImplAttributes ||
            !SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()))
            return false;
        for (var index = 0; index < method.Parameters.Count; index++)
            if (!SignatureComparer.Default.Equals(signature.ParameterTypes[index],
                    method.Parameters[index].ParameterType.ToTypeSignature())) return false;
        return true;
    }
}
