using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GenericMethodTableProofTests
{
    [Test]
    public void EveryOriginalRowAndCapturedNativeSlotRemainsBound()
    {
        const string environment = "CPP2IL_NATIVE_DIRECT_GENERIC_REFERENCE_INVOCATION_FIXTURE_INPUT";
        var input = Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set " + environment + " to the neutral exact player-input directory.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
                UnityVersion.Parse("2021.3.35f1"));
            var results = X64GenericMethodTableControls.Run(Cpp2IlApi.CurrentAppContext!);
            Assert.That(results, Has.Length.EqualTo(39));
            Assert.Multiple(() =>
            {
                foreach (var result in results)
                {
                    TestContext.Out.WriteLine(result.Name + ": fresh=" + result.FreshAccepted +
                        ", saved=" + result.SavedAccepted + ", selected=" + result.SelectedAccepted +
                        ", restored=" + result.Restored);
                    Assert.That(result.FreshAccepted, Is.EqualTo(result.ExpectedFresh), result.Name);
                    Assert.That(result.SavedAccepted, Is.EqualTo(result.ExpectedSaved), result.Name);
                    if (result.ExpectedSelected is { } selected)
                        Assert.That(result.SelectedAccepted, Is.EqualTo(selected), result.Name);
                    Assert.That(result.Restored, Is.True, result.Name);
                    Assert.That(result.Exception, Is.Null, result.Name);
                }
            });
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
