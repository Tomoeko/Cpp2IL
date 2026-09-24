using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ComposedReferenceFieldStoreEmissionTests
{
    [TestCase("removed increment")]
    [TestCase("wrong captured value")]
    [TestCase("store before source read")]
    public void FinalStoreRetainsTheProvedEffectsAndValue(string mutation)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT to the synthetic player-input directory.");

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
            var method = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ComposedArrayFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "ReplaceValues");
            method.Analyze();
            var evidence = method.ComposedReferenceFieldStoreEvidence;
            Assert.That(evidence, Is.Not.Null);
            Assert.DoesNotThrow(() => IlGenerator.ValidateComposedReferenceFieldStore(method));

            var graph = method.ControlFlowGraph!;
            var increment = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.MarkerIncrementIp &&
                instruction.OpCode == OpCode.Add);
            var source = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.SourceReadIp &&
                instruction is { OpCode: OpCode.Move,
                    Operands: [LocalVariable, FieldReference] });
            var store = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.DestinationStoreIp &&
                instruction is { OpCode: OpCode.Move,
                    Operands: [FieldReference, LocalVariable] });
            switch (mutation)
            {
                case "removed increment":
                    graph.FindBlockByInstruction(increment)!.Instructions.Remove(increment);
                    break;
                case "wrong captured value":
                    store.SetOperand(1, method.ParameterLocals.Single(local =>
                        !local.IsThis && !local.IsMethodInfo));
                    break;
                case "store before source read":
                    var block = graph.FindBlockByInstruction(source)!;
                    Assert.That(block, Is.SameAs(graph.FindBlockByInstruction(store)));
                    var sourceIndex = block.Instructions.IndexOf(source);
                    var storeIndex = block.Instructions.IndexOf(store);
                    (block.Instructions[sourceIndex], block.Instructions[storeIndex]) =
                        (block.Instructions[storeIndex], block.Instructions[sourceIndex]);
                    break;
            }

            Assert.That(() => IlGenerator.ValidateComposedReferenceFieldStore(method),
                Throws.TypeOf<DecompilerException>().With.Message.Contains(
                    "Composed reference-field store proof"), mutation);
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
