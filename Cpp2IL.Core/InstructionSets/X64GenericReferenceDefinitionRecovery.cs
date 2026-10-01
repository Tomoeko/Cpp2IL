using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

// This adapter produces useful incomplete IL from authenticated body facts.
// Its caller must record Partial, never Emitted, while body-local gaps remain.
internal static class X64GenericReferenceDefinitionRecovery
{
    internal static bool TryGeneratePartial(MethodAnalysisContext method,
        MethodDefinition definition, out string[] reasons)
    {
        reasons = [];
        if (method.Definition?.genericContainerIndex.IsNull != false ||
            X64GenericReferenceDefinitionProof.Find(method, out _) is not { } evidence ||
            !evidence.IsUnchanged(out _) || !MatchesOutputDeclaration(method, definition, evidence))
            return false;

        // Resolve all managed operands before publishing a body. A failed
        // binding must preserve any body already attached to the output method.
        var field = evidence.Counter.ToFieldDescriptor();
        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        foreach (var step in evidence.Recipe)
        {
            if (step.Field != null)
                body.Instructions.Add(step.OpCode, field);
            else
                body.Instructions.Add(step.OpCode);
        }

        var previous = definition.CilMethodBody;
        definition.CilMethodBody = body;
        try
        {
            body.VerifyLabels();
            body.ComputeMaxStack();
        }
        catch
        {
            definition.CilMethodBody = previous;
            throw;
        }
        reasons = ["GENERIC-BODY-PARTIAL: Typed generic definition IL preserves the authenticated registered native leaf's normal-path increment and reference return; complete behavior remains unresolved.",
            .. evidence.UnresolvedBodyReasons];
        return true;
    }

    private static bool MatchesOutputDeclaration(MethodAnalysisContext method,
        MethodDefinition definition, X64GenericReferenceDefinitionProof.Evidence evidence)
    {
        var owner = method.DeclaringType!;
        var outputOwner = owner.GetExtraData<TypeDefinition>("AsmResolverType");
        var fieldOrdinal = owner.Fields.IndexOf(evidence.Counter);
        if (!ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), definition) ||
            outputOwner == null || !ReferenceEquals(definition.DeclaringType, outputOwner) ||
            definition.Name != method.Name ||
            definition.Signature is not { HasThis: true, ExplicitThis: false, GenericParameterCount: 1 } signature ||
            !IsOriginalParameter(signature.ReturnType) || signature.ParameterTypes.Count != 1 ||
            !IsOriginalParameter(signature.ParameterTypes[0]) || definition.GenericParameters.Count != 1 ||
            definition.GenericParameters[0].Name != evidence.GenericParameter.Name ||
            (ushort)definition.GenericParameters[0].Attributes != (ushort)evidence.GenericParameter.Attributes ||
            definition.GenericParameters[0].Constraints.Count != 0 ||
            evidence.Counter.GetExtraData<FieldDefinition>("AsmResolverField") is not { } field ||
            fieldOrdinal < 0 || fieldOrdinal >= outputOwner.Fields.Count ||
            !ReferenceEquals(outputOwner.Fields[fieldOrdinal], field) ||
            !ReferenceEquals(field.DeclaringType, outputOwner) || field.Name != evidence.Counter.Name ||
            (ushort)field.Attributes != (ushort)evidence.Counter.Attributes ||
            (ushort)definition.Attributes != (ushort)method.Attributes ||
            (ushort)definition.ImplAttributes != (ushort)method.ImplAttributes)
            return false;
        return SignatureComparer.Default.Equals(field.Signature?.FieldType, evidence.Counter.FieldType.ToTypeSignature());
    }

    private static bool IsOriginalParameter(TypeSignature? signature) => signature is
        GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };
}
