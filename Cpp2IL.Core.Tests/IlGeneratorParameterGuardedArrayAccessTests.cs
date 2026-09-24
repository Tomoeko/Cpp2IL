using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

/// <summary>
/// Exercises the final typed gate against the neutral exact-target player.
/// Its native input stays local; only the fixture source and mutations are public.
/// </summary>
[NonParallelizable]
public class IlGeneratorParameterGuardedArrayAccessTests
{
    [TestCase("wrong-array-origin")]
    [TestCase("removed-call")]
    [TestCase("wrong-call-receiver")]
    [TestCase("extra-divide")]
    [TestCase("extra-disconnected-block")]
    public void FinalGraphMustPreserveBothParameterReadsAndTheCall(string mutation)
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_PARAMETER_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_PARAMETER_ARRAY_FIXTURE_INPUT to the neutral fixture player-input directory.");
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
            var method = app.GetAssemblyByName("ParameterArrayFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "CompareWithMark");
            method.Analyze();
            var evidence = method.ParameterGuardedArrayAccessEvidence;
            Assert.That(evidence, Is.Not.Null);
            Assert.DoesNotThrow(() => IlGenerator.ValidateParameterGuardedArrayAccesses(method));

            var graph = method.ControlFlowGraph!;
            var first = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.Sites[0].ElementReadIp &&
                instruction is { OpCode: OpCode.Move,
                    Operands: [LocalVariable, ArrayAccess] });
            var second = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.Sites[1].ElementReadIp &&
                instruction is { OpCode: OpCode.Move,
                    Operands: [LocalVariable, ArrayAccess] });
            var call = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.Sites[1]
                    .EffectsSincePreviousAccess[0].Ip &&
                instruction.OpCode == OpCode.CallVoid);
            switch (mutation)
            {
                case "wrong-array-origin":
                    ((ArrayAccess)second.Operands[1]).Array =
                        ((ArrayAccess)first.Operands[1]).Array;
                    break;
                case "removed-call":
                    graph.FindBlockByInstruction(call)!.Instructions.Remove(call);
                    break;
                case "wrong-call-receiver":
                    var receiver = (LocalVariable)call.Operands[1];
                    call.SetOperand(1, new LocalVariable("otherReceiver",
                        receiver.Register.Copy(2), receiver.Type));
                    break;
                case "extra-divide":
                    var index = method.ParameterLocals.Single(local =>
                        local.Name == "index");
                    var quotient = new LocalVariable("syntheticQuotient",
                        index.Register.Copy(2), index.Type);
                    var block = graph.FindBlockByInstruction(call)!;
                    block.Instructions.Insert(block.Instructions.IndexOf(call),
                        new Instruction(999, OpCode.Divide, quotient, index, index)
                        { IntegerBitWidth = 32 });
                    break;
                case "extra-disconnected-block":
                    var detached = new Block();
                    detached.Instructions.Add(new Instruction(999, OpCode.Move,
                        new LocalVariable("syntheticValue",
                            ((LocalVariable)call.Operands[1]).Register.Copy(2),
                            method.AppContext.SystemTypes.SystemInt32Type),
                        new Immediate(1)));
                    graph.Blocks.Insert(1, detached);
                    break;
            }

            Assert.That(() => IlGenerator.ValidateParameterGuardedArrayAccesses(method),
                Throws.TypeOf<DecompilerException>().With.Message.Contains(
                    "Parameter-array guard proof"), mutation);
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
