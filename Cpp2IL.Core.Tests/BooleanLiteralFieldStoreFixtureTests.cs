using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional player-only control for stores in longer ordinary callers.</summary>
[NonParallelizable]
public class BooleanLiteralFieldStoreFixtureTests
{
    [Test]
    public void ExactPlayerStoreRequiresCanonicalBooleanLayoutAndTheOriginalProducer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_LITERAL_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_LITERAL_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("BooleanLiteralStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "BooleanStoreOwner");
            var producer = owner.Methods.Single(method => method.Name == "Acquire");
            var receiver = assembly.Types.Single(type => type.Name == "BooleanStoreTarget");
            var neighbor = receiver.Fields.Single(field => field.Name == "Neighbor");
            foreach (var name in new[] { "Enable", "Disable", "EnableAfterMutation" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction =>
                    instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
                var evidence = method.NullCheckedFieldAccesses.Single();
                var literal = (Immediate)evidence.StoredValue!;
                Assert.That(evidence.IsValidFor(method), Is.True);
                Assert.That(BooleanLiteralFieldStoreProof.IsValidFor(method,
                    evidence.Operation, evidence.Access, literal), Is.True);
                var origin = method.ControlFlowGraph.Instructions.Single(instruction =>
                    ReferenceEquals(instruction.Destination, evidence.Receiver));
                Assert.That(origin.Operands[0], Is.SameAs(producer));
                Assert.That(origin.NativeAddress, Is.Not.Null);
                Assert.That(evidence.Operation.NativeAddress, Is.Not.Null);
                Assert.That(method.ControlFlowGraph.Instructions.ToList().IndexOf(origin),
                    Is.LessThan(method.ControlFlowGraph.Instructions.ToList().IndexOf(evidence.Operation)));

                try
                {
                    evidence.Field.OverrideFieldType = app.SystemTypes.SystemByteType;
                    Assert.That(evidence.IsValidFor(method), Is.False);
                }
                finally { evidence.Field.OverrideFieldType = null; }
                try
                {
                    neighbor.OverrideOffset = evidence.Offset;
                    Assert.That(evidence.IsValidFor(method), Is.False);
                }
                finally { neighbor.OverrideOffset = null; }
                try
                {
                    producer.OverrideName = "ChangedProducer";
                    Assert.That(evidence.IsValidFor(method), Is.False);
                }
                finally { producer.OverrideName = null; }
                var originalAddress = origin.NativeAddress;
                try
                {
                    origin.NativeAddress = evidence.Operation.NativeAddress;
                    Assert.That(evidence.IsValidFor(method), Is.False);
                }
                finally { origin.NativeAddress = originalAddress; }
                var originalValue = evidence.Operation.Operands[1];
                try
                {
                    evidence.Operation.SetOperand(1, new Immediate(2));
                    Assert.That(evidence.IsValidFor(method), Is.False);
                }
                finally { evidence.Operation.SetOperand(1, originalValue); }
                Assert.That(evidence.IsValidFor(method), Is.True);

                var originBlock = method.ControlFlowGraph.FindBlockByInstruction(origin)!;
                var storeBlock = method.ControlFlowGraph.FindBlockByInstruction(evidence.Operation)!;
                var originalPosition = originBlock.Instructions.IndexOf(origin);
                try
                {
                    originBlock.Instructions.RemoveAt(originalPosition);
                    storeBlock.Instructions.Insert(storeBlock.Instructions.IndexOf(evidence.Operation) + 1, origin);
                    Assert.That(evidence.IsValidFor(method), Is.False,
                        "The producer's effects must still precede the captured-result null failure.");
                    var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
                    Assert.That(() => IlGenerator.GenerateIl(method, definition),
                        Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-checked field access marker"));
                }
                finally
                {
                    storeBlock.Instructions.Remove(origin);
                    originBlock.Instructions.Insert(originalPosition, origin);
                }
                Assert.That(evidence.IsValidFor(method), Is.True);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
