using System;
using System.IO;
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
        Assert.That(Directory.GetFiles(Path.Combine(_directory, "Assets"), "*.dll", SearchOption.AllDirectories), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_directory, "Assets/Recovered/Synthetic.Application/Synthetic.Application.asmdef")), Is.True);
    }

    [Test]
    public void MissingTargetReferenceFailsInsteadOfUsingHostRuntime()
    {
        Assert.Throws<ICSharpCode.Decompiler.Metadata.ResolutionException>(() =>
            UnitySourceProjectEmitter.Emit([CreateAssembly("Synthetic.Application")], ["Synthetic.Application"], [], _directory));
        Assert.That(File.ReadAllText(Path.Combine(_directory, "source-emission-report.json")), Does.Contain("failed"));
    }

    [Test]
    public void ResolverRejectsReferenceIdentityMismatch()
    {
        using var resolver = new ExplicitAssemblyResolver([], [Path.GetDirectoryName(typeof(object).Assembly.Location)!]);
        var name = typeof(object).Assembly.GetName();
        var reference = AssemblyNameReference.Parse($"{name.Name}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(reference));
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
