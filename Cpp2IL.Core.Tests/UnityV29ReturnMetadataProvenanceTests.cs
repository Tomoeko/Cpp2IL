using Cpp2IL.Core.SourceEmission;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityV29ReturnMetadataProvenanceTests
{
    [SetUp]
    public void SetUp() => Cpp2IlApi.ResetInternalState();

    [TearDown]
    public void TearDown() => Cpp2IlApi.ResetInternalState();

    [Test]
    [TestCase(28.9f, false)]
    [TestCase(29f, true)]
    [TestCase(29.1f, true)]
    [TestCase(30f, false)]
    [TestCase(31.1f, false)]
    public void VersionGateCoversTheV29Family(float version, bool expected)
    {
        Assert.That(UnityV29ReturnMetadataProvenance.IsV29Family(version), Is.EqualTo(expected));
    }

    [Test]
    public void V311FixtureDoesNotClaimV29ReturnMetadata()
    {
        var app = TestGameLoader.LoadSimple2022Game();
        Assert.That(app.MetadataVersion, Is.EqualTo(31.1f));
        var provenance = UnityV29ReturnMetadataProvenance.Analyze(app, ["mscorlib"]);
        Assert.That(provenance, Is.Empty);
    }
}
