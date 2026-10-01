using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using ICSharpCode.Decompiler.Metadata;
using AssemblyReference = System.Reflection.Metadata.AssemblyReference;
using TypeDefinition = System.Reflection.Metadata.TypeDefinition;
using ModuleDefinition = AsmResolver.DotNet.ModuleDefinition;
using TypeSpecification = AsmResolver.DotNet.TypeSpecification;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Separates the supplied unityaot runtime definitions used by the decompiler from
/// the NET_Unity_4_8 API definitions used by the source compiler. This is not an
/// assembly redirect or a claim that every API in the two libraries is equivalent.
/// </summary>
internal sealed class UnityTargetReferenceTransport
{
    internal sealed record Binding(string RuntimePath, string CompilerPath,
        AssemblyNameReference RuntimeReference, AssemblyNameReference CompilerReference);

    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);

    internal UnityTargetReferenceTransport(IEnumerable<string> compilerDirectories, IEnumerable<string> runtimeFiles)
    {
        var directories = compilerDirectories.ToArray();
        foreach (var path in runtimeFiles.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            var identity = AssemblyName.GetAssemblyName(path);
            if (!IsRuntimeIdentity(identity.Name ?? "", identity.Version, identity.CultureName,
                    identity.GetPublicKeyToken() ?? []) || identity.GetPublicKey()?.Length is not > 8 ||
                !OrdinaryAssemblyFlags(identity))
                throw new InvalidOperationException("An explicit runtime reference is not a qualified Unity target transport identity.");
            var name = identity.Name!;
            if (_bindings.ContainsKey(name))
                throw new InvalidOperationException($"Multiple explicit runtime references were supplied for {name}.");
            var compilerPaths = directories.Select(directory => Path.Combine(directory, name + ".dll"))
                .Where(File.Exists).Where(candidate => IsCompilerIdentity(AssemblyName.GetAssemblyName(candidate))).ToArray();
            if (compilerPaths.Length != 1)
                throw new InvalidOperationException($"The runtime reference {name} requires one exact compiler-role target reference.");
            var compilerIdentity = AssemblyName.GetAssemblyName(compilerPaths[0]);
            if (compilerIdentity.GetPublicKey()?.Length is not > 8 || !OrdinaryAssemblyFlags(compilerIdentity))
                throw new InvalidOperationException("A compiler-role target reference must retain its full signing key.");
            _bindings.Add(name, new Binding(path, compilerPaths[0], AssemblyNameReference.Parse(identity.FullName!),
                AssemblyNameReference.Parse(compilerIdentity.FullName!)));
        }
    }

    internal bool TryGet(IAssemblyReference reference, out Binding binding)
    {
        if (_bindings.TryGetValue(reference.Name, out var candidate) && SameIdentity(reference, candidate.RuntimeReference))
        {
            binding = candidate;
            return true;
        }
        binding = null!;
        return false;
    }

    private static bool IsCompilerIdentity(AssemblyName identity) =>
        Unity2021TargetFrameworkAssemblies.HasTargetIdentity(identity.Name ?? "", identity.Version,
            identity.CultureName, identity.GetPublicKeyToken() ?? []);

    private static bool OrdinaryAssemblyFlags(AssemblyName identity) =>
        (identity.Flags & ~AssemblyNameFlags.PublicKey) == 0 && identity.ContentType == AssemblyContentType.Default;

    private static bool IsRuntimeIdentity(string name, Version? version, string? culture, ReadOnlySpan<byte> token) =>
        name is "System" or "System.Core" or "System.Xml" &&
        Unity2021TargetFrameworkAssemblies.HasPlayerRuntimeIdentity(name, version, culture, token) &&
        !Unity2021TargetFrameworkAssemblies.HasTargetIdentity(name, version, culture, token);

    private static bool SameIdentity(IAssemblyReference first, IAssemblyReference second) =>
        first.Name == second.Name && first.Version == second.Version &&
        (first.Culture ?? "") == (second.Culture ?? "") &&
        (first.PublicKeyToken ?? []).SequenceEqual(second.PublicKeyToken ?? []);

    internal List<UnityReferenceTransportReport> ValidateConsumedSignatures(ModuleDefinition module, PEFile file,
        ExplicitAssemblyResolver resolver)
    {
        var metadata = file.Metadata;
        var active = metadata.AssemblyReferences.Select(handle => new ICSharpCode.Decompiler.Metadata.AssemblyReference(file, handle))
            .Where(reference => TryGet(reference, out _)).GroupBy(reference => reference.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => _bindings[group.First().Name], StringComparer.Ordinal);
        if (active.Count == 0)
            return [];
        foreach (var name in active.Values.Select(binding => binding.RuntimeReference.Name).Distinct(StringComparer.Ordinal))
        {
            var identities = metadata.AssemblyReferences
                .Select(handle => new ICSharpCode.Decompiler.Metadata.AssemblyReference(file, handle))
                .Where(reference => reference.Name == name).Select(reference => reference.FullName)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length != 1)
                throw new NotSupportedException("SOURCE015: Distinct original identities share one transported assembly name; selecting one compiler role would collapse their original type scopes.");
        }

        var uses = new SignatureUses(metadata, reference => active.ContainsKey(reference.FullName));
        foreach (var handle in metadata.FieldDefinitions)
            metadata.GetFieldDefinition(handle).DecodeSignature(uses, true);
        foreach (var handle in metadata.MethodDefinitions)
            metadata.GetMethodDefinition(handle).DecodeSignature(uses, true);
        foreach (var handle in metadata.PropertyDefinitions)
            metadata.GetPropertyDefinition(handle).DecodeSignature(uses, true);
        uses.SignatureTypesAllowed = false;
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            uses.ReadType(member.Parent, false);
            if (member.GetKind() == MemberReferenceKind.Field)
                member.DecodeFieldSignature(uses, false);
            else
                member.DecodeMethodSignature(uses, false);
        }
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            uses.ReadType(type.BaseType, false);
            foreach (var implementation in type.GetInterfaceImplementations())
                uses.ReadType(metadata.GetInterfaceImplementation(implementation).Interface, false);
        }
        foreach (var handle in metadata.EventDefinitions)
            uses.ReadType(metadata.GetEventDefinition(handle).Type, false);
        foreach (var handle in metadata.ExportedTypes)
        {
            var exported = metadata.GetExportedType(handle);
            if (exported.Implementation.Kind == HandleKind.AssemblyReference &&
                active.ContainsKey(MetadataReference(metadata, (AssemblyReferenceHandle)exported.Implementation).FullName))
                uses.Unsupported = true;
        }
        for (var ordinal = 1; ordinal <= metadata.GetTableRowCount(TableIndex.GenericParamConstraint); ordinal++)
            uses.ReadType(metadata.GetGenericParameterConstraint(MetadataTokens.GenericParameterConstraintHandle(ordinal)).Type, false);
        for (var ordinal = 1; ordinal <= metadata.GetTableRowCount(TableIndex.TypeSpec); ordinal++)
            uses.ReadType(MetadataTokens.TypeSpecificationHandle(ordinal), false);
        for (var ordinal = 1; ordinal <= metadata.GetTableRowCount(TableIndex.MethodSpec); ordinal++)
            metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(ordinal)).DecodeSignature(uses, false);
        for (var ordinal = 1; ordinal <= metadata.GetTableRowCount(TableIndex.StandAloneSig); ordinal++)
        {
            var signature = metadata.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(ordinal));
            if (metadata.GetBlobReader(signature.Signature).ReadSignatureHeader().Kind == SignatureKind.LocalVariables)
            {
                // A plain reference local only stores an opaque alias, as a field
                // or parameter does. Wrapped local types keep their existing gaps.
                uses.SignatureTypesAllowed = true;
                uses.LocalSignature = true;
                try { signature.DecodeLocalSignature(uses, true); }
                finally
                {
                    uses.SignatureTypesAllowed = false;
                    uses.LocalSignature = false;
                }
            }
            else
                signature.DecodeMethodSignature(uses, false);
        }
        foreach (var handle in metadata.TypeReferences)
        {
            if (uses.IsTransported(handle) && !uses.Consumed.Contains(handle))
                uses.Unsupported = true;
        }
        // Signature-only transport does not qualify cast/type-token/catch uses,
        // serialized attribute Type values, or executable members of these types.
        foreach (var type in module.GetAllTypes())
        {
            foreach (var method in type.Methods)
            {
                if (method.CilMethodBody?.Instructions.Any(instruction =>
                        instruction.Operand is ITypeDescriptor operand && IsTransported(operand)) == true)
                    uses.Unsupported = true;
                if (method.CilMethodBody?.ExceptionHandlers.Any(handler => handler.ExceptionType is { } exception && IsTransported(exception)) == true)
                    uses.Unsupported = true;
            }
        }
        foreach (var owner in AttributeOwners(module))
        foreach (var attribute in owner.CustomAttributes)
        {
            var signature = attribute.Signature;
            if (signature == null || signature.FixedArguments.Any(ContainsTransportedValue) ||
                signature.NamedArguments.Any(argument => ContainsTransportedValue(argument.Argument)))
                uses.Unsupported = true;
        }
        if (uses.Unsupported)
            throw new NotSupportedException("SOURCE015: Runtime-to-compiler transport is limited to ordinary reference signatures and opaque reference locals; a consumed API, derivation, constraint, modifier or executable type use is unqualified.");

        var reports = new List<UnityReferenceTransportReport>();
        foreach (var binding in active.Values.Distinct())
        {
            var runtime = resolver.Resolve(binding.RuntimeReference) as PEFile ?? throw new InvalidOperationException("Runtime-role reference is unavailable.");
            var compiler = resolver.ResolveCompilerReference(binding.RuntimeReference);
            var names = new List<string>();
            var localNames = new List<string>();
            foreach (var handle in uses.Consumed)
            {
                var type = metadata.GetTypeReference(handle);
                if (!SameIdentity(uses.Reference(type.ResolutionScope), binding.RuntimeReference))
                    continue;
                var typeNamespace = metadata.GetString(type.Namespace);
                var typeName = metadata.GetString(type.Name);
                if (!MatchingOrdinaryType(runtime.Metadata, compiler.Metadata, typeNamespace, typeName))
                    throw new NotSupportedException($"SOURCE015: {binding.RuntimeReference.Name}: A consumed reference type does not have a matching visible ordinary class declaration in the explicit compiler role.");
                var displayName = typeNamespace + "." + typeName;
                if (uses.DeclarationConsumed.Contains(handle)) names.Add(displayName);
                if (uses.LocalConsumed.Contains(handle)) localNames.Add(displayName);
            }
            reports.Add(new UnityReferenceTransportReport
            {
                Name = binding.RuntimeReference.Name, OriginalIdentity = binding.RuntimeReference.FullName,
                CompilerIdentity = binding.CompilerReference.FullName,
                ConsumedSignatureTypes = names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList(),
                ConsumedLocalSignatureTypes = localNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList(),
            });
        }
        return reports.OrderBy(report => report.Name, StringComparer.Ordinal).ToList();
    }

    private bool IsTransported(ITypeDescriptor type, int depth = 0)
    {
        if (depth >= 32)
            throw new NotSupportedException("SOURCE015: Recursive executable or serialized type payload is outside the transport boundary.");
        if (type is GenericInstanceTypeSignature generic)
            return IsTransported(generic.GenericType, depth + 1) || generic.TypeArguments.Any(argument => IsTransported(argument, depth + 1));
        if (type is TypeSpecificationSignature wrapped)
            return IsTransported(wrapped.BaseType, depth + 1);
        if (type is TypeSpecification specification)
            return specification.Signature != null && IsTransported(specification.Signature, depth + 1);
        if (type.Scope is AsmResolver.DotNet.TypeReference enclosing)
            return IsTransported(enclosing, depth + 1);
        return type.Scope is AsmResolver.DotNet.AssemblyReference reference &&
               UnityTargetAssemblyScope.TryNormalizePublicKey(reference.PublicKeyOrToken, reference.HasPublicKey, out var token) &&
               IsRuntimeIdentity(reference.Name?.ToString() ?? "", reference.Version, reference.Culture?.ToString(), token);
    }

    private bool ContainsTransportedValue(CustomAttributeArgument argument) => argument.Elements.Any(value =>
        value is ITypeDescriptor type && IsTransported(type) || value is CustomAttributeArgument nested && ContainsTransportedValue(nested));

    private static IEnumerable<IHasCustomAttribute> AttributeOwners(ModuleDefinition module)
    {
        yield return module;
        if (module.Assembly != null)
            yield return module.Assembly;
        foreach (var type in module.GetAllTypes())
        {
            yield return type;
            foreach (var parameter in type.GenericParameters) yield return parameter;
            foreach (var field in type.Fields) yield return field;
            foreach (var property in type.Properties) yield return property;
            foreach (var @event in type.Events) yield return @event;
            foreach (var method in type.Methods)
            {
                yield return method;
                foreach (var parameter in method.ParameterDefinitions) yield return parameter;
                foreach (var parameter in method.GenericParameters) yield return parameter;
            }
        }
    }

    private static bool MatchingOrdinaryType(MetadataReader runtime, MetadataReader compiler, string typeNamespace, string name)
    {
        var runtimeTypes = runtime.TypeDefinitions.Where(handle => MatchesTypeName(runtime, runtime.GetTypeDefinition(handle), typeNamespace, name)).ToArray();
        var compilerTypes = compiler.TypeDefinitions.Where(handle => MatchesTypeName(compiler, compiler.GetTypeDefinition(handle), typeNamespace, name)).ToArray();
        if (runtimeTypes.Length != 1 || compilerTypes.Length != 1)
            return false;
        var first = runtime.GetTypeDefinition(runtimeTypes[0]);
        var second = compiler.GetTypeDefinition(compilerTypes[0]);
        const TypeAttributes shape = TypeAttributes.VisibilityMask | TypeAttributes.ClassSemanticsMask |
                                     TypeAttributes.LayoutMask | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.StringFormatMask;
        if ((first.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
            (second.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
            (first.Attributes & (TypeAttributes.ClassSemanticsMask | TypeAttributes.LayoutMask)) != 0 ||
            (first.Attributes & shape) != (second.Attributes & shape) ||
            first.GetGenericParameters().Count != 0 || second.GetGenericParameters().Count != 0 ||
            !first.GetDeclaringType().IsNil || !second.GetDeclaringType().IsNil)
            return false;
        var firstBase = DeclarationTypeKey(runtime, first.BaseType);
        var secondBase = DeclarationTypeKey(compiler, second.BaseType);
        if (firstBase == null || firstBase != secondBase ||
            firstBase.Value.Namespace == "System" && firstBase.Value.Name is "ValueType" or "Enum")
            return false;
        var firstInterfaces = first.GetInterfaceImplementations().Select(handle =>
            DeclarationTypeKey(runtime, runtime.GetInterfaceImplementation(handle).Interface))
            .OrderBy(key => key?.Role, StringComparer.Ordinal).ThenBy(key => key?.Namespace, StringComparer.Ordinal)
            .ThenBy(key => key?.Name, StringComparer.Ordinal).ToArray();
        var secondInterfaces = second.GetInterfaceImplementations().Select(handle =>
            DeclarationTypeKey(compiler, compiler.GetInterfaceImplementation(handle).Interface))
            .OrderBy(key => key?.Role, StringComparer.Ordinal).ThenBy(key => key?.Namespace, StringComparer.Ordinal)
            .ThenBy(key => key?.Name, StringComparer.Ordinal).ToArray();
        return firstInterfaces.All(key => key != null) && firstInterfaces.SequenceEqual(secondInterfaces);
    }

    private static bool MatchesTypeName(MetadataReader reader, TypeDefinition type, string typeNamespace, string name) =>
        reader.GetString(type.Namespace) == typeNamespace && reader.GetString(type.Name) == name;

    private readonly record struct DeclarationIdentity(string Role, string Namespace, string Name);

    private static DeclarationIdentity? DeclarationTypeKey(MetadataReader reader, EntityHandle handle)
    {
        string typeNamespace;
        string name;
        IAssemblyReference reference;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            if (!type.GetDeclaringType().IsNil) return null;
            typeNamespace = reader.GetString(type.Namespace);
            name = reader.GetString(type.Name);
            var assembly = reader.GetAssemblyDefinition();
            reference = ParseReference(reader.GetString(assembly.Name), assembly.Version,
                reader.GetString(assembly.Culture), reader.GetBlobBytes(assembly.PublicKey), true);
        }
        else if (handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) return null;
            reference = MetadataReference(reader, (AssemblyReferenceHandle)type.ResolutionScope);
            typeNamespace = reader.GetString(type.Namespace);
            name = reader.GetString(type.Name);
        }
        else return null;
        var role = IsRuntimeIdentity(reference.Name, reference.Version, reference.Culture, reference.PublicKeyToken ?? []) ||
                   reference.Name is "System" or "System.Core" or "System.Xml" &&
                   Unity2021TargetFrameworkAssemblies.HasTargetIdentity(reference.Name, reference.Version, reference.Culture, reference.PublicKeyToken ?? [])
            ? reference.Name + ":target-transport" : reference.FullName;
        return new DeclarationIdentity(role, typeNamespace, name);
    }

    private static AssemblyNameReference MetadataReference(MetadataReader reader, AssemblyReferenceHandle handle)
    {
        AssemblyReference reference = reader.GetAssemblyReference(handle);
        return ParseReference(reader.GetString(reference.Name), reference.Version, reader.GetString(reference.Culture),
            reader.GetBlobBytes(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0);
    }

    private static AssemblyNameReference ParseReference(string name, Version version, string culture, byte[] key, bool fullKey)
    {
        if (!UnityTargetAssemblyScope.TryNormalizePublicKey(key, fullKey, out var token))
            throw new BadImageFormatException("A target assembly reference has malformed signing metadata.");
        return AssemblyNameReference.Parse(name + ", Version=" + version + ", Culture=" +
            (culture.Length == 0 ? "neutral" : culture) +
            ", PublicKeyToken=" + (token.Length == 0 ? "null" : string.Concat(token.Select(value => value.ToString("x2")))));
    }

    private sealed class SignatureUses(MetadataReader metadata, Func<IAssemblyReference, bool> transported)
        : ISignatureTypeProvider<string, bool>
    {
        internal readonly HashSet<TypeReferenceHandle> Consumed = [];
        internal readonly HashSet<TypeReferenceHandle> DeclarationConsumed = [];
        internal readonly HashSet<TypeReferenceHandle> LocalConsumed = [];
        internal bool Unsupported;
        internal bool SignatureTypesAllowed = true;
        internal bool LocalSignature;
        private int _depth;
        internal IAssemblyReference Reference(EntityHandle scope) => scope.Kind == HandleKind.AssemblyReference
            ? MetadataReference(metadata, (AssemblyReferenceHandle)scope)
            : throw new NotSupportedException("SOURCE015: Nested or module-scoped transported type resolution is unqualified.");
        internal bool IsTransported(TypeReferenceHandle handle)
        {
            var type = metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;
            for (var depth = 0; depth < 32 && scope.Kind == HandleKind.TypeReference; depth++)
                scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference && transported(Reference(scope));
        }
        internal string ReadType(EntityHandle handle, bool allowed) => handle.Kind switch
        {
            HandleKind.TypeReference => GetTypeFromReference(metadata, (TypeReferenceHandle)handle, (byte)SignatureTypeKind.Class, allowed),
            HandleKind.TypeSpecification => GetTypeFromSpecification(metadata, allowed, (TypeSpecificationHandle)handle, 0),
            _ => "local",
        };
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            GetTypeFromReference(reader, handle, rawTypeKind, SignatureTypesAllowed);
        private string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind, bool allowed)
        {
            if (!IsTransported(handle)) return "other";
            Consumed.Add(handle);
            if (allowed)
            {
                if (LocalSignature) LocalConsumed.Add(handle);
                else DeclarationConsumed.Add(handle);
            }
            if (!allowed || rawTypeKind != (byte)SignatureTypeKind.Class ||
                reader.GetTypeReference(handle).ResolutionScope.Kind != HandleKind.AssemblyReference) Unsupported = true;
            return "transport";
        }
        public string GetTypeFromSpecification(MetadataReader reader, bool allowed, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            if (++_depth > 32) throw new BadImageFormatException("Recursive type specifications exceed the transport boundary.");
            try { return Check(reader.GetTypeSpecification(handle).DecodeSignature(this, allowed)); }
            finally { _depth--; }
        }
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "local";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "primitive";
        public string GetGenericMethodParameter(bool context, int index) => "generic";
        public string GetGenericTypeParameter(bool context, int index) => "generic";
        public string GetSZArrayType(string elementType) => Check(elementType);
        public string GetArrayType(string elementType, ArrayShape shape) => Check(elementType);
        public string GetByReferenceType(string elementType) => Check(elementType);
        public string GetPointerType(string elementType) => Check(elementType);
        public string GetPinnedType(string elementType) => Check(elementType);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => Check(modifier + unmodifiedType);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => Check(genericType + string.Concat(typeArguments));
        public string GetFunctionPointerType(MethodSignature<string> signature) => Check(signature.ReturnType + string.Concat(signature.ParameterTypes));
        private string Check(string value)
        {
            if (value.Contains("transport")) Unsupported = true;
            return value;
        }
    }
}
