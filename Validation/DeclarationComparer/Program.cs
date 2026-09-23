using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeclarationComparer;

internal static class Program
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            var root = Directory.GetCurrentDirectory();
            if (!Directory.Exists(Path.Combine(root, "LibCpp2IL")))
                throw new ArgumentException("Run the comparer from the repository root.");
            var output = Path.GetFullPath(Required(options, "--output"));
            var privateRoot = Path.Combine(root, "Files") + Path.DirectorySeparatorChar;
            if (!output.StartsWith(privateRoot, StringComparison.Ordinal) || Directory.Exists(output) || File.Exists(output))
                throw new ArgumentException("--output must be a new directory under this repository's ignored Files/.");
            var ignored = new ProcessStartInfo("git") { UseShellExecute = false };
            ignored.ArgumentList.Add("check-ignore");
            ignored.ArgumentList.Add("--quiet");
            ignored.ArgumentList.Add(output);
            using (var process = Process.Start(ignored)!)
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new ArgumentException("The output directory must be gitignored.");
            }
            var references = options.GetValueOrDefault("--reference-dir", []).Select(Path.GetFullPath).ToArray();
            var oraclePath = Path.GetFullPath(Required(options, "--oracle"));
            var candidatePath = Path.GetFullPath(Required(options, "--candidate"));
            var oracle = Project(oraclePath, references);
            var candidate = Project(candidatePath, references);
            var differences = Compare(oracle, candidate);
            Projection? unstripped = null;
            List<Difference>? strippingDifferences = null;
            if (options.TryGetValue("--unstripped", out var original))
            {
                unstripped = Project(Path.GetFullPath(original.Single()), references);
                strippingDifferences = Compare(unstripped, oracle);
            }
            var passed = oracle.Diagnostics.Count == 0 && candidate.Diagnostics.Count == 0 &&
                         (unstripped?.Diagnostics.Count ?? 0) == 0 && differences.Count == 0;
            Directory.CreateDirectory(output);
            Write(Path.Combine(output, "oracle.json"), oracle);
            Write(Path.Combine(output, "candidate.json"), candidate);
            if (unstripped != null)
                Write(Path.Combine(output, "unstripped.json"), unstripped);
            var report = new
            {
                status = passed ? "passed" : "failed",
                scope = "Managed declaration projection including custom-attribute constructor identity; no input assembly loading/execution, IL validity or behavior claim.",
                ignoredPhysicalDetails = new[] { "metadata row/token order", "MVID", "method body/RVA", "debug symbols", "assembly file hash equality" },
                oracle = new { path = oraclePath, sha256 = Hash(oraclePath), counts = oracle.Counts },
                candidate = new { path = candidatePath, sha256 = Hash(candidatePath), counts = candidate.Counts },
                referenceDirectories = references,
                differenceCount = differences.Count,
                differences,
                diagnostics = oracle.Diagnostics.Concat(candidate.Diagnostics).ToArray(),
                stripping = unstripped == null ? null : new
                {
                    countsBefore = unstripped.Counts,
                    countsAfter = oracle.Counts,
                    lostIdentities = unstripped.Identities.Except(oracle.Identities, StringComparer.Ordinal).Order().ToArray(),
                    introducedIdentities = oracle.Identities.Except(unstripped.Identities, StringComparer.Ordinal).Order().ToArray(),
                    differences = strippingDifferences,
                    diagnostics = unstripped.Diagnostics
                }
            };
            Write(Path.Combine(output, "report.json"), report);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.status, report.scope, report.differenceCount,
                oracleCounts = oracle.Counts, candidateCounts = candidate.Counts,
                lostByStripping = unstripped?.Identities.Except(oracle.Identities, StringComparer.Ordinal).Count(),
                diagnosticCount = oracle.Diagnostics.Count + candidate.Diagnostics.Count + (unstripped?.Diagnostics.Count ?? 0)
            }, Pretty));
            return passed ? 0 : 1;
        }
        catch (Exception error) when (error is ArgumentException or IOException or BadImageFormatException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }

    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allowed = new HashSet<string> { "--oracle", "--candidate", "--unstripped", "--reference-dir", "--output" };
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || i + 1 == args.Length)
                throw new ArgumentException("Options: --oracle FILE --candidate FILE [--unstripped FILE] [--reference-dir DIR] --output NEW_FILES_DIRECTORY");
            if (!result.TryGetValue(args[i], out var values))
                result[args[i]] = values = [];
            if (values.Count != 0 && args[i] != "--reference-dir")
                throw new ArgumentException("Duplicate option: " + args[i]);
            values.Add(args[i + 1]);
        }
        return result;
    }

    private static string Required(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values.Single() : throw new ArgumentException("Missing " + name);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Pretty) + "\n");
    private static string Json(object? value) => JsonSerializer.Serialize(value);
    private sealed record Projection(SortedDictionary<string, string> Facts, SortedDictionary<string, int> Counts,
        SortedSet<string> Identities, List<string> Diagnostics);
    private sealed record Difference(string Kind, string Key, string? Oracle, string? Candidate);

    private static List<Difference> Compare(Projection oracle, Projection candidate)
    {
        var result = new List<Difference>();
        foreach (var key in oracle.Facts.Keys.Union(candidate.Facts.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var expected = oracle.Facts.GetValueOrDefault(key);
            var actual = candidate.Facts.GetValueOrDefault(key);
            if (expected != actual)
                result.Add(new Difference(expected == null ? "unexpected" : actual == null ? "missing" : "changed", key, expected, actual));
        }
        return result;
    }

    private static Projection Project(string path, string[] references)
    {
        using var image = new MetadataImage(path);
        using var types = new TypeNames(image.Reader, references);
        var reader = image.Reader;
        var result = new Projection(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), []);
        void Count(string category) => result.Counts[category] = result.Counts.GetValueOrDefault(category) + 1;
        void Fact(string key, object? value) => result.Facts.Add(key, Json(value));
        void Identity(string key, string category) { result.Identities.Add(key); Count(category); }
        void Attributes(string owner, CustomAttributeHandleCollection handles)
        {
            var attributes = new List<string>();
            foreach (var handle in handles)
            {
                Count("customAttributes");
                try
                {
                    var attribute = reader.GetCustomAttribute(handle);
                    var decoded = attribute.DecodeValue(types);
                    attributes.Add(Json(new
                    {
                        type = types.AttributeType(reader, attribute.Constructor),
                        constructor = types.Member(reader, attribute.Constructor),
                        fixedArguments = decoded.FixedArguments.Select(Argument).ToArray(),
                        namedArguments = decoded.NamedArguments.Select(argument => new
                        {
                            name = argument.Name, kind = argument.Kind.ToString(), type = argument.Type,
                            value = AttributeValue(argument.Value)
                        }).OrderBy(argument => argument.name, StringComparer.Ordinal).ThenBy(argument => argument.kind, StringComparer.Ordinal).ToArray()
                    }));
                }
                catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or IOException)
                {
                    result.Diagnostics.Add(owner + ": custom attribute decode unavailable: " + error.Message);
                }
            }
            Fact(owner + "/attributes", attributes.Order(StringComparer.Ordinal).ToArray());
        }
        void Constant(string owner, ConstantHandle handle)
        {
            if (handle.IsNil) { Fact(owner + "/constant", null); return; }
            var constant = reader.GetConstant(handle);
            Fact(owner + "/constant", new { type = constant.TypeCode.ToString(), bytes = Convert.ToHexString(reader.GetBlobBytes(constant.Value)) });
        }
        void Generics(string owner, GenericParameterHandleCollection handles)
        {
            foreach (var handle in handles)
            {
                Count("genericParameters");
                var parameter = reader.GetGenericParameter(handle);
                var key = owner + "/generic/" + parameter.Index;
                Fact(key, new
                {
                    name = reader.GetString(parameter.Name), flags = (int)parameter.Attributes,
                    constraints = parameter.GetConstraints().Select(constraint => types.Entity(reader,
                        reader.GetGenericParameterConstraint(constraint).Type)).Order(StringComparer.Ordinal).ToArray()
                });
                Attributes(key, parameter.GetCustomAttributes());
            }
        }
        string MethodKey(MethodDefinitionHandle handle)
        {
            if (handle.IsNil) return "";
            var method = reader.GetMethodDefinition(handle);
            return "method:" + types.Entity(reader, method.GetDeclaringType()) + "::" + reader.GetString(method.Name) +
                   TypeNames.Signature(method.DecodeSignature(types, (object?)null));
        }
        var assembly = reader.GetAssemblyDefinition();
        Fact("assembly", new
        {
            name = reader.GetString(assembly.Name), version = assembly.Version.ToString(),
            culture = reader.GetString(assembly.Culture), flags = (int)assembly.Flags,
            publicKey = Convert.ToHexString(reader.GetBlobBytes(assembly.PublicKey))
        });
        Attributes("assembly", assembly.GetCustomAttributes());
        foreach (var referenceHandle in reader.AssemblyReferences)
        {
            Count("assemblyReferences");
            var reference = reader.GetAssemblyReference(referenceHandle);
            Fact("reference:" + reader.GetString(reference.Name), new
            {
                version = reference.Version.ToString(), culture = reader.GetString(reference.Culture),
                flags = (int)reference.Flags, publicKeyOrToken = Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken))
            });
        }
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) == "<Module>") continue;
            var key = "type:" + types.Entity(reader, typeHandle);
            Identity(key, "types");
            var layout = type.GetLayout();
            Fact(key, new
            {
                flags = (int)type.Attributes, baseType = types.Entity(reader, type.BaseType),
                interfaces = type.GetInterfaceImplementations().Select(handle =>
                    types.Entity(reader, reader.GetInterfaceImplementation(handle).Interface)).Order(StringComparer.Ordinal).ToArray(),
                packing = layout.PackingSize, size = layout.Size,
                methodImplementations = type.GetMethodImplementations().Select(handle =>
                {
                    var implementation = reader.GetMethodImplementation(handle);
                    return types.Member(reader, implementation.MethodBody) + " => " + types.Member(reader, implementation.MethodDeclaration);
                }).Order(StringComparer.Ordinal).ToArray()
            });
            Attributes(key, type.GetCustomAttributes());
            Generics(key, type.GetGenericParameters());
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var fieldKey = key + "/field:" + reader.GetString(field.Name);
                Identity(fieldKey, "fields");
                Fact(fieldKey, new { type = field.DecodeSignature(types, (object?)null), flags = (int)field.Attributes,
                    offset = field.GetOffset(), marshal = Convert.ToHexString(reader.GetBlobBytes(field.GetMarshallingDescriptor())) });
                Constant(fieldKey, field.GetDefaultValue());
                Attributes(fieldKey, field.GetCustomAttributes());
            }
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var methodKey = MethodKey(methodHandle);
                Identity(methodKey, "methods");
                var import = method.GetImport();
                Fact(methodKey, new { flags = (int)method.Attributes, implementationFlags = (int)method.ImplAttributes,
                    importName = import.Name.IsNil ? null : reader.GetString(import.Name),
                    importModule = import.Module.IsNil ? null : reader.GetString(reader.GetModuleReference(import.Module).Name),
                    importFlags = (int)import.Attributes });
                Generics(methodKey, method.GetGenericParameters());
                Attributes(methodKey, method.GetCustomAttributes());
                foreach (var parameterHandle in method.GetParameters())
                {
                    Count("parameters");
                    var parameter = reader.GetParameter(parameterHandle);
                    var parameterKey = methodKey + "/parameter:" + parameter.SequenceNumber;
                    Fact(parameterKey, new { name = reader.GetString(parameter.Name), flags = (int)parameter.Attributes,
                        marshal = Convert.ToHexString(reader.GetBlobBytes(parameter.GetMarshallingDescriptor())) });
                    Constant(parameterKey, parameter.GetDefaultValue());
                    Attributes(parameterKey, parameter.GetCustomAttributes());
                }
            }
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var propertyKey = key + "/property:" + reader.GetString(property.Name) + TypeNames.Signature(property.DecodeSignature(types, (object?)null));
                Identity(propertyKey, "properties");
                var accessors = property.GetAccessors();
                Fact(propertyKey, new { flags = (int)property.Attributes, getter = MethodKey(accessors.Getter),
                    setter = MethodKey(accessors.Setter), others = accessors.Others.Select(MethodKey).Order(StringComparer.Ordinal).ToArray() });
                Constant(propertyKey, property.GetDefaultValue());
                Attributes(propertyKey, property.GetCustomAttributes());
            }
            foreach (var eventHandle in type.GetEvents())
            {
                var item = reader.GetEventDefinition(eventHandle);
                var eventKey = key + "/event:" + reader.GetString(item.Name);
                Identity(eventKey, "events");
                var accessors = item.GetAccessors();
                Fact(eventKey, new { flags = (int)item.Attributes, type = types.Entity(reader, item.Type),
                    add = MethodKey(accessors.Adder), remove = MethodKey(accessors.Remover), raise = MethodKey(accessors.Raiser),
                    others = accessors.Others.Select(MethodKey).Order(StringComparer.Ordinal).ToArray() });
                Attributes(eventKey, item.GetCustomAttributes());
            }
        }
        return result;
    }

    private static object Argument(CustomAttributeTypedArgument<string> argument) =>
        new { type = argument.Type, value = AttributeValue(argument.Value) };
    private static object? AttributeValue(object? value) => value is ImmutableArray<CustomAttributeTypedArgument<string>> items
        ? items.Select(Argument).ToArray() : value;
}

internal sealed class MetadataImage : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _pe;
    public MetadataReader Reader { get; }
    public MetadataImage(string path)
    {
        _stream = File.OpenRead(path);
        _pe = new PEReader(_stream);
        Reader = _pe.GetMetadataReader();
        if (!Reader.IsAssembly) throw new BadImageFormatException("Expected a managed assembly.");
    }
    public void Dispose() { _pe.Dispose(); _stream.Dispose(); }
}

internal sealed class TypeNames(MetadataReader primary, string[] references) :
    ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>, IDisposable
{
    private readonly Dictionary<AssemblyIdentity, MetadataImage> _references = new();
    private sealed record AssemblyIdentity(string Name, Version Version, string Culture, string PublicKeyToken);
    private static string Token(MetadataReader reader, BlobHandle handle, bool fullKey)
    {
        var bytes = reader.GetBlobBytes(handle);
        if (fullKey && bytes.Length != 0)
            bytes = SHA1.HashData(bytes)[^8..].Reverse().ToArray();
        return Convert.ToHexString(bytes);
    }
    private static AssemblyIdentity Identity(MetadataReader reader)
    {
        var assembly = reader.GetAssemblyDefinition();
        return new(reader.GetString(assembly.Name), assembly.Version, reader.GetString(assembly.Culture), Token(reader, assembly.PublicKey, true));
    }
    private static AssemblyIdentity Identity(MetadataReader reader, AssemblyReferenceHandle handle)
    {
        var assembly = reader.GetAssemblyReference(handle);
        return new(reader.GetString(assembly.Name), assembly.Version, reader.GetString(assembly.Culture),
            Token(reader, assembly.PublicKeyOrToken, (assembly.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0));
    }
    private AssemblyIdentity ReferencedIdentity(string name)
    {
        if (name == Assembly(primary))
            return Identity(primary);
        var identities = primary.AssemblyReferences.Select(handle => Identity(primary, handle)).Where(i => i.Name == name).Distinct().ToArray();
        if (identities.Length != 1)
            throw new BadImageFormatException("Enum reference requires one declared assembly identity: " + name);
        return identities[0];
    }
    private MetadataReader Resolve(AssemblyIdentity identity)
    {
        if (identity == Identity(primary))
            return primary;
        if (_references.TryGetValue(identity, out var resolved))
            return resolved.Reader;
        if (identity.Name.IndexOfAny(['/', '\\']) >= 0)
            throw new BadImageFormatException("Enum assembly identity contains a path separator.");
        var matches = new List<string>();
        foreach (var path in references.Select(directory => Path.Combine(directory, identity.Name + ".dll")).Where(File.Exists).Distinct())
        {
            using var image = new MetadataImage(path);
            if (Identity(image.Reader) == identity)
                matches.Add(path);
        }
        if (matches.Count != 1)
            throw new BadImageFormatException("Enum reference requires exactly one explicit identity match: " + identity.Name);
        resolved = new MetadataImage(matches[0]);
        _references.Add(identity, resolved);
        return resolved.Reader;
    }
    private static string Assembly(MetadataReader reader) => reader.GetString(reader.GetAssemblyDefinition().Name);
    private static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        return definition.GetDeclaringType().IsNil
            ? (reader.GetString(definition.Namespace) is { Length: > 0 } space ? space + "." : "") + name
            : DefinitionName(reader, definition.GetDeclaringType()) + "+" + name;
    }
    public string Entity(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        _ when handle.IsNil => "",
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
        _ => throw new BadImageFormatException("Unsupported type handle: " + handle.Kind)
    };
    public string Member(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            return Entity(reader, method.GetDeclaringType()) + "::" + reader.GetString(method.Name) + Signature(method.DecodeSignature(this, (object?)null));
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)handle);
            return Entity(reader, member.Parent) + "::" + reader.GetString(member.Name) + Signature(member.DecodeMethodSignature(this, (object?)null));
        }
        throw new BadImageFormatException("Unsupported method handle: " + handle.Kind);
    }
    public string AttributeType(MetadataReader reader, EntityHandle constructor) => constructor.Kind switch
    {
        HandleKind.MethodDefinition => Entity(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
        HandleKind.MemberReference => Entity(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
        _ => throw new BadImageFormatException("Unsupported attribute constructor.")
    };
    public static string Signature(MethodSignature<string> signature) =>
        ";arity=" + signature.GenericParameterCount + "(" + string.Join(",", signature.ParameterTypes) + ")->" + signature.ReturnType +
        ";header=" + signature.Header.RawValue + ";required=" + signature.RequiredParameterCount;
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[rank=" + shape.Rank +
        ";sizes=" + string.Join(",", shape.Sizes) + ";lower=" + string.Join(",", shape.LowerBounds) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fn" + Signature(signature);
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        (isRequired ? "modreq(" : "modopt(") + modifier + ")" + unmodifiedType;
    public string GetPinnedType(string elementType) => "pinned " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Kind(rawTypeKind) + "[" + Assembly(reader) + "]" + DefinitionName(reader, handle);
    private static string Kind(byte rawTypeKind) => rawTypeKind switch { 0x11 => "valuetype:", 0x12 => "class:", _ => "" };
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return Kind(rawTypeKind) + GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, 0) + "+" + name;
        var assembly = type.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) : Assembly(reader);
        var space = reader.GetString(type.Namespace);
        return Kind(rawTypeKind) + "[" + assembly + "]" + (space.Length == 0 ? "" : space + ".") + name;
    }
    public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    public string GetSystemType() => "[mscorlib]System.Type";
    public bool IsSystemType(string type) => type.EndsWith("]System.Type", StringComparison.Ordinal);
    public string GetTypeFromSerializedName(string name) => "serialized:" + name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type)
    {
        AssemblyIdentity? serializedIdentity = null;
        if (type.StartsWith("serialized:", StringComparison.Ordinal))
        {
            var serialized = type["serialized:".Length..];
            var comma = serialized.IndexOf(',');
            var qualified = comma < 0 ? null : new System.Reflection.AssemblyName(serialized[(comma + 1)..].Trim());
            var assemblyName = qualified?.Name ?? Assembly(primary);
            if (qualified?.Version != null)
                serializedIdentity = new(assemblyName, qualified.Version, qualified.CultureName ?? "", Convert.ToHexString(qualified.GetPublicKeyToken() ?? []));
            type = "[" + assemblyName + "]" + (comma < 0 ? serialized : serialized[..comma]).Trim();
        }
        if (type.StartsWith("valuetype:", StringComparison.Ordinal))
            type = type["valuetype:".Length..];
        var close = type.IndexOf(']');
        if (!type.StartsWith('[') || close < 2) throw new BadImageFormatException("Cannot resolve enum type: " + type);
        var assembly = type[1..close];
        var name = type[(close + 1)..];
        var reader = Resolve(serializedIdentity ?? ReferencedIdentity(assembly));
        foreach (var handle in reader.TypeDefinitions)
        {
            if (DefinitionName(reader, handle) != name) continue;
            foreach (var fieldHandle in reader.GetTypeDefinition(handle).GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (reader.GetString(field.Name) == "value__" && Enum.TryParse<PrimitiveTypeCode>(field.DecodeSignature(this, (object?)null), out var underlying))
                    return underlying;
            }
        }
        throw new BadImageFormatException("Enum storage is unavailable: " + type);
    }
    public void Dispose() { foreach (var image in _references.Values) image.Dispose(); }
}
