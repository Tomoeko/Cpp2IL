using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64SelectedGenericRegistrationTests
{
    [Test]
    public void OriginalCountsAndSelectedRegistrationIdentitiesCannotBeRewritten()
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
            var app = Cpp2IlApi.CurrentAppContext!;
            var results = X64SelectedGenericRegistrationControls.Run(app, (reference, pointer) =>
                X64OriginalReferenceClassProof.OriginalGenericMethodReference(app, reference, pointer));

            Assert.That(results, Has.Length.EqualTo(19));
            Assert.Multiple(() =>
            {
                foreach (var result in results)
                {
                    TestContext.Out.WriteLine(result.Name + ": accepted=" + result.Accepted +
                        ", restored=" + result.Restored);
                    Assert.That(result.Accepted, Is.EqualTo(result.ExpectedAcceptance), result.Name);
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
