using System;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void NativeThrowUnwindsFrameWithoutHidingNormalReturnBalance(bool throwFirst)
    {
        var first = throwFirst
            ? new Instruction(2, OpCode.Throw, new Register(null, "exception"))
            : new Instruction(2, OpCode.ShiftStack, Imm(40));
        var second = throwFirst
            ? new Instruction(4, OpCode.ShiftStack, Imm(40))
            : new Instruction(4, OpCode.Throw, new Register(null, "exception"));
        List<Instruction> body =
        [
            new(0, OpCode.ShiftStack, Imm(-40)),
            new(1, OpCode.ConditionalJump, second, new Register(null, "condition")),
            first, new(3, OpCode.Return), second, new(5, OpCode.Return),
        ];
        var (context, _, _) = CreateMethod("FrameAndThrow", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(body);
        StackAnalyzer.Analyze(context);
        Assert.That(context.AnalysisWarnings, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EveryNormalNativeExitRetainsItsOwnUnbalancedFrameWarning(bool unbalancedFirst)
    {
        var secondPath = new Instruction(4, OpCode.ShiftStack, Imm(unbalancedFirst ? 40 : 0));
        List<Instruction> body =
        [
            new(0, OpCode.ShiftStack, Imm(-40)),
            new(1, OpCode.ConditionalJump, secondPath, new Register(null, "condition")),
            new(2, OpCode.ShiftStack, Imm(unbalancedFirst ? 0 : 40)),
            new(3, OpCode.Return), secondPath, new(5, OpCode.Return),
        ];
        var (context, _, _) = CreateMethod("SeparateExits", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(body);
        StackAnalyzer.Analyze(context);
        Assert.That(context.AnalysisWarnings, Has.Count.EqualTo(1));
        Assert.That(context.AnalysisWarnings[0], Does.Contain("non empty stack (-28)"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConflictingNativeStackOffsetsFailAtTheJoin(bool loop)
    {
        var join = new Instruction(3, OpCode.Move, new Register(null, "rax"), new StackOffset(0));
        List<Instruction> body =
        [
            new(0, OpCode.ConditionalJump, join, new Register(null, "condition")),
            new(1, OpCode.ShiftStack, Imm(-8)),
            new(2, OpCode.Jump, join), join, new(4, OpCode.Return),
        ];
        if (loop)
        {
            var start = new Instruction(0, OpCode.ShiftStack, Imm(-8));
            body = [start, new(1, OpCode.ConditionalJump, start, new Register(null, "continue")), new(2, OpCode.Return)];
        }
        var (context, _, _) = CreateMethod("ConflictingFrame", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(body);
        Assert.That(() => StackAnalyzer.Analyze(context),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("stack delta differs at a control-flow join"));
    }

    [Test]
    public void NativeLoopWithBalancedPerIterationFrameSettles()
    {
        var start = new Instruction(0, OpCode.ShiftStack, Imm(-8));
        List<Instruction> body =
        [
            start, new(1, OpCode.Move, new Register(null, "rax"), new StackOffset(0)),
            new(2, OpCode.ShiftStack, Imm(8)),
            new(3, OpCode.ConditionalJump, start, new Register(null, "continue")), new(4, OpCode.Return),
        ];
        var (context, _, _) = CreateMethod("BalancedLoop", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(body);
        StackAnalyzer.Analyze(context);
        Assert.That(context.AnalysisWarnings, Is.Empty);
    }

    [TestCase(OpCode.IndirectJump)]
    [TestCase(OpCode.Invalid)]
    public void AnUnprovedNativeExitCannotResetTheFrame(OpCode terminal)
    {
        var (context, _, _) = CreateMethod("UnprovedExit", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            new(0, OpCode.ShiftStack, Imm(-8)),
            new(1, terminal, new Register(null, "target")),
        ]);
        StackAnalyzer.Analyze(context);
        Assert.That(context.AnalysisWarnings, Has.Count.EqualTo(1));
        Assert.That(context.AnalysisWarnings[0], Does.Contain("non empty stack (-8)"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public void OversizedNativeStackPositionsCannotWrapToAnotherSlot(bool sum)
    {
        var (context, _, _) = CreateMethod("OversizedFrame", _app.SystemTypes.SystemVoidType, []);
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            new(0, OpCode.ShiftStack, new Immediate(sum ? int.MaxValue : (long)int.MaxValue + 1)),
            new(1, OpCode.Move, new Register(null, "rax"), new StackOffset(1)),
            new(2, OpCode.Return),
        ]);
        Assert.That(() => StackAnalyzer.Analyze(context), Throws.TypeOf<OverflowException>());
    }

}
