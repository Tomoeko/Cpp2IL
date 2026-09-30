using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public class X64NativeInvocationReachingDefinitionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RepeatedDiamondsRetainOneIncomingOrCapturedValue(bool captured)
    {
        var graph = NewGraph();
        var value = new LocalVariable("value", new Register(0, "value"));
        var condition = new LocalVariable("condition", new Register(1, "condition"));
        var start = AddBlock(graph);
        Link(graph.EntryBlock, start);
        var definition = captured ? new Instruction(0, OpCode.Move, value, new Immediate(7)) : null;
        if (definition != null) start.Instructions.Add(definition);
        var last = start;
        for (var layer = 0; layer < 64; layer++)
        {
            var left = AddBlock(graph);
            var right = AddBlock(graph);
            var join = AddBlock(graph);
            last.Instructions.Add(new Instruction(layer + 1, OpCode.ConditionalJump, right, condition));
            Link(last, left);
            Link(last, right);
            Link(left, join);
            Link(right, join);
            last = join;
        }
        var use = new Instruction(65, OpCode.Return, value);
        last.Instructions.Add(use);
        Link(last, graph.ExitBlock);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            captured ? [] : [value], value, use, out var reaching), Is.True);
        Assert.That(reaching, Is.SameAs(definition));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AJoinRejectsMissingOrDifferentDefinitions(bool missing)
    {
        var graph = NewGraph();
        var value = new LocalVariable("value", new Register(0, "value"));
        var left = AddBlock(graph);
        var right = AddBlock(graph);
        var join = AddBlock(graph);
        Link(graph.EntryBlock, left);
        Link(graph.EntryBlock, right);
        Link(left, join);
        Link(right, join);
        left.Instructions.Add(new Instruction(0, OpCode.Move, value, new Immediate(7)));
        if (!missing) right.Instructions.Add(new Instruction(1, OpCode.Move, value, new Immediate(8)));
        var use = new Instruction(2, OpCode.Return, value);
        join.Instructions.Add(use);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [], value, use, out _), Is.False);
    }

    [Test]
    public void AnUnresolvedCycleDoesNotBecomeAProvedIncomingValue()
    {
        var graph = NewGraph();
        var value = new LocalVariable("value", new Register(0, "value"));
        var first = AddBlock(graph);
        var second = AddBlock(graph);
        var use = new Instruction(0, OpCode.Return, value);
        second.Instructions.Add(use);
        Link(graph.EntryBlock, first);
        Link(first, second);
        Link(second, first);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [value], value, use, out _), Is.False);
    }

    [Test]
    public void ADefinitionBeforeTheUseStopsACycleButALaterDefinitionDoesNot()
    {
        var graph = NewGraph();
        var value = new LocalVariable("value", new Register(0, "value"));
        var loop = AddBlock(graph);
        Link(graph.EntryBlock, loop);
        Link(loop, loop);
        var definition = new Instruction(0, OpCode.Move, value, new Immediate(7));
        var use = new Instruction(1, OpCode.Return, value);
        loop.Instructions.AddRange([use, definition]);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [value], value, use, out _), Is.False, "the first iteration cannot use a later capture");
        loop.Instructions.Reverse();
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [value], value, use, out var reaching), Is.True);
        Assert.That(reaching, Is.SameAs(definition));
    }

    [Test]
    public void SeparateQueriesObserveChangedDefinitionsAndIncomingMembership()
    {
        var graph = NewGraph();
        var value = new LocalVariable("value", new Register(0, "value"));
        var body = AddBlock(graph);
        Link(graph.EntryBlock, body);
        var use = new Instruction(0, OpCode.Return, value);
        body.Instructions.Add(use);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [value], value, use, out var incoming), Is.True);
        Assert.That(incoming, Is.Null);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [], value, use, out _), Is.False);
        var definition = new Instruction(1, OpCode.Move, value, new Immediate(7));
        body.Instructions.Insert(0, definition);
        Assert.That(X64NativeNullCheckedInvocationProof.TryReachingDefinition(graph,
            [], value, use, out var captured), Is.True);
        Assert.That(captured, Is.SameAs(definition));
    }

    private static ISILControlFlowGraph NewGraph()
    {
        var graph = new ISILControlFlowGraph([]);
        graph.EntryBlock.Successors.Clear();
        graph.ExitBlock.Predecessors.Clear();
        graph.Blocks = [graph.EntryBlock, graph.ExitBlock];
        return graph;
    }

    private static Block AddBlock(ISILControlFlowGraph graph)
    {
        var block = new Block { ID = graph.Blocks.Count };
        graph.Blocks.Add(block);
        return block;
    }

    private static void Link(Block source, Block target)
    {
        source.Successors.Add(target);
        target.Predecessors.Add(source);
    }
}
