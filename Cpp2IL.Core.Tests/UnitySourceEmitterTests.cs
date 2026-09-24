using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.SourceEmission;
using ICSharpCode.Decompiler.Metadata;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnitySourceEmitterTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cpp2il-source-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void EmitsMethodBodyAndBlockNamespaceWithoutClaimingValidation()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"], [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory);

        var source = File.ReadAllText(Path.Combine(_directory, report.Assemblies[0].SourceFile));
        Assert.That(source, Does.Contain("return 17;"));
        Assert.That(source, Does.Contain("namespace Synthetic"));
        Assert.That(source, Does.Not.Contain("namespace Synthetic;"));
        Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
        Assert.That(report.UnityCompilation, Is.EqualTo("unverified"));
        Assert.That(report.BehavioralValidation, Is.EqualTo("unverified"));
        Assert.That(report.Assemblies[0].SourceFiles, Is.EqualTo(new[] { report.Assemblies[0].SourceFile }));
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "Assets"), "*.dll", SearchOption.AllDirectories), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_directory, "Assets/Recovered/Synthetic.Application/Synthetic.Application.asmdef")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(_directory, "Packages/manifest.json")), Is.EqualTo("{\"dependencies\":{}}\n"));
        Assert.That(report.PackageManifestProvenance, Is.EqualTo("default-empty"));
        Assert.That(report.PackageDependencyCount, Is.Zero);
    }

    [Test]
    public void ExplicitUnityManifestIsCopiedExactlyAndReportedWithoutInputDetails()
    {
        const string manifest = "{\n  \"scopedRegistries\": [{\"name\": \"Example registry\", \"url\": \"https://packages.example.org\", \"scopes\": [\"com.example\"]}],\n" +
                                "  \"dependencies\": {\"com.example.feature\": \"2.4.1-preview.3+001\", \"com.unity.test-framework\": \"1.1.33\", \"com.example.remote_package\": \"https://example.org/package.git?path=/Runtime/Package#v1.2.3\"},\n" +
                                "  \"testables\": [\"com.example.feature\"], \"enableLockFile\": true, \"resolutionStrategy\": \"highest\"\n}\n";
        var input = Path.Combine(_directory, "manifest-input.json");
        var output = Path.Combine(_directory, "project");
        File.WriteAllText(input, manifest, new UTF8Encoding(false));

        var report = UnitySourceProjectEmitter.Emit([CreateAssembly("Synthetic.Application")], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], output, input);

        Assert.That(File.ReadAllText(Path.Combine(output, "Packages/manifest.json")), Is.EqualTo(manifest));
        Assert.That(report.PackageManifestProvenance, Is.EqualTo("explicit-auxiliary"));
        Assert.That(report.PackageDependencyCount, Is.EqualTo(3));
        var sourceReport = File.ReadAllText(Path.Combine(output, "source-emission-report.json"));
        Assert.That(sourceReport, Does.Contain("\"PackageManifestProvenance\":\"explicit-auxiliary\""));
        Assert.That(sourceReport, Does.Not.Contain(input).And.Not.Contain("com.example.feature").And.Not.Contain("Sha256"));
    }

    [Test]
    public void ExplicitExternalKindsUseDistinctUnityAssemblyDefinitionFields()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        assembly.ManifestModule!.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Unity.Example.Package", new Version(1, 0, 0, 0)));
        assembly.ManifestModule.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Synthetic.Plugin", new Version(1, 0, 0, 0)));
        var referenceDirectory = WriteReference("Unity.Example.Package", new Version(1, 0, 0, 0), "references");
        WriteReference("Synthetic.Plugin", new Version(1, 0, 0, 0), "references");
        var map = WriteExternalReferenceMap("""
            {"references":[
              {"assembly":"Unity.Example.Package","kind":"asmdef"},
              {"assembly":"Synthetic.Plugin","kind":"precompiled-plugin"}
            ]}
            """);
        var output = Path.Combine(_directory, "project");

        var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [referenceDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)!], output,
            externalReferenceMapPath: map);

        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(output,
            "Assets/Recovered/Synthetic.Application/Synthetic.Application.asmdef")));
        Assert.That(definition.RootElement.GetProperty("references").EnumerateArray().Select(item => item.GetString()),
            Is.EqualTo(new[] { "Unity.Example.Package" }));
        Assert.That(definition.RootElement.GetProperty("precompiledReferences").EnumerateArray().Select(item => item.GetString()),
            Is.EqualTo(new[] { "Synthetic.Plugin.dll" }));
        Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
        Assert.That(report.ExternalReferenceMapProvenance, Is.EqualTo("explicit-auxiliary"));
        Assert.That(report.Assemblies.Single().ExternalReferenceKinds.Select(item => (item.Name, item.Kind)),
            Is.EquivalentTo(new[] { ("Unity.Example.Package", "asmdef"), ("Synthetic.Plugin", "precompiled-plugin"),
                (typeof(object).Assembly.GetName().Name!, "target-provided") }));
        Assert.That(File.ReadAllText(Path.Combine(output, "source-emission-report.json")), Does.Not.Contain(map));
    }

    [Test]
    public void UnclassifiedUnityPrefixedReferenceCannotClaimCompleteSource()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        assembly.ManifestModule!.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Unity.Example.Package", new Version(1, 0, 0, 0)));
        var referenceDirectory = WriteReference("Unity.Example.Package", new Version(1, 0, 0, 0), "references");

        var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [referenceDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)!], Path.Combine(_directory, "project"));

        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("SOURCE007").And.Contains("Unity.Example.Package"));
        Assert.That(report.Assemblies.Single().ExternalReferenceKinds.Single(item => item.Name == "Unity.Example.Package").Kind,
            Is.EqualTo("unclassified"));
        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory,
            "project/Assets/Recovered/Synthetic.Application/Synthetic.Application.asmdef")));
        Assert.That(definition.RootElement.GetProperty("references").GetArrayLength(), Is.Zero);
        Assert.That(definition.RootElement.GetProperty("precompiledReferences").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void BuiltInNumericsReferenceRequiresTheExactTargetIdentity()
    {
        var token = Convert.FromHexString("B77A5C561934E089");
        var exact = new AsmResolver.DotNet.AssemblyReference("System.Numerics", new Version(4, 0, 0, 0))
        {
            PublicKeyOrToken = token,
        };
        var wrongVersion = new AsmResolver.DotNet.AssemblyReference("System.Numerics", new Version(5, 0, 0, 0))
        {
            PublicKeyOrToken = token,
        };
        var wrongToken = new AsmResolver.DotNet.AssemblyReference("System.Numerics", new Version(4, 0, 0, 0))
        {
            PublicKeyOrToken = [0, 0, 0, 0, 0, 0, 0, 0],
        };

        Assert.Multiple(() =>
        {
            Assert.That(UnitySourceProjectEmitter.IsTargetProvidedAssembly(exact), Is.True);
            Assert.That(UnitySourceProjectEmitter.IsTargetProvidedAssembly(wrongVersion), Is.False);
            Assert.That(UnitySourceProjectEmitter.IsTargetProvidedAssembly(wrongToken), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PredefinedAssemblyNeedsExplicitAutoReferenceEvidenceForExternalDependency(bool autoReferenced)
    {
        var assembly = CreateAssembly("Assembly-CSharp");
        assembly.ManifestModule!.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Synthetic.Package", new Version(1, 0, 0, 0)));
        var referenceDirectory = WriteReference("Synthetic.Package", new Version(1, 0, 0, 0), "references");
        var map = WriteExternalReferenceMap("{\"references\":[{\"assembly\":\"Synthetic.Package\",\"kind\":\"asmdef\",\"autoReferenced\":" +
                                            (autoReferenced ? "true" : "false") + "}]}");

        var report = UnitySourceProjectEmitter.Emit([assembly], ["Assembly-CSharp"],
            [referenceDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)!], Path.Combine(_directory, "project"),
            externalReferenceMapPath: map);

        Assert.That(report.SourceGeneration, Is.EqualTo(autoReferenced ? "generated" : "partial"));
        Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Contains("SOURCE007")), Is.EqualTo(!autoReferenced));
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "project/Assets"), "*.asmdef", SearchOption.AllDirectories), Is.Empty);
    }

    [TestCase("{\"references\":[{\"assembly\":\"Synthetic.Plugin\",\"kind\":\"plugin\"}]}")]
    [TestCase("{\"references\":[{\"assembly\":\"../Outside\",\"kind\":\"asmdef\"}]}")]
    [TestCase("{\"references\":[{\"assembly\":\"Synthetic.Plugin\",\"kind\":\"asmdef\"},{\"assembly\":\"Synthetic.Plugin\",\"kind\":\"precompiled-plugin\"}]}")]
    [TestCase("{\"references\":[{\"assembly\":\"Synthetic.Plugin\",\"kind\":\"precompiled-plugin\",\"fileName\":\"Synthetic.Binary.dll\"}]}")]
    public void InvalidExternalReferenceMapIsRejectedBeforeProjectCreation(string content)
    {
        var map = WriteExternalReferenceMap(content);
        var output = Path.Combine(_directory, "project");

        var error = Assert.Throws<ArgumentException>(() => UnitySourceProjectEmitter.Emit(
            [CreateAssembly("Synthetic.Application")], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], output, externalReferenceMapPath: map));

        Assert.That(error!.Message, Does.Not.Contain(map).And.Not.Contain("Outside"));
        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void OneUnityReferenceNameCannotRepresentDistinctExternalManagedIdentities()
    {
        var first = CreateAssembly("Synthetic.First");
        var second = CreateAssembly("Synthetic.Second");
        first.ManifestModule!.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Synthetic.Plugin", new Version(1, 0, 0, 0)));
        second.ManifestModule!.AssemblyReferences.Add(new AsmResolver.DotNet.AssemblyReference("Synthetic.Plugin", new Version(2, 0, 0, 0)));
        var firstReferences = WriteReference("Synthetic.Plugin", new Version(1, 0, 0, 0), "first-references");
        var secondReferences = WriteReference("Synthetic.Plugin", new Version(2, 0, 0, 0), "second-references");
        var map = WriteExternalReferenceMap("{\"references\":[{\"assembly\":\"Synthetic.Plugin\",\"kind\":\"precompiled-plugin\"}]}");

        var report = UnitySourceProjectEmitter.Emit([first, second], ["Synthetic.First", "Synthetic.Second"],
            [firstReferences, secondReferences, Path.GetDirectoryName(typeof(object).Assembly.Location)!],
            Path.Combine(_directory, "project"), externalReferenceMapPath: map);

        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("SOURCE007").And.Contains("Distinct managed identities"));
    }

    private static IEnumerable<string> InvalidPackageManifests()
    {
        yield return "[]";
        yield return "{}";
        yield return "{\"dependencies\":[]}";
        yield return "{\"dependencies\":{},\"useSatSolver\":true}";
        yield return "{\"dependencies\":{},\"dependencies\":{}}";
        yield return "{\"dependencies\":{\"com.example.feature\":null}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"1.0.0-01\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"1.0.0-preview.01\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"file:/example/absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"C:\\\\example\\\\absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"../absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://user:token@example.org/package.git\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"http://example.org/package.git\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"git+file:///example/absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://localhost/package.git\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://packages.localhost/package.git\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://127.0.0.1/package.git\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package.git?path=/../absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package.git?path=/Runtime/%2e%2e\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package.git?path=//absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package.git?path=/Runtime&token=absolute-package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package?path=/Runtime\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package\"}}";
        yield return "{\"dependencies\":{\"com.example.feature\":\"https://example.org/package.git#\"}}";
        yield return "{\"dependencies\":{\"example\":\"1.0.0\"}}";
        yield return "{\"dependencies\":{},\"unexpected\":\"/example/absolute-package\"}";
        yield return "{\"dependencies\":{},\"scopedRegistries\":[{\"name\":\"A\",\"url\":\"file:/example/absolute-package\",\"scopes\":[\"com.example\"]}]}";
    }

    [TestCaseSource(nameof(InvalidPackageManifests))]
    public void UnsafeOrMalformedManifestIsRejectedBeforeProjectCreation(string manifest)
    {
        var input = Path.Combine(_directory, "manifest-input.json");
        var output = Path.Combine(_directory, "project");
        File.WriteAllText(input, manifest, new UTF8Encoding(false));

        var error = Assert.Throws<ArgumentException>(() => UnitySourceProjectEmitter.Emit(
            [CreateAssembly("Synthetic.Application")], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], output, input));
        Assert.That(error!.Message, Does.Not.Contain("absolute-package").And.Not.Contain(input));
        Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void MissingTargetReferenceFailsInsteadOfUsingHostRuntime()
    {
        Assert.Throws<ICSharpCode.Decompiler.Metadata.ResolutionException>(() =>
            UnitySourceProjectEmitter.Emit([CreateAssembly("Synthetic.Application")], ["Synthetic.Application"], [], _directory));
        Assert.That(File.ReadAllText(Path.Combine(_directory, "source-emission-report.json")), Does.Contain("failed"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnresolvedMarshalingMetadataMakesSourcePartial(bool parameter)
    {
        var assembly = CreateAssembly("Synthetic.Application");
        var module = assembly.ManifestModule!;
        var type = module.TopLevelTypes.Single(t => t.Name == "Constants");
        if (parameter)
        {
            var method = type.Methods[0];
            method.Signature!.ParameterTypes.Add(module.CorLibTypeFactory.Boolean);
            method.ParameterDefinitions.Add(new ParameterDefinition(1, "value", ParameterAttributes.HasFieldMarshal));
        }
        else
            type.Fields.Add(new FieldDefinition("Enabled", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldMarshal,
                module.CorLibTypeFactory.Boolean));

        var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory);
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("SOURCE004"));
        Assert.That(report.DeclarationFidelity, Is.EqualTo("unverified"));
    }

    [Test]
    public void ByReferenceReturnWithoutModifierCannotClaimCompleteSource()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        var module = assembly.ManifestModule!;
        var type = module.TopLevelTypes.Single(t => t.Name == "Constants");
        var field = new FieldDefinition("Storage", FieldAttributes.Private | FieldAttributes.Static, module.CorLibTypeFactory.Int32);
        type.Fields.Add(field);
        var method = new MethodDefinition("GetStorage", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32.MakeByReferenceType()));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldsflda, field);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var inAttribute = new TypeReference(module, module.AssemblyReferences.Single(), "System.Runtime.InteropServices", "InAttribute");
        var readonlyMethod = new MethodDefinition("GetStorageReadonly", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32.MakeByReferenceType().MakeModifierType(inAttribute, true)));
        type.Methods.Add(readonlyMethod);
        readonlyMethod.CilMethodBody = new CilMethodBody();
        readonlyMethod.CilMethodBody.Instructions.Add(CilOpCodes.Ldsflda, field);
        readonlyMethod.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

        var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory, playerMetadataVersion: 29f);

        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics.Count(diagnostic => diagnostic.StartsWith("SOURCE008:", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(report.Diagnostics, Has.Some.Contains("GetStorage").And.Contains("ref readonly"));
        Assert.That(report.Diagnostics, Has.None.Contains("GetStorageReadonly"));
        Assert.That(File.ReadAllText(Path.Combine(_directory, "source-emission-report.json")), Does.Contain("SOURCE008"));

        var authoredReport = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], Path.Combine(_directory, "authored-input"));
        Assert.That(authoredReport.SourceGeneration, Is.EqualTo("generated"));
        Assert.That(authoredReport.Diagnostics, Has.None.Contains("SOURCE008"));
    }

    [Test]
    public void ResolverRejectsReferenceIdentityMismatch()
    {
        using var resolver = new ExplicitAssemblyResolver([], [Path.GetDirectoryName(typeof(object).Assembly.Location)!]);
        var name = typeof(object).Assembly.GetName();
        var reference = AssemblyNameReference.Parse($"{name.Name}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(reference));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExplicitReferenceVersionsResolveByIdentityRegardlessOfSearchOrder(bool reverse)
    {
        var first = WriteReference(new Version(1, 0, 0, 0), "first");
        var second = WriteReference(new Version(2, 0, 0, 0), "second");
        using var resolver = new ExplicitAssemblyResolver([], reverse ? [second, first] : [first, second]);
        foreach (var version in new[] { new Version(1, 0, 0, 0), new Version(2, 0, 0, 0) })
        {
            var resolved = resolver.Resolve(AssemblyNameReference.Parse($"Synthetic.Reference, Version={version}, Culture=neutral, PublicKeyToken=null"));
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Metadata.GetAssemblyDefinition().Version, Is.EqualTo(version));
        }
    }

    [Test]
    public void DuplicateExactReferenceIdentitiesRemainAmbiguous()
    {
        var first = WriteReference(new Version(1, 0, 0, 0), "first");
        var second = WriteReference(new Version(1, 0, 0, 0), "second");
        using var resolver = new ExplicitAssemblyResolver([], [first, second]);
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(
            AssemblyNameReference.Parse("Synthetic.Reference, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")));
    }

    [Test]
    public void ComponentFilesPreserveHelpersAttributesAndManagedIdentity()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        var module = assembly.ManifestModule!;
        var marker = AddComponent(module, "Synthetic", "Marker", "MonoBehaviour");
        marker.NestedTypes.Add(new TypeDefinition("", "NestedHelper", TypeAttributes.NestedPublic, module.CorLibTypeFactory.Object.Type));
        AddComponent(module, "Synthetic", "Data", "ScriptableObject");
        var attribute = new AsmResolver.DotNet.TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "CLSCompliantAttribute")
            .CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Boolean]));
        assembly.CustomAttributes.Add(new CustomAttribute(attribute)
        {
            Signature = new CustomAttributeSignature(new CustomAttributeArgument(module.CorLibTypeFactory.Boolean, true))
        });
        var report = EmitComponents(assembly);
        var entry = report.Assemblies.Single();
        Assert.That(entry.SourceFiles.Count, Is.EqualTo(3));
        Assert.That(entry.ComponentScripts.Select(c => c.TypeName), Is.EquivalentTo(new[] { "Synthetic.Marker", "Synthetic.Data" }));
        var central = ReadSource(entry.SourceFile);
        Assert.That(central, Does.Contain("class Constants"));
        Assert.That(central, Does.Not.Contain("class Marker"));
        Assert.That(central, Does.Contain("CLSCompliant(true)"));
        var component = ReadSource(entry.ComponentScripts.Single(c => c.TypeName == "Synthetic.Marker").SourceFile);
        Assert.That(component, Does.Contain("class Marker"));
        Assert.That(component, Does.Contain("class NestedHelper"));
        Assert.That(component, Does.Not.Contain("CLSCompliant(true)"));
        Assert.That(component, Does.Not.Contain("class Constants"));
        Assert.That(entry.ComponentScripts.All(c => Path.GetFileName(c.SourceFile) == c.TypeName.Split('.').Last() + ".cs"), Is.True);
        Assert.That(report.ScriptAssetBindings, Is.EqualTo("unverified"));
        Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
    }

    [Test]
    public void IndirectComponentInheritanceUsesResolvedUnityIdentity()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        var module = assembly.ManifestModule!;
        var parent = AddComponent(module, "Synthetic", "Parent", "MonoBehaviour");
        module.TopLevelTypes.Add(new TypeDefinition("Synthetic", "Derived", TypeAttributes.Public, parent));
        var report = EmitComponents(assembly);
        Assert.That(report.Assemblies.Single().ComponentScripts.Select(c => c.TypeName),
            Is.EquivalentTo(new[] { "Synthetic.Parent", "Synthetic.Derived" }));
    }

    [Test]
    public void ComponentPathsThatDifferOnlyByCaseAreReportedWithoutOverwriting()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, "First", "Marker", "MonoBehaviour");
        AddComponent(assembly.ManifestModule!, "first", "Marker", "MonoBehaviour");
        var report = EmitComponents(assembly);
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics.Count(d => d.Contains("SOURCE006")), Is.EqualTo(2));
        Assert.That(report.Assemblies.Single().SourceFiles.Count, Is.EqualTo(1));
        Assert.That(ReadSource(report.Assemblies.Single().SourceFile), Does.Contain("namespace First").And.Contain("namespace first"));
    }

    [TestCase("CON")]
    [TestCase("Aux")]
    public void ReservedWindowsComponentNamesRemainExplicitlyUnbound(string name)
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, "Synthetic", name, "MonoBehaviour");
        var report = EmitComponents(assembly);
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("SOURCE006"));
        Assert.That(report.Assemblies.Single().ComponentScripts, Is.Empty);
    }

    [Test]
    public void NamespaceFoldersCannotActivateUnityEditorOrResourceRules()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, "Editor.Resources.Plugins.cvs", "Marker", "MonoBehaviour");
        var report = EmitComponents(assembly);
        var component = report.Assemblies.Single().ComponentScripts.Single();
        Assert.That(component.SourceFile, Does.Contain("Components/ns-Editor/ns-Resources/ns-Plugins/ns-cvs/Marker.cs"));
        Assert.That(ReadSource(component.SourceFile), Does.Contain("namespace Editor.Resources.Plugins.cvs"));
    }

    [Test]
    public void UnityIgnoredNamespaceSuffixRemainsExplicitlyUnbound()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, "Synthetic.Hidden~", "Marker", "MonoBehaviour");
        var report = EmitComponents(assembly);
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("Unity ignores a namespace directory ending in '~'"));
        Assert.That(report.Assemblies.Single().ComponentScripts, Is.Empty);
        Assert.That(report.Assemblies.Single().SourceFiles.Count, Is.EqualTo(1));
    }

    [TestCase("Synthetic.Bad-Name", "Marker")]
    [TestCase("Synthetic.1Name", "Marker")]
    [TestCase("Synthetic.Bad\u200cName", "Marker")]
    [TestCase("Synthetic", "Bad-Name")]
    [TestCase("Synthetic", "Bad Name")]
    public void UnrepresentableDeclarationCannotClaimComponentIdentity(string ns, string name)
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, ns, name, "MonoBehaviour");
        var report = EmitComponents(assembly);
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.That(report.Diagnostics, Has.Some.Contains("identifier cannot preserve its metadata name in C#"));
        Assert.That(report.Assemblies.Single().ComponentScripts, Is.Empty);
    }

    [Test]
    public void KeywordAndUnicodeLetterIdentifiersKeepTheirManagedNames()
    {
        var assembly = CreateAssembly("Synthetic.Application");
        AddComponent(assembly.ManifestModule!, "namespace.\u03b1", "class", "MonoBehaviour");
        var report = EmitComponents(assembly);
        Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
        var component = report.Assemblies.Single().ComponentScripts.Single();
        Assert.That(component.TypeName, Is.EqualTo("namespace.\u03b1.class"));
        Assert.That(component.SourceFile, Does.EndWith("/class.cs"));
        Assert.That(ReadSource(component.SourceFile), Does.Contain("namespace @namespace.\u03b1").And.Contain("class @class"));
    }

    [TestCase("Editor")]
    [TestCase("Resources")]
    [TestCase("Plugins")]
    [TestCase("cvs")]
    [TestCase(".Hidden")]
    [TestCase("Trailing~")]
    public void AssemblyIdentityDoesNotSelectUnitySpecialOrHiddenFolders(string name)
    {
        var report = UnitySourceProjectEmitter.Emit([CreateAssembly(name)], [name],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory);
        var entry = report.Assemblies.Single();
        Assert.That(entry.Name, Is.EqualTo(name));
        Assert.That(entry.SourceFile, Is.EqualTo("Assets/Recovered/assembly-" + name + "-source/Recovered.cs"));
        var definition = Directory.GetFiles(_directory, "*.asmdef", SearchOption.AllDirectories).Single();
        Assert.That(Path.GetFileName(definition).StartsWith(".", StringComparison.Ordinal), Is.False);
        Assert.That(File.ReadAllText(definition), Does.Contain("\"name\":\"" + name + "\""));
    }

    [Test]
    public void EscapedAssemblyDirectoryCollisionsAreRejectedBeforeWriting()
    {
        Assert.Throws<ArgumentException>(() => UnitySourceProjectEmitter.Emit(
            [CreateAssembly("Editor"), CreateAssembly("assembly-Editor-source")], ["Editor", "assembly-Editor-source"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory));
        Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
    }

    private string ReadSource(string relative) => File.ReadAllText(Path.Combine(_directory, "project", relative));

    private UnitySourceEmissionReport EmitComponents(AssemblyDefinition assembly)
    {
        var references = Path.Combine(_directory, "references");
        Directory.CreateDirectory(references);
        var engine = CreateAssembly("UnityEngine.CoreModule");
        foreach (var name in new[] { "MonoBehaviour", "ScriptableObject" })
            engine.ManifestModule!.TopLevelTypes.Add(new TypeDefinition("UnityEngine", name, TypeAttributes.Public,
                engine.ManifestModule.CorLibTypeFactory.Object.Type));
        using (var stream = File.Create(Path.Combine(references, "UnityEngine.CoreModule.dll")))
            engine.WriteManifest(stream);
        return UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.Application"],
            [references, Path.GetDirectoryName(typeof(object).Assembly.Location)!], Path.Combine(_directory, "project"));
    }

    private static TypeDefinition AddComponent(ModuleDefinition module, string ns, string name, string baseName)
    {
        var engine = module.AssemblyReferences.FirstOrDefault(r => r.Name == "UnityEngine.CoreModule");
        if (engine == null)
        {
            engine = new AsmResolver.DotNet.AssemblyReference("UnityEngine.CoreModule", new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(engine);
        }
        var type = new TypeDefinition(ns, name, TypeAttributes.Public,
            new AsmResolver.DotNet.TypeReference(module, engine, "UnityEngine", baseName));
        module.TopLevelTypes.Add(type);
        return type;
    }

    private string WriteReference(Version version, string directoryName)
        => WriteReference("Synthetic.Reference", version, directoryName);

    private string WriteReference(string name, Version version, string directoryName)
    {
        var directory = Path.Combine(_directory, directoryName);
        Directory.CreateDirectory(directory);
        var reference = CreateAssembly(name);
        reference.Version = version;
        using var stream = File.Create(Path.Combine(directory, name + ".dll"));
        reference.WriteManifest(stream);
        return directory;
    }

    private string WriteExternalReferenceMap(string content)
    {
        var path = Path.Combine(_directory, "external-reference-map.json");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Test]
    public void PredefinedAssemblyKeepsItsUnityAssemblyBoundary()
    {
        UnitySourceProjectEmitter.Emit([CreateAssembly("Assembly-CSharp")], ["Assembly-CSharp"], [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory);
        Assert.That(File.Exists(Path.Combine(_directory, "Assets/Recovered/Assembly-CSharp/Recovered.cs")), Is.True);
        Assert.That(Directory.GetFiles(_directory, "*.asmdef", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void ExistingProjectCannotHideAnIncompleteGeneration()
    {
        File.WriteAllText(Path.Combine(_directory, "stale.cs"), "stale");
        Assert.Throws<IOException>(() => UnitySourceProjectEmitter.Emit([CreateAssembly("Synthetic.Application")], ["Synthetic.Application"], [], _directory));
        Assert.That(File.ReadAllText(Path.Combine(_directory, "stale.cs")), Is.EqualTo("stale"));
    }

    [TestCase("UnityEngine.CoreModule")]
    [TestCase("mscorlib")]
    [TestCase("../Outside")]
    [TestCase("CON")]
    [TestCase("Trailing ")]
    public void RejectsReferenceAssembliesAndUnsafeOutputNames(string name)
    {
        Assert.Throws<ArgumentException>(() => UnitySourceProjectEmitter.Emit([CreateAssembly(name)], [name], [], _directory));
        Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
    }

    private static AssemblyDefinition CreateAssembly(string name)
    {
        // Synthetic unit-test input explicitly targets this test runtime, not Unity. Exact-editor
        // compilation is a separate licensed integration gate.
        var coreName = typeof(object).Assembly.GetName();
        var coreReference = new AsmResolver.DotNet.AssemblyReference(coreName.Name, coreName.Version!)
        {
            PublicKeyOrToken = coreName.GetPublicKeyToken(),
        };
        var module = new ModuleDefinition(name + ".dll", coreReference);
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Synthetic", "Constants", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, 17);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        return assembly;
    }
}
