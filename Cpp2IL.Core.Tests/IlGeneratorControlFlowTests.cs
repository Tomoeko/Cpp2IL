using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(BlockType.Entry)]
    [TestCase(BlockType.Exit)]
    public void SyntheticSentinelEffectsCannotBecomeDefaultValues(BlockType sentinel)
    {
        var (context, definition, _) = CreateMethod("CapturedValue", _app.SystemTypes.SystemInt32Type, []);
        var value = new LocalVariable("captured", new Register(900, "scratch"), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(value);
        var capture = new Instruction(0, OpCode.Move, value, Imm(41));
        Emit(context, definition, [capture, new(1, OpCode.Return, value)]);

        using (var runtime = Load())
            Assert.That(runtime.Type.GetMethod("CapturedValue")!.Invoke(null, null), Is.EqualTo(41));

        var graph = context.ControlFlowGraph!;
        graph.Blocks.Single(block => block.Instructions.Contains(capture)).Instructions.Remove(capture);
        var destination = sentinel == BlockType.Entry ? graph.EntryBlock : graph.ExitBlock;
        destination.Instructions.Add(capture);
        definition.CilMethodBody = null;

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("entry and exit blocks must be empty"));
        Assert.That(definition.CilMethodBody, Is.Null,
            "A dropped capture would otherwise produce valid IL that returns a fabricated zero.");
    }

    [TestCase("reordered-return")]
    [TestCase("unreachable-prefix")]
    public void EmissionCannotStartBeforeTheProvedControlFlowEntry(string mutation)
    {
        var (context, definition, parameters) = CreateMethod("ChooseValue", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemBooleanType]);
        var firstReturn = new Instruction(1, OpCode.Return, Imm(41));
        var secondReturn = new Instruction(2, OpCode.Return, Imm(13));
        Emit(context, definition, [new(0, OpCode.ConditionalJump, secondReturn, parameters[0]),
            firstReturn, secondReturn]);
        using (var runtime = Load())
        {
            var method = runtime.Type.GetMethod("ChooseValue")!;
            Assert.That(method.Invoke(null, [false]), Is.EqualTo(41));
            Assert.That(method.Invoke(null, [true]), Is.EqualTo(13));
        }

        var graph = context.ControlFlowGraph!;
        if (mutation == "reordered-return")
        {
            var returnBlock = graph.Blocks.Single(block => block.Instructions.Contains(secondReturn));
            graph.Blocks.Remove(returnBlock);
            graph.Blocks.Insert(1, returnBlock);
        }
        else
            graph.Blocks.Insert(1, new Block { ID = 900, BlockType = BlockType.Fall,
                Instructions = [new(3, OpCode.Move, parameters[0], Imm(0))] });
        definition.CilMethodBody = null;
        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("control-flow entry"));
        Assert.That(definition.CilMethodBody, Is.Null);
    }

    [Test]
    public void EmptyEntryBridgesPreserveTheFirstEmittedDefinition()
    {
        var (context, definition, _) = CreateMethod("BridgedValue", _app.SystemTypes.SystemInt32Type, []);
        Emit(context, definition, [new(0, OpCode.Return, Imm(41))]);
        var graph = context.ControlFlowGraph!;
        var original = graph.EntryBlock.Successors.Single();
        var empty = new Block { ID = 901, BlockType = BlockType.Fall,
            Predecessors = [graph.EntryBlock], Successors = [original] };
        graph.EntryBlock.Successors = [empty];
        original.Predecessors.Remove(graph.EntryBlock);
        original.Predecessors.Add(empty);
        graph.Blocks.Insert(1, empty);
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("BridgedValue")!.Invoke(null, null), Is.EqualTo(41));
    }
}
