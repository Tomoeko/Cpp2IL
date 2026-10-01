using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.SourceEmission;
using ICSharpCode.Decompiler.Metadata;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityTargetReferenceTransportTests
{
    private string _directory = null!;
    private string _compilerDirectory = null!;
    private string _runtimePath = null!;
    private byte[] _runtimeKey = null!;
    private byte[] _compilerKey = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cpp2il-reference-transport-" + Guid.NewGuid().ToString("N"));
        _compilerDirectory = Path.Combine(_directory, "compiler");
        Directory.CreateDirectory(_compilerDirectory);
        var hostDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        _runtimeKey = typeof(object).Assembly.GetName().GetPublicKey()!;
        _compilerKey = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(hostDirectory, "mscorlib.dll")).GetPublicKey()!;
        File.Copy(typeof(object).Assembly.Location, Path.Combine(_compilerDirectory, Path.GetFileName(typeof(object).Assembly.Location)));
        WriteTarget("compiler", _compilerKey);
        _runtimePath = WriteTarget("runtime", _runtimeKey);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void RuntimeAndCompilerRolesResolveDistinctExactFiles()
    {
        using var resolver = Resolver();
        var runtime = RuntimeReference();
        var original = resolver.Resolve(runtime) as PEFile;
        var compiler = resolver.ResolveCompilerReference(runtime);
        Assert.Multiple(() =>
        {
            Assert.That(original!.FileName, Is.EqualTo(_runtimePath));
            Assert.That(compiler.FileName, Is.EqualTo(Path.Combine(_compilerDirectory, "System.dll")));
            Assert.That(Unity2021TargetFrameworkAssemblies.HasTargetIdentity("System", runtime.Version, runtime.Culture,
                runtime.PublicKeyToken!), Is.False);
        });
    }

    [Test]
    public void MatchingSignatureTypesKeepRuntimeIlIdentityAndSelectCompilerAlias()
    {
        var application = Application();
        var report = UnitySourceProjectEmitter.Emit([application], ["Synthetic.Application"], [_compilerDirectory],
            Path.Combine(_directory, "project"), runtimeReferenceFiles: [_runtimePath]);
        var entry = report.Assemblies.Single();
        using var recovered = new PEFile(Path.Combine(_directory, "project", "RecoveredManaged", "Synthetic.Application.dll"));
        var reference = recovered.Metadata.AssemblyReferences.Select(handle => new ICSharpCode.Decompiler.Metadata.AssemblyReference(recovered, handle))
            .Single(reference => reference.Name == "System");
        var aliases = File.ReadAllText(Path.Combine(_directory, "project", "Assets", "Recovered", "Synthetic.Application", "ReferenceAliases.rsp"));
        Assert.Multiple(() =>
        {
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
            Assert.That(reference.FullName, Is.EqualTo(RuntimeReference().FullName));
            Assert.That(entry.ReferenceTransports.Single().ConsumedSignatureTypes, Is.EqualTo(new[] { "Synthetic.Transported" }));
            Assert.That(entry.ReferenceTransports.Single().OriginalIdentity, Is.EqualTo(reference.FullName));
            Assert.That(entry.ReferenceTransports.Single().CompilerIdentity, Does.Contain("b77a5c561934e089"));
            Assert.That(aliases, Does.Contain(Path.Combine(_compilerDirectory, "System.dll").Replace('\\', '/')));
            Assert.That(aliases, Does.Not.Contain(_runtimePath));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "project", "source-emission-report.json")),
                Does.Contain("\"LinkerTransportValidation\":\"unverified\""));
        });
    }

    [Test]
    public void SameModuleBaseTypeHasAnAuthenticatedDefiningRole()
    {
        WriteTarget("compiler", _compilerKey, localBase: true);
        _runtimePath = WriteTarget("runtime", _runtimeKey, localBase: true);
        Validate(Application());
    }

    [Test]
    public void NamespaceAndNameComponentsCannotBeFlattenedIntoACompilerMatch()
    {
        WriteTarget("compiler", _compilerKey, typeNamespace: "Synthetic.Outer", typeName: "Inner");
        _runtimePath = WriteTarget("runtime", _runtimeKey, typeNamespace: "Synthetic", typeName: "Outer.Inner");
        Assert.Throws<NotSupportedException>(() => Validate(Application(typeNamespace: "Synthetic", typeName: "Outer.Inner")));
    }

    [Test]
    public void LocalBaseNamespaceAndNameComponentsMustRemainDistinct()
    {
        WriteTarget("compiler", _compilerKey, "base-collision-compiler", localBase: true);
        _runtimePath = WriteTarget("runtime", _runtimeKey, "base-collision-runtime", localBase: true);
        Assert.Throws<NotSupportedException>(() => Validate(Application()));
    }

    [Test]
    public void PlainReferenceLocalAliasUsesTheSameQualifiedTypeAndReportsItsRole()
    {
        var application = Application("local");
        var report = UnitySourceProjectEmitter.Emit([application], ["Synthetic.Application"], [_compilerDirectory],
            Path.Combine(_directory, "local-project"), runtimeReferenceFiles: [_runtimePath]);
        var transport = report.Assemblies.Single().ReferenceTransports.Single();
        Assert.Multiple(() =>
        {
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
            Assert.That(transport.ConsumedSignatureTypes, Is.EqualTo(new[] { "Synthetic.Transported" }));
            Assert.That(transport.ConsumedLocalSignatureTypes, Is.EqualTo(new[] { "Synthetic.Transported" }));
            Assert.That(transport.OriginalIdentity, Is.EqualTo(RuntimeReference().FullName));
        });
    }

    [Test]
    public void MixedOriginalRuntimeAndCompilerIdentitiesCannotCollapseIntoOneSourceType()
    {
        var application = Application();
        var module = application.ManifestModule!;
        var identity = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(_compilerDirectory, "System.dll"));
        var compilerReference = new AsmResolver.DotNet.AssemblyReference(identity.Name, identity.Version!)
        { PublicKeyOrToken = identity.GetPublicKeyToken() };
        module.AssemblyReferences.Add(compilerReference);
        var type = module.TopLevelTypes.Single(type => type.Name == "Holder");
        type.Fields.Add(new FieldDefinition("CompilerValue", FieldAttributes.Public,
            new FieldSignature(new TypeReference(module, compilerReference, "Synthetic", "Transported").ToTypeSignature(false))));
        var project = Path.Combine(_directory, "mixed-roles");
        var exception = Assert.Throws<NotSupportedException>(() => UnitySourceProjectEmitter.Emit([application],
            ["Synthetic.Application"], [_compilerDirectory], project, runtimeReferenceFiles: [_runtimePath]));
        using var recovered = new PEFile(Path.Combine(project, "RecoveredManaged", "Synthetic.Application.dll"));
        var identities = recovered.Metadata.AssemblyReferences.Select(handle =>
            new ICSharpCode.Decompiler.Metadata.AssemblyReference(recovered, handle)).Where(reference => reference.Name == "System")
            .Select(reference => reference.FullName).Distinct().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("SOURCE015:"));
            Assert.That(identities, Has.Length.EqualTo(2));
            Assert.That(File.ReadAllText(Path.Combine(project, "source-emission-report.json")),
                Does.Contain("\"SourceGeneration\":\"failed\""));
            Assert.That(Directory.Exists(Path.Combine(project, "Assets", "Recovered")), Is.False);
        });
    }

    [TestCase("absent")]
    [TestCase("private")]
    [TestCase("interface")]
    [TestCase("sealed")]
    [TestCase("base")]
    [TestCase("generic")]
    public void ConsumedTypeRequiresAMatchingVisibleOrdinaryCompilerClass(string change)
    {
        WriteTarget("compiler", _compilerKey, change);
        Assert.Throws<NotSupportedException>(() => Validate(Application()));
    }

    [TestCase("member")]
    [TestCase("derive")]
    [TestCase("array")]
    [TestCase("byref")]
    [TestCase("modifier")]
    [TestCase("constraint")]
    [TestCase("type-token")]
    [TestCase("array-local")]
    [TestCase("generic-local")]
    [TestCase("byref-local")]
    [TestCase("modifier-local")]
    [TestCase("attribute")]
    [TestCase("generic-attribute")]
    [TestCase("array-attribute")]
    [TestCase("generic-method-call")]
    public void UnqualifiedConsumedApiUsesRemainSourceGaps(string use)
    {
        var exception = Assert.Throws<NotSupportedException>(() => Validate(Application(use)));
        Assert.That(exception!.Message, Does.StartWith("SOURCE015:"));
    }

    [Test]
    public void RuntimeDirectoryIsNotAnImplicitResolutionSearchPath()
    {
        WriteTarget("runtime", _runtimeKey, assemblyName: "System.Xml");
        using var resolver = Resolver();
        var identity = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(_directory, "runtime", "System.Xml.dll"));
        Assert.That(resolver.Resolve(AssemblyNameReference.Parse(identity.FullName!)), Is.Null);
    }

    [Test]
    public void CompilerRoleCannotBeSelectedByRuntimeIdentityOrMissingEvidence()
    {
        using var ordinary = new ExplicitAssemblyResolver([], [_compilerDirectory]);
        Assert.Throws<InvalidOperationException>(() => ordinary.Resolve(RuntimeReference()));
        Assert.Throws<InvalidOperationException>(() => new ExplicitAssemblyResolver([], [_compilerDirectory],
            [Path.Combine(_compilerDirectory, "System.dll")]));
        File.Delete(Path.Combine(_compilerDirectory, "System.dll"));
        Assert.Throws<InvalidOperationException>(() => Resolver());
    }

    [Test]
    public void DuplicateRoleFilesRemainAmbiguous()
    {
        var duplicate = WriteTarget("duplicate", _runtimeKey);
        Assert.Throws<InvalidOperationException>(() => new ExplicitAssemblyResolver([], [_compilerDirectory], [_runtimePath, duplicate]));
        WriteTarget("other-compiler", _compilerKey);
        Assert.Throws<InvalidOperationException>(() => new ExplicitAssemblyResolver([], [_compilerDirectory,
            Path.Combine(_directory, "other-compiler")], [_runtimePath]));
    }

    private ExplicitAssemblyResolver Resolver() => new([], [_compilerDirectory], [_runtimePath]);

    private AssemblyNameReference RuntimeReference() =>
        AssemblyNameReference.Parse(System.Reflection.AssemblyName.GetAssemblyName(_runtimePath).FullName!);

    private void Validate(AssemblyDefinition application)
    {
        var path = Path.Combine(_directory, "application.dll");
        using (var stream = File.Create(path)) application.WriteManifest(stream);
        using var file = new PEFile(path);
        using var resolver = Resolver();
        resolver.ValidateReferenceTransport(application.ManifestModule!, file);
    }

    private string WriteTarget(string directory, byte[] key, string? change = null, bool localBase = false,
        string assemblyName = "System", string typeNamespace = "Synthetic", string typeName = "Transported")
    {
        var target = CreateAssembly(assemblyName);
        target.Version = new Version(4, 0, 0, 0);
        target.PublicKey = key;
        target.HasPublicKey = true;
        var module = target.ManifestModule!;
        var baseType = module.CorLibTypeFactory.Object.Type;
        if (localBase)
        {
            var parent = new TypeDefinition(change == "base-collision-compiler" ? "Synthetic.Outer" : "Synthetic",
                change == "base-collision-compiler" ? "Inner" : change == "base-collision-runtime" ? "Outer.Inner" : "Parent",
                TypeAttributes.Public, baseType);
            module.TopLevelTypes.Add(parent);
            baseType = parent;
        }
        if (change == "base") baseType = module.CorLibTypeFactory.String.Type;
        if (change != "absent")
        {
            var attributes = change switch
            {
                "private" => TypeAttributes.NotPublic,
                "interface" => TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
                "sealed" => TypeAttributes.Public | TypeAttributes.Sealed,
                _ => TypeAttributes.Public,
            };
            var type = new TypeDefinition(typeNamespace, typeName, attributes, baseType);
            if (change == "generic") type.GenericParameters.Add(new GenericParameter("T"));
            module.TopLevelTypes.Add(type);
        }
        var path = Path.Combine(_directory, directory, assemblyName + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path)) target.WriteManifest(stream);
        return path;
    }

    private AssemblyDefinition Application(string? use = null, string typeNamespace = "Synthetic", string typeName = "Transported")
    {
        var application = CreateAssembly("Synthetic.Application");
        var module = application.ManifestModule!;
        var identity = System.Reflection.AssemblyName.GetAssemblyName(_runtimePath);
        var reference = new AsmResolver.DotNet.AssemblyReference(identity.Name, identity.Version!)
        { PublicKeyOrToken = identity.GetPublicKeyToken() };
        module.AssemblyReferences.Add(reference);
        var transported = new TypeReference(module, reference, typeNamespace, typeName);
        var signature = transported.ToTypeSignature(false);
        var type = new TypeDefinition("Synthetic", "Holder", TypeAttributes.Public,
            use == "derive" ? transported : module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var fieldType = use switch
        {
            "array" => signature.MakeSzArrayType(),
            "byref" => signature.MakeByReferenceType(),
            "modifier" => signature.MakeModifierType(module.CorLibTypeFactory.Object.Type, true),
            _ => signature,
        };
        type.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public, new FieldSignature(fieldType)));
        var method = new MethodDefinition("Identity", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(signature, [signature]));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        if (use == "member")
        {
            var member = transported.CreateMemberReference("Inspect", MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
            method.CilMethodBody.Instructions.Insert(0, new CilInstruction(CilOpCodes.Call, member));
        }
        if (use == "type-token")
        {
            method.CilMethodBody.Instructions.Insert(0, new CilInstruction(CilOpCodes.Ldtoken, transported));
            method.CilMethodBody.Instructions.Insert(1, new CilInstruction(CilOpCodes.Pop));
        }
        if (use is "local" or "array-local" or "generic-local" or "byref-local" or "modifier-local")
        {
            var localType = use switch
            {
                "array-local" => signature.MakeSzArrayType(),
                "generic-local" => new GenericInstanceTypeSignature(
                    new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System.Collections.Generic", "List`1"), false, [signature]),
                "byref-local" => signature.MakeByReferenceType(),
                "modifier-local" => signature.MakeModifierType(module.CorLibTypeFactory.Object.Type, true),
                _ => signature,
            };
            method.CilMethodBody.LocalVariables.Add(new CilLocalVariable(localType));
            if (use == "local")
            {
                method.CilMethodBody.Instructions.Clear();
                method.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
                method.CilMethodBody.Instructions.Add(CilOpCodes.Stloc_0);
                method.CilMethodBody.Instructions.Add(CilOpCodes.Ldloc_0);
                method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            }
        }
        if (use == "constraint")
        {
            method.Signature = MethodSignature.CreateStatic(signature, 1, [signature]);
            var parameter = new GenericParameter("T");
            parameter.Constraints.Add(new GenericParameterConstraint(transported));
            method.GenericParameters.Add(parameter);
        }
        if (use == "generic-method-call")
        {
            var generic = new MethodDefinition("Consume", MethodAttributes.Public | MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, 1, []));
            generic.GenericParameters.Add(new GenericParameter("T"));
            generic.CilMethodBody = new CilMethodBody();
            generic.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
            type.Methods.Add(generic);
            var specification = new MethodSpecification(generic, new GenericInstanceMethodSignature([signature]));
            method.CilMethodBody.Instructions.Insert(0, new CilInstruction(CilOpCodes.Call, specification));
        }
        if (use is "attribute" or "generic-attribute" or "array-attribute")
        {
            var systemType = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "Type");
            var attribute = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "Synthetic", "MarkerAttribute")
                .CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [systemType.ToTypeSignature(false)]));
            type.CustomAttributes.Add(new CustomAttribute(attribute)
            {
                Signature = new CustomAttributeSignature(new CustomAttributeArgument(systemType.ToTypeSignature(false), use switch
                {
                    "generic-attribute" => new GenericInstanceTypeSignature(
                        new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System.Collections.Generic", "List`1"), false, [signature]),
                    "array-attribute" => signature.MakeSzArrayType(),
                    _ => signature,
                })),
            });
        }
        return application;
    }

    private static AssemblyDefinition CreateAssembly(string name)
    {
        // These are synthetic metadata controls, not exact-target compilation evidence.
        var core = typeof(object).Assembly.GetName();
        var reference = new AsmResolver.DotNet.AssemblyReference(core.Name, core.Version!)
        { PublicKeyOrToken = core.GetPublicKeyToken() };
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(new ModuleDefinition(name + ".dll", reference));
        return assembly;
    }
}
