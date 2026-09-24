using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player mutations of the eight-byte struct tail-call proof.</summary>
[NonParallelizable]
public class X64GuardedStructParameterCallFixtureTests
{
    [Test]
    public void GuardedTailCallRequiresItsValueAbiLayoutAndUniqueTarget()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STRUCT_FORWARD_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STRUCT_FORWARD_CALL_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("StructForwardCallFixture")!;
            var owner = assembly.Types.Single(type => type.FullName ==
                "StructForwardCallFixture.ForwardOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == "Forward");
            Assert.That(X64GuardedParameterTailCallBodyProof.Find(method), Is.Not.Null,
                "The native body does not match the shared tail-call shape.");
            var proof = X64GuardedStructParameterCallProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Target.Name, Is.EqualTo("StorePair"));
            Assert.That(proof.ReceiverField.Name, Is.EqualTo("Receiver"));

            var pair = method.Parameters[0].ParameterType;
            Assert.That(proof.Target.Parameters[0].ParameterType, Is.SameAs(pair));
            var pairField = pair.Fields.Single(field => field.Name == "Second");
            var sourceParameter = method.Parameters[0];
            try
            {
                sourceParameter.OverrideParameterType = app.SystemTypes.SystemInt64Type;
                Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Null);
            }
            finally { sourceParameter.OverrideParameterType = null; }

            try
            {
                pairField.OverrideOffset = 8;
                Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Null);
            }
            finally { pairField.OverrideOffset = null; }

            try
            {
                proof.ReceiverField.OverrideOffset = proof.ReceiverField.DefaultOffset + 8;
                Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Null);
            }
            finally { proof.ReceiverField.OverrideOffset = null; }

            var targetBindings = app.MethodsByAddress[proof.Target.UnderlyingPointer];
            targetBindings.Add(method);
            try { Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Null); }
            finally { targetBindings.RemoveAt(targetBindings.Count - 1); }

            var interior = method.UnderlyingPointer + 1;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            app.MethodsByAddress.Add(interior, new List<MethodAnalysisContext> { method });
            try
            {
                Assert.That(X64GuardedParameterTailCallBodyProof.Find(method), Is.Null);
                Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Null);
            }
            finally { app.MethodsByAddress.Remove(interior); }

            Assert.That(X64GuardedStructParameterCallProof.Find(method), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
