using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64VirtualScalarZeroReturnProof
{
    internal static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition output)
    {
        try
        {
            if (method.GetExtraData<Evidence>(EvidenceKey) is not { } proof || !proof.IsUnchanged() ||
                !ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output)) return false;
            foreach (var context in proof.Types)
                if (!MatchesType(context)) return false;
            foreach (var alias in proof.Aliases)
            {
                if (alias.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } declaration ||
                    !MatchesMethod(alias, declaration) || alias.DeclaringType!.GetExtraData<TypeDefinition>("AsmResolverType") is not { } owner)
                    return false;
                // Only the observed same-name public implicit relation is
                // admitted. An added explicit row changes the output contract.
                if (owner.MethodImplementations.Any(row => ReferenceEquals(row.Body, declaration) ||
                    SignatureComparer.Default.Equals(row.Body, declaration))) return false;
                foreach (var target in alias.Overrides)
                    if (target.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } contract || !MatchesMethod(target, contract)) return false;
                if (alias.BaseMethod is { } baseMethod &&
                    (baseMethod.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } baseOutput || !MatchesMethod(baseMethod, baseOutput))) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool MatchesType(TypeAnalysisContext context)
    {
        if (context.Definition is not { } original || context.GetExtraData<TypeDefinition>("AsmResolverType") is not { } declaration ||
            context.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") is not { ManifestModule: { } module } assembly ||
            !ReferenceEquals(declaration.DeclaringModule, module) || !ReferenceEquals(module.Assembly, assembly) ||
            module.Name != context.DeclaringAssembly.CleanAssemblyName + ".dll" ||
            assembly.Name != context.DeclaringAssembly.Name || assembly.Version != context.DeclaringAssembly.Version ||
            (assembly.Culture?.ToString() ?? "") != (context.DeclaringAssembly.Culture ?? "") ||
            (uint)assembly.Attributes != context.DeclaringAssembly.Flags ||
            (uint)assembly.HashAlgorithm != context.DeclaringAssembly.HashAlgorithm ||
            !(assembly.PublicKey ?? []).SequenceEqual(context.DeclaringAssembly.PublicKey ?? []) ||
            declaration.Name != context.Name || (declaration.Namespace?.ToString() ?? "") != context.Namespace ||
            (uint)declaration.Attributes != (uint)context.Attributes || declaration.GenericParameters.Count != 0 ||
            !ReferenceEquals(declaration.DeclaringType, context.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
            !SignatureComparer.Default.Equals(declaration.BaseType, context.BaseType?.ToTypeSignature().ToTypeDefOrRef()) ||
            declaration.Interfaces.Count != context.InterfaceContexts.Count || declaration.Methods.Count != context.Methods.Count ||
            declaration.Properties.Count < context.Properties.Count) return false;
        var layout = new TypeDefinition(context.Namespace, context.Name,
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)context.Attributes);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(original, layout);
        if (declaration.ClassLayout?.ClassSize != layout.ClassLayout?.ClassSize ||
            declaration.ClassLayout?.PackingSize != layout.ClassLayout?.PackingSize ||
            declaration.ClassLayout != null && !ReferenceEquals(declaration.ClassLayout.Parent, declaration)) return false;
        for (var ordinal = 0; ordinal < context.InterfaceContexts.Count; ordinal++)
            if (!SignatureComparer.Default.Equals(declaration.Interfaces[ordinal].Interface,
                    context.InterfaceContexts[ordinal].ToTypeSignature().ToTypeDefOrRef())) return false;
        for (var ordinal = 0; ordinal < context.Methods.Count; ordinal++)
            if (!MatchesMethod(context.Methods[ordinal], declaration.Methods[ordinal])) return false;
        for (var ordinal = 0; ordinal < context.Properties.Count; ordinal++)
        {
            var property = context.Properties[ordinal];
            var written = declaration.Properties[ordinal];
            if (!ReferenceEquals(property.GetExtraData<PropertyDefinition>("AsmResolverProperty"), written) ||
                !ReferenceEquals(written.DeclaringType, declaration) || written.Name != property.Name ||
                (ushort)written.Attributes != (ushort)property.Attributes ||
                written.Signature is not { } signature || signature.HasThis != !property.IsStatic ||
                signature.Attributes != (CallingConventionAttributes.Property | (property.IsStatic ? 0 : CallingConventionAttributes.HasThis)) ||
                !SignatureComparer.Default.Equals(signature.ReturnType, property.PropertyType.ToTypeSignature()) ||
                !ReferenceEquals(written.GetMethod, property.Getter?.GetExtraData<MethodDefinition>("AsmResolverMethod")) ||
                !ReferenceEquals(written.SetMethod, property.Setter?.GetExtraData<MethodDefinition>("AsmResolverMethod"))) return false;
            var parameters = (property.Getter ?? property.Setter)!.Parameters;
            var count = parameters.Count - (property.Getter == null ? 1 : 0);
            if (signature.ParameterTypes.Count != count) return false;
            for (var index = 0; index < count; index++)
                if (!SignatureComparer.Default.Equals(signature.ParameterTypes[index], parameters[index].ParameterType.ToTypeSignature())) return false;
            if (written.Semantics.Count != (property.Getter == null ? 0 : 1) + (property.Setter == null ? 0 : 1)) return false;
        }
        for (var ordinal = context.Properties.Count; ordinal < declaration.Properties.Count; ordinal++)
            if (!MatchesTransportGetter(context, declaration.Properties[ordinal])) return false;
        return true;
    }

    // The existing populator transports an explicit getter as a property when
    // IL2CPP omits its property row. Qualify the observed slot relation and
    // exact generated shape; original property/MethodImpl row presence remains
    // unavailable and is reported independently of this leaf's body recovery.
    private static bool MatchesTransportGetter(TypeAnalysisContext context, PropertyDefinition property)
    {
        if (context.Definition is not { } original || property.GetMethod is not { } getter || property.SetMethod != null ||
            property.Semantics.Count != 1 || property.CustomAttributes.Count != 0 || property.Constant != null ||
            !HasOriginalTransportDispatchRows(context) ||
            context.Methods.Where(method => ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), getter)).ToArray() is not [var body] ||
            context.Properties.Any(member => ReferenceEquals(member.Getter, body)) || !MatchesMethod(body, getter) ||
            !ReferenceEquals(property.DeclaringType, getter.DeclaringType)) return false;
        var targets = new HashSet<MethodAnalysisContext>();
        var table = original.VTable;
        for (var slot = 0; slot < table.Length; slot++)
        {
            if (table[slot] is not { Type: MetadataUsageType.MethodDef, IsValid: true } usage ||
                !ReferenceEquals(usage.AsMethod(), body.Definition)) continue;
            foreach (var row in original.InterfaceOffsets)
            {
                if (X64OriginalReferenceClassProof.ResolveClass(context.AppContext, row.Type) is not { IsInterface: true } contract) return false;
                var relative = slot - row.offset;
                foreach (var target in contract.Methods.Where(method => method.Definition!.slot == relative)) targets.Add(target);
            }
        }
        var matches = 0;
        foreach (var target in targets)
        {
            if (target.DeclaringType is not { } contract || target.Name == body.Name &&
                (body.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Private ||
                contract.Properties.Where(member => ReferenceEquals(member.Getter, target)).ToArray() is not [var source] ||
                source.Setter != null || target.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } interfaceMethod ||
                !MatchesMethod(target, interfaceMethod) || !SameSignature(body, target) ||
                property.Name != contract.ToTypeSignature().FullName + "." + source.Name) continue;
            if (getter.DeclaringType!.MethodImplementations.Where(row =>
                    ReferenceEquals(row.Body, getter) || SignatureComparer.Default.Equals(row.Body, getter)).ToArray() is not [var implementation] ||
                !SignatureComparer.Default.Equals(implementation.Declaration, interfaceMethod) ||
                (ushort)property.Attributes != (ushort)source.Attributes ||
                property.Signature is not { } signature ||
                signature.Attributes != (CallingConventionAttributes.Property | (body.IsStatic ? 0 : CallingConventionAttributes.HasThis)) ||
                signature.ParameterTypes.Count != body.Parameters.Count ||
                !SignatureComparer.Default.Equals(signature.ReturnType, body.ReturnType.ToTypeSignature())) return false;
            for (var ordinal = 0; ordinal < body.Parameters.Count; ordinal++)
                if (!SignatureComparer.Default.Equals(signature.ParameterTypes[ordinal], body.Parameters[ordinal].ParameterType.ToTypeSignature())) return false;
            matches++;
        }
        return matches == 1;
    }

    private static bool HasOriginalTransportDispatchRows(TypeAnalysisContext context)
    {
        var metadata = context.AppContext.Metadata;
        var definition = context.Definition!;
        var vtable = metadata.metadataHeader.vtableMethods;
        var offsets = metadata.metadataHeader.interfaceOffsets;
        if (definition.VtableStart < 0 || definition.VtableStart > vtable.Size / 4 - definition.VtableCount ||
            definition.InterfaceOffsetsCount != 0 && (definition.InterfaceOffsetsStart.Value < 0 ||
                definition.InterfaceOffsetsStart.Value > offsets.Size / 8 - definition.InterfaceOffsetsCount)) return false;
        for (var slot = 0; slot < definition.VtableCount; slot++)
        {
            var index = definition.VtableStart + slot;
            if (metadata.VTableMethodIndices[index] != metadata.ReadClassArrayAtRawAddr<uint>(vtable.Offset + (long)index * 4, 1)[0]) return false;
        }
        var rows = definition.InterfaceOffsets;
        if (rows.Length != definition.InterfaceOffsetsCount) return false;
        for (var ordinal = 0; ordinal < rows.Length; ordinal++)
        {
            var index = definition.InterfaceOffsetsStart.Value + ordinal;
            var raw = metadata.ReadClassArrayAtRawAddr<int>(offsets.Offset + (long)index * 8, 2);
            if (raw[0] != rows[ordinal].typeIndex.Value || raw[1] != rows[ordinal].offset) return false;
        }
        return true;
    }

    private static bool MatchesMethod(MethodAnalysisContext context, MethodDefinition declaration)
    {
        if (!ReferenceEquals(context.GetExtraData<MethodDefinition>("AsmResolverMethod"), declaration) ||
            !ReferenceEquals(declaration.DeclaringType, context.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
            declaration.Name != context.Name || (ushort)declaration.Attributes != (ushort)context.Attributes ||
            (ushort)declaration.ImplAttributes != (ushort)context.ImplAttributes ||
            declaration.GenericParameters.Count != context.GenericParameters.Count ||
            declaration.Signature is not { ExplicitThis: false, SentinelParameterTypes.Count: 0 } signature ||
            signature.Attributes != (CallingConventionAttributes.Default | (context.IsStatic ? 0 : CallingConventionAttributes.HasThis) |
                (context.GenericParameters.Count == 0 ? 0 : CallingConventionAttributes.Generic)) ||
            signature.GenericParameterCount != context.GenericParameters.Count ||
            signature.ParameterTypes.Count != context.Parameters.Count ||
            !SignatureComparer.Default.Equals(signature.ReturnType, context.ReturnType.ToTypeSignature()) ||
            declaration.ParameterDefinitions.Count != context.Parameters.Count) return false;
        for (var ordinal = 0; ordinal < context.Parameters.Count; ordinal++)
        {
            var parameter = context.Parameters[ordinal];
            var written = declaration.ParameterDefinitions[ordinal];
            if (written.Sequence != ordinal + 1 || written.Name != parameter.Name ||
                (ushort)written.Attributes != (ushort)parameter.Attributes ||
                !SignatureComparer.Default.Equals(signature.ParameterTypes[ordinal], parameter.ParameterType.ToTypeSignature())) return false;
        }
        return true;
    }
}
