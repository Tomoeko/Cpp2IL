using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved captured-reference scalar parameter store.</summary>
internal static class X64NestedScalarParameterStoreRecovery
{
    internal static bool TryGeneratePartial(MethodAnalysisContext method, MethodDefinition definition,
        out string[] unresolvedReasons)
    {
        unresolvedReasons = [];
        if (X64NestedScalarParameterStoreProof.Find(method) is not { } proof ||
            !MatchesOutputDeclaration(method, definition, proof)) return false;
        var source = proof.SourceField.ToFieldDescriptor();
        var value = proof.ValueField.ToFieldDescriptor();
        var body = new CilMethodBody
        {
            InitializeLocals = false,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Ldfld, source);
        body.Instructions.Add(CilOpCodes.Ldarg_1);
        body.Instructions.Add(CilOpCodes.Stfld, value);
        body.Instructions.Add(CilOpCodes.Ret);
        var previous = definition.CilMethodBody;
        definition.CilMethodBody = body;
        try { body.VerifyLabels(); body.ComputeMaxStack(); }
        catch
        {
            definition.CilMethodBody = previous;
            throw;
        }
        // The explicit nested-reference null helper is proved. The implicit
        // fault at the owner load and stripped custom modifiers remain separate
        // body-local obligations, even when a controlled fixture passes.
        unresolvedReasons =
        [
            "NESTED-STORE-IMPLICIT-FAULT: The native implicit owner-null fault transport is not qualified.",
            "NESTED-STORE-MODIFIERS: Player metadata does not establish complete volatile custom-modifier provenance for the captured reference and scalar store."
        ];
        return true;
    }

    private static bool MatchesOutputDeclaration(MethodAnalysisContext method, MethodDefinition definition,
        X64NestedScalarParameterStoreProof.Evidence proof) =>
        ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), definition) &&
        definition.Signature is { HasThis: true, GenericParameterCount: 0, ParameterTypes.Count: 1 } signature &&
        definition.GenericParameters.Count == 0 &&
        !signature.ExplicitThis && definition.Name == method.Name &&
        (ushort)definition.Attributes == (ushort)method.Attributes &&
        (ushort)definition.ImplAttributes == (ushort)method.ImplAttributes &&
        SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) &&
        SignatureComparer.Default.Equals(signature.ParameterTypes[0], method.Parameters[0].ParameterType.ToTypeSignature()) &&
        MatchesOutputField(proof.SourceField, out var source) && ReferenceEquals(source!.DeclaringType, definition.DeclaringType) &&
        MatchesOutputField(proof.ValueField, out _);

    private static bool MatchesOutputField(FieldAnalysisContext original, out FieldDefinition? field)
    {
        field = original.GetExtraData<FieldDefinition>("AsmResolverField");
        var owner = original.DeclaringType;
        var output = owner.GetExtraData<TypeDefinition>("AsmResolverType");
        var ordinal = owner.Fields.IndexOf(original);
        return field != null && output != null && ordinal >= 0 && ordinal < output.Fields.Count &&
            ReferenceEquals(field.DeclaringType, output) && ReferenceEquals(output.Fields[ordinal], field) &&
            field.Name == original.Name && (ushort)field.Attributes == (ushort)original.Attributes &&
            SignatureComparer.Default.Equals(field.Signature?.FieldType, original.FieldType.ToTypeSignature());
    }
}
