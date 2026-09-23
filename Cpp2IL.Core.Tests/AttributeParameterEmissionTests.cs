using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AssetRipper.Primitives;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional serialization regression against the authored exact-version native fixture.</summary>
[TestFixture]
[NonParallelizable]
public class AttributeParameterEmissionTests
{
    [Test]
    public void PreservesDeclaredNamedArraysAndBoxedTypeAndArrayValues()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ATTRIBUTE_PARAMETER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ATTRIBUTE_PARAMETER_FIXTURE_INPUT to the exact synthetic AttributeParameterFixture player-input directory.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True,
            "The supplied directory must contain neutral fixture native inputs; no fallback is used.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            Assert.That(app.MetadataVersion, Is.EqualTo(29));
            var assembly = app.GetAssemblyByName("AttributeParameterFixture");
            Assert.That(assembly, Is.Not.Null, "Build the public Validation/AttributeParameterFixture source first.");
            var fixtureTypes = assembly!.Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(fixtureTypes.Select(type => type.Name), Is.EquivalentTo(new[]
            {
                "ByteMode", "ChoiceAttribute", "PayloadAttribute", "ParameterMarkAttribute",
                "BoxedStringCase", "StringCase", "PayloadCases", "ParameterCases",
            }));
            Assert.That(fixtureTypes.Sum(type => type.Methods.Count), Is.EqualTo(15));
            Assert.That(fixtureTypes.Sum(type => type.Fields.Count), Is.EqualTo(8));

            // Build metadata associations before analysis, then convert the actual native attribute
            // payloads. Original source and managed assemblies are never supplied to recovery.
            var managed = new AsmResolverDllOutputFormatDefault().BuildAssemblies(app)
                .Single(candidate => candidate.Name == assembly.Name);
            foreach (var type in fixtureTypes)
                type.AnalyzeCustomAttributeData();
            AsmResolverAssemblyPopulator.PopulateCustomAttributes(assembly);

            using var stream = new MemoryStream();
            managed.WriteManifest(stream, RecoveredAssemblyImageBuilder.Create());
            stream.Position = 0;
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var provider = new FixtureAttributeTypes();
            var payloadHandle = reader.TypeDefinitions.Single(handle => reader.GetString(reader.GetTypeDefinition(handle).Name) == "PayloadCases");
            var attributes = reader.GetTypeDefinition(payloadHandle).GetCustomAttributes()
                .Select(handle => reader.GetCustomAttribute(handle).DecodeValue(provider)).ToArray();
            Assert.That(attributes, Has.Length.EqualTo(8));

            var namedNull = attributes.Single(attribute => attribute.NamedArguments.Any(argument => argument.Name == "Numbers" && argument.Value == null));
            Assert.That(namedNull.FixedArguments[0].Type, Is.EqualTo("System.Type"));
            Assert.That(namedNull.FixedArguments[0].Value?.ToString(), Does.StartWith("System.Int32[]"));
            Assert.That(namedNull.NamedArguments.Single(argument => argument.Name == "Numbers").Type, Is.EqualTo("Int32[]"));
            Assert.That(namedNull.NamedArguments.Single(argument => argument.Name == "Target").Value, Is.Null);

            var namedValues = attributes.Single(attribute => attribute.NamedArguments.Any(argument => argument.Name == "Numbers" && argument.Value != null));
            var numbers = (ImmutableArray<CustomAttributeTypedArgument<string>>)namedValues.NamedArguments.Single(argument => argument.Name == "Numbers").Value!;
            Assert.That(numbers.Select(argument => argument.Value), Is.EqualTo(new object[] { -1, 0, 5 }));
            var boxedType = namedValues.NamedArguments.Single(argument => argument.Name == "Boxed");
            Assert.That(boxedType.Type, Is.EqualTo("System.Type"));
            Assert.That(boxedType.Value?.ToString(), Does.StartWith("System.Int32"));

            var integerArrays = attributes.Where(attribute => attribute.FixedArguments[0].Type == "Int32[]")
                .Select(attribute => (ImmutableArray<CustomAttributeTypedArgument<string>>)attribute.FixedArguments[0].Value!).ToArray();
            Assert.That(integerArrays, Has.Length.EqualTo(2));
            Assert.That(integerArrays.Any(array => array.IsEmpty), Is.True, "An empty array must remain distinct from a null array.");
            Assert.That(integerArrays.Single(array => !array.IsEmpty).Select(argument => argument.Value), Is.EqualTo(new object[] { -7, 0, 9 }));
            Assert.That(attributes.Single(attribute => attribute.FixedArguments[0].Type == "System.Type[]").FixedArguments[0].Value,
                Is.InstanceOf<ImmutableArray<CustomAttributeTypedArgument<string>>>());
            var objects = (ImmutableArray<CustomAttributeTypedArgument<string>>)attributes.Single(attribute => attribute.FixedArguments[0].Type == "Object[]").FixedArguments[0].Value!;
            Assert.That(objects.Select(argument => argument.Type), Is.EqualTo(new[] { "String", "Byte", "AttributeParameterFixture.ByteMode", "System.Type", "String" }));
            Assert.That(objects[2].Value, Is.EqualTo((byte)225));
            Assert.That(objects[4].Value, Is.Null);

            // This synthetic mutation exercises an unresolved case without inventing an array
            // element type from an object owner. The real compiler lowers the fixture's boxed
            // null expression to string-null before IL2CPP, so it is not evidence of this case.
            var payload = assembly.GetTypeByFullName("AttributeParameterFixture.PayloadCases")!;
            var constructor = payload.CustomAttributes![0].Constructor;
            var unknown = new AnalyzedCustomAttribute(constructor);
            unknown.ConstructorParameters.Add(new CustomAttributeArrayParameter(unknown, CustomAttributeParameterKind.ConstructorParam, 0) { IsNullArray = true });
            var error = Assert.Throws<Exception>(() => AsmResolverAssemblyPopulator.ConvertCustomAttribute(unknown));
            Assert.That(error!.ToString(), Does.Contain("ATTRIBUTE001"));
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    [Test]
    public void PreservesEnumArrayIdentityForObjectOwnersAndNamedProperties()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ATTRIBUTE_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ATTRIBUTE_ARRAY_FIXTURE_INPUT to the exact synthetic AttributeArrayFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True, "Missing native fixture inputs; no fallback is used.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            Assert.That(app.MetadataVersion, Is.EqualTo(29));
            Assert.That(app.AssembliesByName.TryGetValue("AttributeArrayFixture", out var assembly), Is.True);
            var fixtureTypes = assembly!.Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(fixtureTypes.Select(type => type.Name), Is.EquivalentTo(new[] { "ByteChoice", "ArrayPayloadAttribute", "ArrayCases" }));
            Assert.That(fixtureTypes.Sum(type => type.Methods.Count), Is.EqualTo(10));
            Assert.That(fixtureTypes.Sum(type => type.Fields.Count), Is.EqualTo(7));
            Assert.That(fixtureTypes.Sum(type => type.Properties.Count), Is.EqualTo(4));

            var managed = new AsmResolverDllOutputFormatDefault().BuildAssemblies(app)
                .Single(candidate => candidate.Name == assembly.Name);
            var cases = assembly.GetTypeByFullName("AttributeArrayFixture.ArrayCases")!;
            cases.AnalyzeCustomAttributeData();
            AsmResolverAssemblyPopulator.PopulateCustomAttributes(assembly);
            using var stream = new MemoryStream();
            managed.WriteManifest(stream, RecoveredAssemblyImageBuilder.Create());
            stream.Position = 0;
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var handle = reader.TypeDefinitions.Single(candidate => reader.GetString(reader.GetTypeDefinition(candidate).Name) == "ArrayCases");
            var attributes = reader.GetTypeDefinition(handle).GetCustomAttributes()
                .Select(candidate => reader.GetCustomAttribute(candidate).DecodeValue(new FixtureAttributeTypes())).ToArray();
            Assert.That(attributes, Has.Length.EqualTo(9));

            const string enumArray = "AttributeArrayFixture.ByteChoice[]";
            var enumArguments = attributes.Where(attribute => attribute.FixedArguments[0].Type == enumArray)
                .Select(attribute => attribute.FixedArguments[0]).ToArray();
            Assert.That(enumArguments, Has.Length.EqualTo(4), "The object-owned array must retain enum identity, not become byte[].");
            Assert.That(enumArguments.Count(argument => argument.Value == null), Is.EqualTo(1));
            var populated = enumArguments.Where(argument => argument.Value != null)
                .Select(argument => (ImmutableArray<CustomAttributeTypedArgument<string>>)argument.Value!).ToArray();
            Assert.That(populated.Count(array => array.IsEmpty), Is.EqualTo(1));
            Assert.That(populated.Single(array => array.Length == 2).Select(argument => argument.Value), Is.EqualTo(new object[] { (byte)0, (byte)210 }));
            Assert.That(populated.Single(array => array.Length == 1)[0].Value, Is.EqualTo((byte)210));

            var boxedEnum = NamedCase("enums").NamedArguments.Single(argument => argument.Name == "Boxed");
            Assert.That(boxedEnum.Type, Is.EqualTo(enumArray));
            Assert.That(((ImmutableArray<CustomAttributeTypedArgument<string>>)boxedEnum.Value!)[0].Value, Is.EqualTo((byte)210));
            var nulls = NamedCase("nulls");
            Assert.That(nulls.NamedArguments.Single(argument => argument.Name == "Modes").Type, Is.EqualTo(enumArray));
            Assert.That(nulls.NamedArguments.All(argument => argument.Value == null), Is.True);
            var values = NamedCase("values");
            Assert.That(((ImmutableArray<CustomAttributeTypedArgument<string>>)values.NamedArguments.Single(argument => argument.Name == "Numbers").Value!).IsEmpty, Is.True);
            Assert.That(values.NamedArguments.Single(argument => argument.Name == "Modes").Type, Is.EqualTo(enumArray));
            var objects = (ImmutableArray<CustomAttributeTypedArgument<string>>)values.NamedArguments.Single(argument => argument.Name == "Boxed").Value!;
            Assert.That(objects.Select(argument => argument.Type), Is.EqualTo(new[] { "Byte", "AttributeArrayFixture.ByteChoice", "System.Type", "String" }));
            Assert.That(NamedCase("type").NamedArguments.Single().Type, Is.EqualTo("System.Type"));
            Assert.That(((ImmutableArray<CustomAttributeTypedArgument<string>>)NamedCase("integers").NamedArguments.Single().Value!).Select(argument => argument.Value), Is.EqualTo(new object[] { -1, 2 }));

            CustomAttributeValue<string> NamedCase(string value) => attributes.Single(attribute => Equals(attribute.FixedArguments[0].Value, value));
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    private sealed class FixtureAttributeTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        }
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => type.Split(',')[0] is "AttributeParameterFixture.ByteMode" or "AttributeArrayFixture.ByteChoice"
            ? PrimitiveTypeCode.Byte : throw new BadImageFormatException("Unexpected enum in the bounded fixture: " + type);
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
