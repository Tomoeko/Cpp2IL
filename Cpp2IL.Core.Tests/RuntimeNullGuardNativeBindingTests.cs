using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class RuntimeNullGuardNativeBindingTests
{
    [Test]
    public void ExactFixtureCallBindingRejectsModifiedMetadataAndAmbiguousNativeOwnership()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LOOP_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LOOP_CALL_FIXTURE_INPUT to the exact synthetic LoopCallFixture player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var target = app.GetAssemblyByName("LoopCallFixture")!.Types.SelectMany(t => t.Methods).Single(m => m.Name == "Step");
            Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target), Is.True);
            var parameter = target.Parameters.Single();
            var rawParameter = parameter.Definition!.RawType!;
            var rawReturn = target.Definition!.RawReturnType!;
            Reject(() => target.Parameters.Clear(), () => target.Parameters.Add(parameter));
            Reject(() => target.Parameters[0] = new InjectedParameterAnalysisContext("replacement", parameter.ParameterType,
                    parameter.Attributes, 0, target), () => target.Parameters[0] = parameter);
            Reject(() => rawParameter.NumMods = 1, () => rawParameter.NumMods = 0);
            Reject(() => rawParameter.Byref = 1, () => rawParameter.Byref = 0);
            Reject(() => rawReturn.Pinned = 1, () => rawReturn.Pinned = 0);
            var binding = app.MethodsByAddress[target.UnderlyingPointer];
            Reject(() => binding.Add(target), () => binding.RemoveAt(binding.Count - 1));
            var moved = new MethodAnalysisContext(target.Definition, app.SystemTypes.SystemObjectType);
            Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(moved), Is.False);

            void Reject(Action change, Action restore)
            {
                change();
                try { Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target), Is.False); }
                finally { restore(); }
                Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target), Is.True);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
