using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.SourceEmission;

namespace Cpp2IL.Core.Tests;

public class UnityTargetAssemblyScopeTests
{
    [TestCase("application-name")]
    [TestCase("reference-name")]
    [TestCase("version")]
    [TestCase("culture")]
    [TestCase("full-key")]
    [TestCase("token")]
    public void OverridesCannotQualifyOrRegenerateOriginalTargetNames(string defect)
    {
        var original = new UnityTargetAssemblyScope.Identity("UnityEngine.CoreModule", new Version(0, 0, 0, 0), null, null, null);
        var current = original;
        switch (defect)
        {
            case "application-name": original = original with { Name = "Unity.Neutral.Package" }; break;
            case "reference-name": current = current with { Name = "Unity.Neutral.Package" }; break;
            case "version": original = original with { Version = new Version(1, 0, 0, 0) }; break;
            case "culture": original = original with { Culture = "en" }; break;
            case "full-key": original = original with { PublicKey = new byte[16] }; break;
            case "token": original = original with { PublicKeyToken = new byte[8] }; break;
        }
        Assert.That(UnityTargetAssemblyScope.ClassifyOriginalIdentity(original, current),
            Is.EqualTo(UnityTargetAssemblyKind.UnresolvedTargetReference));
    }

    [Test]
    public void EqualOriginalIdentityContainersKeepTheSameClassification()
    {
        byte[] key = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];
        Assert.That(UnityTargetAssemblyScope.TryNormalizePublicKey(key, true, out var token), Is.True);
        var original = new UnityTargetAssemblyScope.Identity("mscorlib", new Version(4, 0, 0, 0), null, key, token);
        var current = original with { Culture = "", PublicKey = key.ToArray(), PublicKeyToken = token.ToArray() };
        Assert.That(UnityTargetAssemblyScope.ClassifyOriginalIdentity(original, current),
            Is.EqualTo(UnityTargetAssemblyKind.TargetReference));
        Assert.That(UnityTargetAssemblyScope.ClassifyOriginalIdentity(
            original with { Name = "Unity.Neutral.Package" }, current with { Name = "Unity.Other.Package" }),
            Is.EqualTo(UnityTargetAssemblyKind.Application));
    }

    [TestCase("Unity.Neutral.Package")]
    [TestCase("UnityEngine.Neutral.Package")]
    [TestCase("System.Neutral.Package")]
    public void UnknownPrefixedAssembliesKeepTheirApplicationSource(string name)
    {
        Assert.That(UnityTargetAssemblyScope.Classify(name, new Version(1, 0, 0, 0), null, null),
            Is.EqualTo(UnityTargetAssemblyKind.Application));
        var directory = Path.Combine(Path.GetTempPath(), "cpp2il-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = UnitySourceProjectEmitter.Emit([CreateAssembly(name)], [name],
                [Path.GetDirectoryName(typeof(object).Assembly.Location)!], directory);
            Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
            Assert.That(report.Assemblies.Single().Name, Is.EqualTo(name));
            Assert.That(File.ReadAllText(Path.Combine(directory, report.Assemblies.Single().SourceFile)),
                Does.Contain("return 17;"));
            Assert.That(File.Exists(Path.Combine(directory, "Assets/Recovered", name, name + ".asmdef")), Is.True);
            Assert.That(report.PackageDependencyCount, Is.Zero);
            Assert.That(report.UnityCompilation, Is.EqualTo("unverified"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase("UnityEngine.CoreModule")]
    [TestCase("Mono.Security")]
    public void KnownTargetNamesCannotBeReplacedByApplicationSource(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cpp2il-scope-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => UnitySourceProjectEmitter.Emit([CreateAssembly(name)], [name], [], directory));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [TestCase("version")]
    [TestCase("culture")]
    [TestCase("token")]
    [TestCase("short-token")]
    [TestCase("zero-token")]
    [TestCase("missing-full-key")]
    public void ATargetNameDoesNotHideAnUnqualifiedIdentity(string defect)
    {
        var version = new Version(0, 0, 0, 0);
        string? culture = null;
        byte[]? key = null;
        var full = false;
        switch (defect)
        {
            case "version": version = new Version(1, 0, 0, 0); break;
            case "culture": culture = "en"; break;
            case "token": key = Enumerable.Repeat((byte)1, 8).ToArray(); break;
            case "short-token": key = new byte[7]; break;
            case "zero-token": key = new byte[8]; break;
            case "missing-full-key": full = true; break;
        }
        Assert.That(UnityTargetAssemblyScope.Classify("UnityEngine.CoreModule", version, culture, key, full),
            Is.EqualTo(UnityTargetAssemblyKind.UnresolvedTargetReference));
    }

    [Test]
    public void AbsentUnsignedIdentityDiffersFromAnExplicitToken()
    {
        Assert.That(UnityTargetAssemblyScope.Classify("UnityEngine.CoreModule", new Version(0, 0, 0, 0), "", null),
            Is.EqualTo(UnityTargetAssemblyKind.TargetReference));
        Assert.That(UnityTargetAssemblyScope.Classify("UnityEngine.CoreModule", new Version(0, 0, 0, 0), null, []),
            Is.EqualTo(UnityTargetAssemblyKind.TargetReference));
        Assert.That(UnityTargetAssemblyScope.Classify("UnityEngine.CoreModule", new Version(0, 0, 0, 0), null, new byte[8]),
            Is.EqualTo(UnityTargetAssemblyKind.UnresolvedTargetReference));
    }

    [Test]
    public void FullPublicKeysNormalizeWithoutTreatingTheBlobAsAToken()
    {
        var identity = typeof(object).Assembly.GetName();
        var key = identity.GetPublicKey()!;
        var before = (byte[])key.Clone();
        Assert.That(UnityTargetAssemblyScope.TryNormalizePublicKey(key, true, out var token), Is.True);
        Assert.That(token, Is.EqualTo(identity.GetPublicKeyToken()));
        Assert.That(key, Is.EqualTo(before));
        Assert.That(UnityTargetAssemblyScope.TryNormalizePublicKey(key, false, out _), Is.False);
        Assert.That(UnityTargetAssemblyScope.TryNormalizePublicKey(null, true, out _), Is.False);
    }

    [Test]
    public void FrameworkTokenNormalizationAndTargetIdentityUseTheSameRule()
    {
        // The standard ECMA key is a public framework identity, not a player fingerprint.
        byte[] key = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];
        var reference = new AssemblyReference("mscorlib", new Version(4, 0, 0, 0))
        {
            HasPublicKey = true,
            PublicKeyOrToken = key,
        };
        Assert.That(Unity2021TargetFrameworkAssemblies.HasTargetIdentity(reference), Is.True);
        Assert.That(UnityTargetAssemblyScope.Classify("mscorlib", reference.Version, null, key, true),
            Is.EqualTo(UnityTargetAssemblyKind.TargetReference));
        reference.HasPublicKey = false;
        Assert.That(Unity2021TargetFrameworkAssemblies.HasTargetIdentity(reference), Is.False);
        Assert.That(UnityTargetAssemblyScope.Classify("mscorlib", reference.Version, null, null),
            Is.EqualTo(UnityTargetAssemblyKind.UnresolvedTargetReference));
    }

    [Test]
    public void AFullKeyCannotOverwriteAConflictingRetainedToken()
    {
        byte[] key = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];
        var token = new byte[8];
        var before = (byte[])token.Clone();
        Assert.That(UnityTargetAssemblyScope.ClassifyIdentity("mscorlib", new Version(4, 0, 0, 0), null, key, token),
            Is.EqualTo(UnityTargetAssemblyKind.UnresolvedTargetReference));
        Assert.That(UnityTargetAssemblyScope.ClassifyIdentity("mscorlib", new Version(4, 0, 0, 0), null, key, null),
            Is.EqualTo(UnityTargetAssemblyKind.TargetReference));
        Assert.That(token, Is.EqualTo(before));
    }

    private static AssemblyDefinition CreateAssembly(string name)
    {
        // This authored unit input targets the test host; Unity compilation is a separate gate.
        var core = typeof(object).Assembly.GetName();
        var reference = new AssemblyReference(core.Name, core.Version!) { PublicKeyOrToken = core.GetPublicKeyToken() };
        var module = new ModuleDefinition(name + ".dll", reference);
        var assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Neutral", "Constants", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, 17);
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        return assembly;
    }
}
