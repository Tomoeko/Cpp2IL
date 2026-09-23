using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests.Isil;

public class X86BodyBoundaryTests
{
    [TestCase("")]
    [TestCase("B807000000")] // Complete MOV, no return in the declared span.
    [TestCase("B8070000000F")] // Iterate stops before the truncated two-byte opcode.
    public void MissingOrTruncatedTerminationKeepsAnExplicitFailure(string hex)
    {
        var native = X86Utils.Iterate(Convert.FromHexString(hex), 0x1000, false);
        var instructions = native.SelectMany(instruction => new X86InstructionSet().GetIsilFromInstruction(instruction)).ToList();
        Reindex(instructions);
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        graph.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(graph);
        Assert.That(graph.Instructions.Last().OpCode, Is.EqualTo(OpCode.Invalid));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Return), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BranchIntoFinalNonterminalKeepsTheBoundaryReachable(bool conditional)
    {
        var final = new Instruction(2, OpCode.Nop);
        var jump = new Instruction(0, conditional ? OpCode.ConditionalJump : OpCode.Jump, final);
        if (conditional)
            jump.AddOperands([new Register(0, "condition")]);
        List<Instruction> instructions = [jump, new(1, OpCode.Return), final];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        graph.RemoveUnreachableBlocks();
        graph.RemoveNops();
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Invalid), Is.True);
    }

    [Test]
    public void LastConditionalBranchRetainsBothItsTargetAndMissingFallthrough()
    {
        var start = new Instruction(0, OpCode.Nop);
        List<Instruction> instructions = [start, new(1, OpCode.ConditionalJump, start, new Register(0, "condition"))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        var branch = graph.Blocks.Single(b => b.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump));
        Assert.That(branch.Successors, Does.Contain(branch));
        Assert.That(branch.Successors.Any(b => b.Instructions.Any(i => i.OpCode == OpCode.Invalid)), Is.True);
    }

    [TestCase(OpCode.Return)]
    [TestCase(OpCode.Throw)]
    [TestCase(OpCode.Jump)]
    [TestCase(OpCode.IndirectJump)]
    public void TerminalTransferDoesNotMakeTrailingPaddingReachable(OpCode terminal)
    {
        var transfer = new Instruction(0, terminal);
        if (terminal == OpCode.Jump)
            transfer.AddOperands([transfer]);
        else if (terminal is OpCode.Throw or OpCode.IndirectJump)
            transfer.AddOperands([new Register(0, "value")]);
        List<Instruction> instructions = [transfer, new(1, OpCode.NotImplemented, new StringLiteral("unreachable padding"))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        graph.RemoveUnreachableBlocks();
        Assert.That(graph.Instructions, Is.EqualTo(new[] { transfer }));
    }

    [Test]
    public void AResolvedNativeTailCallKeepsItsExplicitReturn()
    {
        var call = new Instruction(0, OpCode.CallVoid, new StringLiteral("resolved target"));
        var ret = new Instruction(1, OpCode.Return);
        List<Instruction> instructions = [call, ret, new(2, OpCode.Nop)];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        graph.RemoveUnreachableBlocks();
        Assert.That(graph.Instructions, Is.EqualTo(new[] { call, ret }));
    }

    [Test]
    public void ABranchMayReachTheLastNativeReturnWithoutReachingTheSentinel()
    {
        var ret = new Instruction(2, OpCode.Return);
        List<Instruction> instructions = [new(0, OpCode.Jump, ret), new(1, OpCode.Nop), ret];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = new ISILControlFlowGraph(instructions);
        graph.RemoveUnreachableBlocks();
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Invalid), Is.False);
        Assert.That(graph.Instructions, Does.Contain(ret));
    }

    private static void Reindex(List<Instruction> instructions)
    {
        for (var index = 0; index < instructions.Count; index++)
            instructions[index].Index = index;
    }
}
