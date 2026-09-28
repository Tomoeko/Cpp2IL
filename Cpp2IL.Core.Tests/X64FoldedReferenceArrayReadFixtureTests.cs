using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64FoldedReferenceArrayReadFixtureTests
{
    [Test]
    public void EveryAliasedGetterMustKeepItsOwnFieldLayout()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_REFERENCE_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FOLDED_REFERENCE_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("FoldedReferenceArrayFixture")!;
            var firstOwner = assembly.Types.Single(type => type.Name == "CatalogA");
            var secondOwner = assembly.Types.Single(type => type.Name == "CatalogB");
            var firstField = firstOwner.Fields.Single(field => field.Name == "Items");
            var secondField = secondOwner.Fields.Single(field => field.Name == "Items");

            foreach (var (name, index) in new[] { ("get_First", 0), ("get_Second", 1) })
            {
                var first = firstOwner.Methods.Single(method => method.Name == name);
                var second = secondOwner.Methods.Single(method => method.Name == name);
                Assert.That(first.UnderlyingPointer, Is.EqualTo(second.UnderlyingPointer));
                Assert.That(app.MethodsByAddress[first.UnderlyingPointer], Has.Count.EqualTo(2));
                Assert.Multiple(() =>
                {
                    Assert.That(X64FixedReferenceArrayReadProof.Find(first),
                        Is.EqualTo(new X64FixedReferenceArrayReadProof.Evidence(firstField, index)));
                    Assert.That(X64FixedReferenceArrayReadProof.Find(second),
                        Is.EqualTo(new X64FixedReferenceArrayReadProof.Evidence(secondField, index)));
                });

                try
                {
                    secondField.OverrideOffset = secondField.DefaultOffset + 8;
                    Assert.Multiple(() =>
                    {
                        Assert.That(X64FixedReferenceArrayReadProof.Find(first), Is.Null);
                        Assert.That(X64FixedReferenceArrayReadProof.Find(second), Is.Null);
                    });
                }
                finally { secondField.OverrideOffset = null; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
