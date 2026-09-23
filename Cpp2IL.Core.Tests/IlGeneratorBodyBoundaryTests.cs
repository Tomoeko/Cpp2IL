using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void BodyBoundaryCannotInventAVoidOrValueReturn(bool isVoid)
    {
        var type = isVoid ? _app.SystemTypes.SystemVoidType : _app.SystemTypes.SystemInt32Type;
        var (context, definition, _) = CreateMethod("Unterminated", type, []);
        var value = new LocalVariable("returnRegister", new Register(830, "returnRegister"), _app.SystemTypes.SystemInt32Type);
        List<Instruction> instructions = [new(0, OpCode.Move, value, Imm(7))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        Assert.That(() => Emit(context, definition, instructions),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("unproved fallthrough"));
    }

    [Test]
    public void BodyBoundaryAllowsANativeReturnWithUnreachablePadding()
    {
        var (context, definition, _) = CreateMethod("Terminated", _app.SystemTypes.SystemInt32Type, []);
        List<Instruction> instructions = [new(0, OpCode.Return, Imm(7)), new(1, OpCode.NotImplemented, new StringLiteral("padding"))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        context.ControlFlowGraph = new(instructions);
        context.ControlFlowGraph.RemoveUnreachableBlocks();
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("Terminated")!.Invoke(null, []), Is.EqualTo(7));
    }

    [Test]
    public void BodyBoundaryRewriteSplitsMergedThrowSuffixAndPreservesExceptionIdentity()
    {
        var (context, definition, parameters) = CreateMethod("ThrowOnly", _app.SystemTypes.SystemVoidType,
            [_app.SystemTypes.SystemExceptionType]);
        var raise = new Instruction(0, OpCode.CallVoid, new StringLiteral("il2cpp_raise_exception"), parameters[0]);
        List<Instruction> instructions = [raise, new(1, OpCode.NotImplemented, new StringLiteral("unreachable native padding"))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        context.ControlFlowGraph = new(instructions);
        context.ControlFlowGraph.MergeCallBlocks();
        Assert.That(context.ControlFlowGraph.FindBlockByInstruction(raise)!.Instructions.Count, Is.EqualTo(3));
        KeyFunctionRecovery.Run(context);
        Assert.That(context.ControlFlowGraph.Instructions, Is.EqualTo(new[] { raise }));
        Assert.That(context.ControlFlowGraph.FindBlockByInstruction(raise)!.Successors,
            Is.EqualTo(new[] { context.ControlFlowGraph.ExitBlock }));
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        var expected = new System.InvalidOperationException("synthetic exception");
        var error = Assert.Throws<TargetInvocationException>(() => runtime.Type.GetMethod("ThrowOnly")!.Invoke(null, [expected]));
        Assert.That(error!.InnerException, Is.SameAs(expected));
    }

    [Test]
    public void BodyBoundaryRewriteDoesNotHideAnAlternateEntryToTheSuffix()
    {
        var (context, definition, parameters) = CreateMethod("ConditionalThrow", _app.SystemTypes.SystemVoidType,
            [_app.SystemTypes.SystemBooleanType, _app.SystemTypes.SystemExceptionType]);
        var suffix = new Instruction(2, OpCode.Nop);
        var raise = new Instruction(1, OpCode.CallVoid, new StringLiteral("il2cpp_raise_exception"), parameters[1]);
        List<Instruction> instructions = [new(0, OpCode.ConditionalJump, suffix, parameters[0]), raise, suffix];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        context.ControlFlowGraph = new(instructions);
        context.ControlFlowGraph.MergeCallBlocks();
        KeyFunctionRecovery.Run(context);
        Assert.That(context.ControlFlowGraph.Instructions.Any(i => i.OpCode == OpCode.Invalid), Is.True);
        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("unproved fallthrough"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BodyBoundaryThrowRewriteKeepsSurvivingPhiInputAligned(bool reversePredecessors)
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("ThrowOrReturn", types.SystemInt32Type,
            [types.SystemBooleanType, types.SystemExceptionType]);
        var result = new LocalVariable("result", new Register(831, "result"), types.SystemInt32Type);
        context.Locals.Add(result);
        var ret = new Instruction(3, OpCode.Return, result);
        var raise = new Instruction(2, OpCode.CallVoid, new StringLiteral("il2cpp_raise_exception"), parameters[1]);
        var normal = new Instruction(1, OpCode.Jump, ret);
        List<Instruction> instructions = [new(0, OpCode.ConditionalJump, raise, parameters[0]), normal, raise, ret];
        X86BodyBoundary.AppendFallthroughFailure(instructions);
        var graph = context.ControlFlowGraph = new(instructions);
        var join = graph.FindBlockByInstruction(ret)!;
        var throwingBlock = graph.FindBlockByInstruction(raise)!;
        if (reversePredecessors)
            join.Predecessors.Reverse();
        var phi = new Instruction(-1, OpCode.Phi, result);
        phi.AddOperands(join.Predecessors.Select(pred => (IOperand)Imm(ReferenceEquals(pred, throwingBlock) ? 11 : 29)));
        join.Instructions.Insert(0, phi);
        KeyFunctionRecovery.Run(context);
        Assert.That(phi.Operands.Count, Is.EqualTo(2));
        Assert.That(((Immediate)phi.Operands[1]).Value, Is.EqualTo(29));
        SsaForm.Remove(context);
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        var expected = new System.InvalidOperationException("synthetic exception");
        var method = runtime.Type.GetMethod("ThrowOrReturn")!;
        Assert.That(method.Invoke(null, [false, expected]), Is.EqualTo(29));
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [true, expected]));
        Assert.That(error!.InnerException, Is.SameAs(expected));
    }
}
