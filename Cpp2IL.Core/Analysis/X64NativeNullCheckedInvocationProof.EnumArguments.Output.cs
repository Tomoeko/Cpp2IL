using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;
using AsmCallingConvention = AsmResolver.DotNet.Signatures.CallingConventionAttributes;
using AsmElementType = AsmResolver.PE.DotNet.Metadata.Tables.ElementType;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    internal static bool EnumOutputDeclarationsValid(MethodAnalysisContext caller)
    {
        try
        {
            // The primary native proof rejects missing site evidence. A
            // separate Boolean-field route has no enum declarations to check.
            if (caller.GetExtraData<System.Collections.Generic.List<Site>>(EvidenceKey) is not { } sites) return true;
            if (!sites.Any(site => site.EnumDeclarations != null)) return true;
            if (!caller.AppContext.MethodsByAddress.TryGetValue(caller.UnderlyingPointer, out var aliases)) return false;
            foreach (var method in aliases.Concat(sites.Select(site => site.Target)).Distinct())
            {
                if (!method.Parameters.Any(parameter => parameter.ParameterType.IsEnumType)) continue;
                if (method.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { Signature: { } signature } output ||
                    method.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType") is not { } owner ||
                    !ReferenceEquals(output.DeclaringType, owner) || output.Name != method.Name ||
                    (ushort)output.Attributes != (ushort)method.Attributes || (ushort)output.ImplAttributes != (ushort)method.ImplAttributes ||
                    signature.Attributes != (method.IsStatic ? AsmCallingConvention.Default :
                        AsmCallingConvention.Default | AsmCallingConvention.HasThis) || signature.ExplicitThis ||
                    signature.GenericParameterCount != 0 || signature.SentinelParameterTypes.Count != 0 ||
                    output.GenericParameters.Count != 0 || signature.ParameterTypes.Count != method.Parameters.Count ||
                    !SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature())) return false;
                for (var index = 0; index < method.Parameters.Count; index++)
                {
                    var parameter = method.Parameters[index];
                    if (!SignatureComparer.Default.Equals(signature.ParameterTypes[index], parameter.ParameterType.ToTypeSignature()))
                        return false;
                    if (!parameter.ParameterType.IsEnumType) continue;
                    if (!OriginalEnumParameter(method, index) || !EnumOutputTypeValid(parameter.ParameterType)) return false;
                    var definitions = output.ParameterDefinitions.Where(definition => definition.Sequence == index + 1).ToArray();
                    if (definitions is not [var written] || written.Name != parameter.Name ||
                        (ushort)written.Attributes != (ushort)parameter.Attributes || written.Constant != null) return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool EnumOutputTypeValid(TypeAnalysisContext type)
    {
        if (!TryEnumDeclaration(type, out _) || type.Definition is not { } original ||
            type.GetExtraData<TypeDefinition>("AsmResolverType") is not { } output ||
            type.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") is not { ManifestModule: { } module } assembly ||
            !ReferenceEquals(output.DeclaringModule, module) || !ReferenceEquals(module.Assembly, assembly) ||
            module.Name != type.DeclaringAssembly.CleanAssemblyName + ".dll" ||
            assembly.Name != type.DeclaringAssembly.Name || assembly.Version != type.DeclaringAssembly.Version ||
            (assembly.Culture?.ToString() ?? "") != (type.DeclaringAssembly.Culture ?? "") ||
            (uint)assembly.Attributes != type.DeclaringAssembly.Flags ||
            (uint)assembly.HashAlgorithm != type.DeclaringAssembly.HashAlgorithm ||
            !(assembly.PublicKey ?? []).SequenceEqual(type.DeclaringAssembly.PublicKey ?? []) ||
            output.Name != type.Name || (output.Namespace?.ToString() ?? "") != type.Namespace ||
            (uint)output.Attributes != (uint)type.Attributes || output.GenericParameters.Count != 0 ||
            !ReferenceEquals(output.DeclaringType, type.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
            !SignatureComparer.Default.Equals(output.BaseType, type.BaseType!.ToTypeSignature().ToTypeDefOrRef()) ||
            output.Fields.Count != type.Fields.Count || output.Methods.Count != type.Methods.Count ||
            output.Interfaces.Count != type.InterfaceContexts.Count) return false;
        var expected = new TypeDefinition(type.Namespace, type.Name,
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)type.Attributes);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(original, expected);
        if (output.ClassLayout?.PackingSize != expected.ClassLayout?.PackingSize ||
            output.ClassLayout?.ClassSize != expected.ClassLayout?.ClassSize ||
            output.ClassLayout != null && !ReferenceEquals(output.ClassLayout.Parent, output)) return false;
        for (var index = 0; index < type.InterfaceContexts.Count; index++)
            if (!SignatureComparer.Default.Equals(output.Interfaces[index].Interface,
                    type.InterfaceContexts[index].ToTypeSignature().ToTypeDefOrRef())) return false;
        for (var index = 0; index < type.Fields.Count; index++)
        {
            var field = type.Fields[index];
            var written = output.Fields[index];
            if (!ReferenceEquals(field.GetExtraData<FieldDefinition>("AsmResolverField"), written) ||
                !ReferenceEquals(written.DeclaringType, output) || written.Name != field.Name ||
                (ushort)written.Attributes != (ushort)field.Attributes ||
                written.Signature is not { Attributes: AsmCallingConvention.Field } signature ||
                !SignatureComparer.Default.Equals(signature.FieldType, field.FieldType.ToTypeSignature()) ||
                written.FieldOffset != (output.IsExplicitLayout && !field.IsStatic ? field.Offset : (int?)null)) return false;
            if (field.IsStatic)
            {
                if (written.Constant is not { Type: AsmElementType.I4 } constant ||
                    constant.InterpretData() is not int value || !Equals(value, field.ConstantValue)) return false;
            }
            else if (written.Constant != null) return false;
        }
        return true;
    }
}
