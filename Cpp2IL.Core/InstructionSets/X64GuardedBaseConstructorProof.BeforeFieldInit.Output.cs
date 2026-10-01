using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedBaseConstructorProof
{
    internal static bool MatchesBeforeFieldInitOutput(MethodAnalysisContext method, MethodDefinition output)
    {
        try
        {
            if (method.GetExtraData<BeforeFieldInitEvidence>(BeforeFieldInitEvidenceKey) is not { } proof ||
                !proof.IsUnchanged() || !MatchesBeforeFieldInitMethod(method, output) ||
                !MatchesBeforeFieldInitType(method.DeclaringType!) ||
                !MatchesBeforeFieldInitType(proof.BaseConstructor.DeclaringType!) ||
                proof.BaseConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } baseOutput ||
                !MatchesBeforeFieldInitMethod(proof.BaseConstructor, baseOutput) ||
                proof.ObjectConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } objectOutput ||
                !MatchesBeforeFieldInitMethod(proof.ObjectConstructor, objectOutput) ||
                proof.ClassConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } initializerOutput ||
                !X64ScalarStaticConstructorProof.MatchesDeclaration(proof.ClassConstructor, initializerOutput)) return false;
            if (baseOutput.CilMethodBody is { } baseBody &&
                (baseBody.ExceptionHandlers.Count != 0 || baseBody.Instructions.Count != 3 ||
                 !LoadsBeforeFieldInitThis(baseBody.Instructions[0], baseOutput) ||
                 baseBody.Instructions[1].OpCode != CilOpCodes.Call || !ReferenceEquals(baseBody.Instructions[1].Operand, objectOutput) ||
                 baseBody.Instructions[2].OpCode != CilOpCodes.Ret || baseBody.Instructions[2].Operand != null)) return false;
            if (initializerOutput.CilMethodBody is { } body &&
                (body.ExceptionHandlers.Count != 0 || body.Instructions.Count != 3 ||
                 !body.Instructions[0].IsLdcI4() || unchecked((uint)body.Instructions[0].GetLdcI4Constant()) != proof.InitializerValueBits ||
                 body.Instructions[1].OpCode != CilOpCodes.Stsfld ||
                 !ReferenceEquals(body.Instructions[1].Operand, proof.StaticField.GetExtraData<FieldDefinition>("AsmResolverField")) ||
                 body.Instructions[2].OpCode != CilOpCodes.Ret || body.Instructions[2].Operand != null)) return false;
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool LoadsBeforeFieldInitThis(CilInstruction instruction, MethodDefinition method) =>
        instruction.OpCode == CilOpCodes.Ldarg_0 && instruction.Operand == null ||
        (instruction.OpCode == CilOpCodes.Ldarg || instruction.OpCode == CilOpCodes.Ldarg_S) &&
        ReferenceEquals(instruction.Operand, method.Parameters.ThisParameter);

    private static bool MatchesBeforeFieldInitMethod(MethodAnalysisContext method, MethodDefinition output) =>
        ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output) &&
        ReferenceEquals(output.DeclaringType, method.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) &&
        output.Name == method.Name && (ushort)output.Attributes == (ushort)method.Attributes &&
        (ushort)output.ImplAttributes == (ushort)method.ImplAttributes && output.GenericParameters.Count == 0 &&
        output.ParameterDefinitions.Count == 0 &&
        output.Signature is { Attributes: CallingConventionAttributes.HasThis, HasThis: true, ExplicitThis: false,
            GenericParameterCount: 0, ParameterTypes.Count: 0, SentinelParameterTypes.Count: 0 } signature &&
        SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) &&
        !output.DeclaringType!.MethodImplementations.Any(row => ReferenceEquals(row.Body, output) ||
            SignatureComparer.Default.Equals(row.Body, output));

    private static bool MatchesBeforeFieldInitType(TypeAnalysisContext type)
    {
        if (type.Definition is not { } original || type.GetExtraData<TypeDefinition>("AsmResolverType") is not { } output ||
            type.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") is not { ManifestModule: { } module } assembly ||
            !ReferenceEquals(output.DeclaringModule, module) || !ReferenceEquals(module.Assembly, assembly) ||
            module.Name != type.DeclaringAssembly.CleanAssemblyName + ".dll" || assembly.Name != type.DeclaringAssembly.Name ||
            assembly.Version != type.DeclaringAssembly.Version ||
            (assembly.Culture?.ToString() ?? "") != (type.DeclaringAssembly.Culture ?? "") ||
            (uint)assembly.Attributes != type.DeclaringAssembly.Flags || (uint)assembly.HashAlgorithm != type.DeclaringAssembly.HashAlgorithm ||
            !(assembly.PublicKey ?? []).SequenceEqual(type.DeclaringAssembly.PublicKey ?? []) ||
            output.Name != type.Name || (output.Namespace?.ToString() ?? "") != type.Namespace ||
            (uint)output.Attributes != (uint)type.Attributes || output.GenericParameters.Count != 0 || output.DeclaringType != null ||
            !SignatureComparer.Default.Equals(output.BaseType, type.BaseType?.ToTypeSignature().ToTypeDefOrRef()) ||
            output.Interfaces.Count != type.InterfaceContexts.Count || output.Fields.Count != type.Fields.Count ||
            output.Methods.Count != type.Methods.Count) return false;
        var expected = new TypeDefinition(type.Namespace, type.Name,
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)type.Attributes);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(original, expected);
        if (output.ClassLayout?.ClassSize != expected.ClassLayout?.ClassSize ||
            output.ClassLayout?.PackingSize != expected.ClassLayout?.PackingSize ||
            output.ClassLayout != null && !ReferenceEquals(output.ClassLayout.Parent, output)) return false;
        for (var ordinal = 0; ordinal < type.InterfaceContexts.Count; ordinal++)
            if (!SignatureComparer.Default.Equals(output.Interfaces[ordinal].Interface,
                    type.InterfaceContexts[ordinal].ToTypeSignature().ToTypeDefOrRef())) return false;
        for (var ordinal = 0; ordinal < type.Methods.Count; ordinal++)
            if (!ReferenceEquals(type.Methods[ordinal].GetExtraData<MethodDefinition>("AsmResolverMethod"), output.Methods[ordinal])) return false;
        for (var ordinal = 0; ordinal < type.Fields.Count; ordinal++)
        {
            var field = type.Fields[ordinal];
            var written = output.Fields[ordinal];
            if (!ReferenceEquals(field.GetExtraData<FieldDefinition>("AsmResolverField"), written) ||
                !ReferenceEquals(written.DeclaringType, output) || written.Name != field.Name ||
                (ushort)written.Attributes != (ushort)field.Attributes ||
                written.Constant != null || written.FieldRva != null || written.MarshalDescriptor != null ||
                written.Signature is not { Attributes: CallingConventionAttributes.Field } signature ||
                !SignatureComparer.Default.Equals(signature.FieldType, field.FieldType.ToTypeSignature()) ||
                written.FieldOffset != (output.IsExplicitLayout && !field.IsStatic ? field.Offset : (int?)null)) return false;
        }
        return true;
    }
}
