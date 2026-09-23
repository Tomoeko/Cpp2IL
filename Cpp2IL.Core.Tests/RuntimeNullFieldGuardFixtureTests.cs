using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player binding regression for the synthetic instance-field control.</summary>
[NonParallelizable]
public class RuntimeNullFieldGuardFixtureTests
{
    [TestCase("FieldReads", "Read", false)]
    [TestCase("FieldWrites", "Write", true)]
    [TestCase("LongFieldReads", "Read", false)]
    [TestCase("LongFieldWrites", "Write", true)]
    public void PlayerOnlyFieldAccessRetainsAnImplicitNullCheckAndRejectsChangedMetadata(string typeName, string name, bool isWrite)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIELD_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_GUARD_FIXTURE_INPUT to the public FieldGuardFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("FieldGuardFixture")!.Types
                .Single(type => type.Name == typeName).Methods.Single(candidate => candidate.Name == name);
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(method.NullCheckedFieldAccesses, Has.Count.EqualTo(1));
            Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
            var evidence = method.NullCheckedFieldAccesses[0];
            Assert.That(evidence.IsValidFor(method), Is.True);
            Assert.That(evidence.StoredValue != null, Is.EqualTo(isWrite));
            var attributes = evidence.Field.Attributes;
            try
            {
                evidence.Field.Attributes |= FieldAttributes.Static;
                Assert.That(evidence.IsValidFor(method), Is.False);
            }
            finally { evidence.Field.Attributes = attributes; }
            Assert.That(evidence.IsValidFor(method), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
