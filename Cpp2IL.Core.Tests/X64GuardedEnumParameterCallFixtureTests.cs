using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player evidence for one unchanged enum argument register.</summary>
[NonParallelizable]
public class X64GuardedEnumParameterCallFixtureTests
{
    [Test]
    public void GuardedTailCallRequiresTheSameUnchangedInt32Enum()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ENUM_PASSTHROUGH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_PASSTHROUGH_FIXTURE_INPUT to the synthetic enum fixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("EnumPassthroughFixture")!;
            var forwarder = assembly.Types.Single(type =>
                type.FullName == "EnumPassthroughFixture.EnumForwarder");
            var method = forwarder.Methods.Single(candidate => candidate.Name == "Forward");
            var proof = X64GuardedEnumParameterCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target.Name, Is.EqualTo("Accept"));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));
            var enumType = method.Parameters[0].ParameterType;
            Assert.That(proof.Target.Parameters[0].ParameterType, Is.SameAs(enumType));
            Assert.That(enumType.DefaultEnumUnderlyingType,
                Is.SameAs(app.SystemTypes.SystemInt32Type));

            var parameter = method.Parameters[0];
            try
            {
                parameter.OverrideParameterType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64GuardedEnumParameterCallProof.Find(method), Is.Null);
            }
            finally { parameter.OverrideParameterType = null; }

            try
            {
                enumType.OverrideEnumUnderlyingType = app.SystemTypes.SystemByteType;
                Assert.That(X64GuardedEnumParameterCallProof.Find(method), Is.Null);
            }
            finally { enumType.OverrideEnumUnderlyingType = null; }

            Assert.That(X64GuardedEnumParameterCallProof.Find(proof.Target), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
