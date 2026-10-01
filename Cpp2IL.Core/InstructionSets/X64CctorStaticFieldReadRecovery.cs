using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Retains a typed field read without admitting native class-initialization behavior.</summary>
internal static class X64CctorStaticFieldReadRecovery
{
    internal static bool TryGeneratePartial(MethodAnalysisContext method, MethodDefinition definition,
        out string[] unresolvedReasons)
    {
        unresolvedReasons = [];
        if (X64CctorStaticFieldReadProof.Find(method) is not { } proof ||
            !MatchesOutput(method, definition, proof)) return false;
        var body = new CilMethodBody
        {
            InitializeLocals = false,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        body.Instructions.Add(CilOpCodes.Ldsfld, proof.Field.ToFieldDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        // Reauthenticate the caller, complete folded identity scope, metadata
        // rows and native helper bytes before replacing any existing body.
        if (X64CctorStaticFieldReadProof.Find(method) is not { } current || !proof.Matches(current))
            return false;
        var previous = definition.CilMethodBody;
        definition.CilMethodBody = body;
        try { body.VerifyLabels(); body.ComputeMaxStack(); }
        catch
        {
            definition.CilMethodBody = previous;
            throw;
        }
        unresolvedReasons =
        [
            "CCTOR-READ-INITIALIZATION: Native class-constructor execution, cached initialization exceptions, recursion, waiting, lock ownership and handler cleanup are not qualified by the exported helper's identity.",
            "CCTOR-READ-SCHEDULING: Managed class-initialization scheduling and the native metadata once-flag effects require behavioral equivalence, including the preserved BeforeFieldInit declaration.",
            "CCTOR-READ-MODIFIERS: Player metadata does not establish complete volatile custom-modifier provenance for the static reference load."
        ];
        return true;
    }

    private static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition definition,
        X64CctorStaticFieldReadProof.Evidence proof)
    {
        var owner = method.DeclaringType!;
        var output = owner.GetExtraData<TypeDefinition>("AsmResolverType");
        var field = proof.Field.GetExtraData<FieldDefinition>("AsmResolverField");
        var constructor = proof.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod");
        var fieldOrdinal = owner.Fields.IndexOf(proof.Field);
        var methodOrdinal = owner.Methods.IndexOf(method);
        var constructorOrdinal = owner.Methods.IndexOf(proof.Constructor);
        return output != null && ReferenceEquals(definition.DeclaringType, output) &&
            output.Name == owner.Name && output.Namespace == owner.Namespace &&
            (uint)output.Attributes == (uint)owner.Attributes &&
            ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), definition) &&
            methodOrdinal >= 0 && methodOrdinal < output.Methods.Count && ReferenceEquals(output.Methods[methodOrdinal], definition) &&
            definition.Signature is { GenericParameterCount: 0, ParameterTypes.Count: 0 } signature &&
            signature.HasThis == !method.IsStatic && !signature.ExplicitThis && definition.GenericParameters.Count == 0 &&
            definition.Name == method.Name && (ushort)definition.Attributes == (ushort)method.Attributes &&
            (ushort)definition.ImplAttributes == (ushort)method.ImplAttributes &&
            SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) &&
            field != null && ReferenceEquals(field.DeclaringType, output) && fieldOrdinal >= 0 &&
            fieldOrdinal < output.Fields.Count && ReferenceEquals(output.Fields[fieldOrdinal], field) &&
            field.Name == proof.Field.Name && (ushort)field.Attributes == (ushort)proof.Field.Attributes &&
            SignatureComparer.Default.Equals(field.Signature?.FieldType, proof.Field.FieldType.ToTypeSignature()) &&
            constructor != null && ReferenceEquals(constructor.DeclaringType, output) && constructorOrdinal >= 0 &&
            constructorOrdinal < output.Methods.Count && ReferenceEquals(output.Methods[constructorOrdinal], constructor) &&
            constructor.Name == proof.Constructor.Name && (ushort)constructor.Attributes == (ushort)proof.Constructor.Attributes &&
            (ushort)constructor.ImplAttributes == (ushort)proof.Constructor.ImplAttributes &&
            constructor.GenericParameters.Count == 0 && constructor.Signature is
                { HasThis: false, ExplicitThis: false, GenericParameterCount: 0, ParameterTypes.Count: 0 } constructorSignature &&
            SignatureComparer.Default.Equals(constructorSignature.ReturnType, proof.Constructor.ReturnType.ToTypeSignature());
    }
}
