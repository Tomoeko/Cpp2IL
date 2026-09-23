using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void RuntimeNullIntrinsicTerminatesWithoutInventingManagedConstructionOrPadding()
    {
        var intrinsic = new Instruction(0, OpCode.RuntimeNullThrow, new StringLiteral("synthetic proof token"));
        List<Instruction> body = [intrinsic, new(1, OpCode.NotImplemented, new StringLiteral("unreachable padding"))];
        X86BodyBoundary.AppendFallthroughFailure(body);
        var graph = new ISILControlFlowGraph(body);
        graph.RemoveUnreachableBlocks();
        DeadCodeEliminator.Run(graph);
        Assert.That(graph.Instructions, Is.EqualTo(new[] { intrinsic }));
        Assert.That(intrinsic.IsFallThrough, Is.False);
        Assert.That(intrinsic.Destination, Is.Null);
        var block = graph.Blocks.Single(b => b.Instructions.Contains(intrinsic));
        Assert.That(block.BlockType, Is.EqualTo(BlockType.Interrupt));
        Assert.That(block.Successors, Is.EqualTo(new[] { graph.ExitBlock }));
    }

    [Test]
    public void LateRuntimeNullRecognitionDoesNotEraseAnAlternateEntryToItsSuffix()
    {
        var suffix = new Instruction(3, OpCode.Return);
        var unresolved = new Instruction(1, OpCode.Nop);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.ConditionalJump, suffix, new Register(null, "condition")),
            unresolved, new(2, OpCode.Nop), suffix,
        ]);
        unresolved.OpCode = OpCode.RuntimeNullThrow;
        unresolved.SetOperands(new StringLiteral("synthetic proof token"));
        graph.NormalizeThrowTerminators();
        Assert.That(graph.Instructions, Does.Contain(suffix));
        var terminated = graph.Blocks.Single(b => b.Instructions.Contains(unresolved));
        Assert.That(terminated.Successors, Is.EqualTo(new[] { graph.ExitBlock }));
        Assert.That(terminated.Instructions, Is.EqualTo(new[] { unresolved }));
        Assert.That(graph.Blocks.Single(b => b.Instructions.Contains(suffix)).Predecessors, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RuntimeNullExitUnwindsItsFrameWithoutHidingAnUnbalancedNormalPath(bool unbalancedNormal)
    {
        var intrinsic = new Instruction(4, OpCode.RuntimeNullThrow, new StringLiteral("synthetic proof token"));
        var (context, _, _) = CreateMethod("RuntimeNullFrame", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new(0, OpCode.ShiftStack, Imm(-40)),
            new(1, OpCode.ConditionalJump, intrinsic, new Register(null, "condition")),
            new(2, OpCode.ShiftStack, Imm(unbalancedNormal ? 0 : 40)),
            new(3, OpCode.Return), intrinsic,
        ]);
        StackAnalyzer.Analyze(context);
        Assert.That(context.AnalysisWarnings, Has.Count.EqualTo(unbalancedNormal ? 1 : 0));
    }

    [Test]
    public void UncoalescedRuntimeNullIntrinsicCannotEmitAnOrdinaryNewException()
    {
        var (context, definition, _) = CreateMethod("UncoalescedRuntimeNull", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new(0, OpCode.RuntimeNullThrow, new StringLiteral("synthetic proof token")),
        ]);
        Assert.That(() => IlGenerator.GenerateIl(context, definition), Throws.TypeOf<DecompilerException>());
    }
}
