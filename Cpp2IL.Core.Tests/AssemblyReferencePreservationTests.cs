using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.Tests;

public class AssemblyReferencePreservationTests
{
    [Test]
    public void DeclaredReferencesSurviveWithoutTypesThatUseThem()
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var application = app.GetAssemblyByName("Assembly-CSharp")!;
        var dependencies = application.Definition!.ReferencedAssemblies
            .Select(definition => app.ResolveContextForAssembly(definition)!).ToArray();
        Assert.That(dependencies, Is.Not.Empty);

        // Remove the consumers: this protects references kept solely by metadata,
        // independently of references that the signature importer would regenerate.
        application.Types.Clear();
        var output = new AsmResolverDllOutputFormatDefault().BuildAssemblies(app)
            .Single(assembly => assembly.Name == application.Name);
        using var stream = new MemoryStream();
        output.WriteManifest(stream, RecoveredAssemblyImageBuilder.Create());
        var roundTrip = AssemblyDefinition.FromBytes(stream.ToArray());
        var references = roundTrip.ManifestModule!.AssemblyReferences;

        foreach (var dependency in dependencies)
        {
            var reference = references.SingleOrDefault(item => item.Name == dependency.Name);
            Assert.That(reference, Is.Not.Null, "A declared dependency disappeared from the written assembly.");
            Assert.Multiple(() =>
            {
                Assert.That(reference!.Version, Is.EqualTo(dependency.Version));
                Assert.That(reference.Culture?.ToString(), Is.EqualTo(dependency.Culture));
                Assert.That(reference.PublicKeyOrToken, Is.EqualTo(dependency.PublicKeyToken));
            });
        }
    }
}
