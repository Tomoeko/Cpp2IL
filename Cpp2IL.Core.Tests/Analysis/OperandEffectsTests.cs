using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

public class OperandEffectsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void PropagationPreservesPotentiallyThrowingReadsBeforeCalls(bool useSsa)
    {
        var receiver = Local("receiver");
        IOperand[] reads = [new MemoryOperand(receiver), new ArrayAccess(receiver, Imm(0)),
            new ArrayLength(receiver), new AddressOf(new ArrayAccess(receiver, Imm(0)))];
        foreach (var read in reads)
        {
            var value = Local("snapshot");
            var load = new Instruction(0, OpCode.Move, value, read);
            var effect = new Instruction(1, OpCode.CallVoid, Str("SideEffect"));
            var firstUse = new Instruction(2, OpCode.CallVoid, Str("Consume"), value);
            var secondUse = new Instruction(3, OpCode.CallVoid, Str("ConsumeAgain"), value);
            var graph = new ISILControlFlowGraph([load, effect, firstUse, secondUse, new(4, OpCode.Return)]);

            RunPropagation(graph, useSsa, receiver, value);

            Assert.Multiple(() =>
            {
                Assert.That(load.OpCode, Is.EqualTo(OpCode.Move), "A potentially throwing load must not move past a side effect or execute twice.");
                Assert.That(firstUse.Operands[1], Is.SameAs(value));
                Assert.That(secondUse.Operands[1], Is.SameAs(value));
                Assert.That(graph.Instructions.IndexOf(load), Is.LessThan(graph.Instructions.IndexOf(effect)));
            });
        }
    }

    [Test]
    public void ZeroMaskDoesNotEraseEvaluationOfMemoryOrArrayOperands()
    {
        var receiver = Local("receiver");
        IOperand[] reads = [new MemoryOperand(receiver), new ArrayAccess(receiver, Imm(0)), new ArrayLength(receiver)];
        foreach (var read in reads)
        {
            var operation = new Instruction(0, OpCode.And, Local("result"), read, Imm(0));
            var graph = new ISILControlFlowGraph([operation, new(1, OpCode.Return)]);
            ConstantFolder.Run(graph);
            Assert.That(operation.OpCode, Is.EqualTo(OpCode.And), "A zero result does not cancel the operand's evaluation.");
        }

        var pure = new Instruction(0, OpCode.And, Local("result"), Local("value"), Imm(0));
        ConstantFolder.Run(new ISILControlFlowGraph([pure, new(1, OpCode.Return)]));
        Assert.That(pure.OpCode, Is.EqualTo(OpCode.Move), "Already available local values can still be folded.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PropagationKeepsSnapshotOfAddressTakenLocal(bool useSsa)
    {
        var source = Local("source");
        var snapshot = Local("snapshot");
        var copy = new Instruction(1, OpCode.Move, snapshot, source);
        var result = new Instruction(3, OpCode.Return, snapshot);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, source, Imm(5)), copy,
            new(2, OpCode.CallVoid, Str("MutateByReference"), new AddressOf(source)), result]);

        RunPropagation(graph, useSsa, source, snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(result.Operands[0], Is.SameAs(snapshot), "Returning the source after its address escaped loses the prior value.");
        });
    }

    [Test]
    public void PostSsaCopyDoesNotObserveLaterSourceAssignment()
    {
        var source = Local("source");
        var snapshot = Local("snapshot");
        var copy = new Instruction(0, OpCode.Move, snapshot, source);
        var result = new Instruction(2, OpCode.Return, snapshot);
        var graph = new ISILControlFlowGraph([copy, new(1, OpCode.Move, source, Imm(2)), result]);

        RunPropagation(graph, false, source, snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(result.Operands[0], Is.SameAs(snapshot));
        });
    }

    [TestCase(OpCode.Return)]
    [TestCase(OpCode.Throw)]
    public void PostSsaCopyDoesNotCrossJoinWhenAnotherPathChangesItsSource(OpCode terminal)
    {
        var source = Local("source");
        var snapshot = Local("snapshot");
        var copy = new Instruction(0, OpCode.Move, snapshot, source);
        var result = new Instruction(5, terminal, snapshot);
        var unchanged = new Instruction(4, OpCode.Nop);
        var graph = new ISILControlFlowGraph([copy,
            new(1, OpCode.ConditionalJump, unchanged, Local("condition")),
            new(2, OpCode.Move, source, Imm(2)), new(3, OpCode.Jump, result), unchanged, result]);

        RunPropagation(graph, false, source, snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(result.Operands[0], Is.SameAs(snapshot));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PropagationKeepsExplicitWidthConversions(bool useSsa)
    {
        var source = Local("source");
        var result = Local("result");
        var conversion = new Instruction(0, OpCode.Move, result, source) { IntegerBitWidth = 32 };
        var use = new Instruction(1, OpCode.Return, result);
        var graph = new ISILControlFlowGraph([conversion, use]);

        RunPropagation(graph, useSsa, source, result);

        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(use.Operands[0], Is.SameAs(result), "A native width contract cannot be erased by copying its unconverted source.");
    }

    [Test]
    public void SsaCopyUsedInsideArrayOperandRetainsItsDefinition()
    {
        var source = Local("source");
        var copy = Local("copy");
        var value = Local("value");
        var definition = new Instruction(0, OpCode.Move, copy, source);
        var access = new ArrayAccess(copy, Imm(0));
        var graph = new ISILControlFlowGraph([definition,
            new(1, OpCode.Move, value, access), new(2, OpCode.Return, value)]);

        SsaSimplifier.Run(graph, []);

        Assert.That(ReferenceEquals(access.Array, source) || definition.OpCode == OpCode.Move, Is.True,
            "A nested array read must not retain a local whose definition was deleted.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PropagationKeepsStructSnapshotBeforeFieldMutation(bool useSsa)
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var type = app.InjectAssembly("ValueEffectsFixture").InjectType("Fixture", "Value",
            app.SystemTypes.SystemValueTypeType, System.Reflection.TypeAttributes.Public);
        var field = type.InjectFieldContext("Number", app.SystemTypes.SystemInt32Type, System.Reflection.FieldAttributes.Public);
        var source = new LocalVariable("source", new Register(null, "source"), type);
        var snapshot = new LocalVariable("snapshot", new Register(null, "snapshot"), type);
        var copy = new Instruction(0, OpCode.Move, snapshot, source);
        var use = new Instruction(2, OpCode.Return, snapshot);
        var graph = new ISILControlFlowGraph([copy,
            new(1, OpCode.Move, new FieldReference(field, source, 0), Imm(2)), use]);

        RunPropagation(graph, useSsa, source, snapshot);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(use.Operands[0], Is.SameAs(snapshot), "A struct copy must retain its value before the original's field changes.");
    }

    [Test]
    public void AllPassesKeepStaticFieldEvaluationAndInitialization()
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var type = app.InjectAssembly("EffectsFixture").InjectType("Fixture", "Fields",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var field = type.InjectFieldContext("Value", app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var value = Local("value");
        var receiver = Local("storage");
        var load = new Instruction(0, OpCode.Move, value, new FieldReference(field, receiver, 0));
        var use = new Instruction(2, OpCode.CallVoid, Str("Consume"), value);
        var graph = new ISILControlFlowGraph([load, new(1, OpCode.CallVoid, Str("SideEffect")), use, new(3, OpCode.Return)]);

        RunPropagation(graph, true, value, receiver);
        RunPropagation(graph, false, value, receiver);
        DeadCodeEliminator.Run(graph);
        Assert.That(load.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(use.Operands[1], Is.SameAs(value));

        var masked = new Instruction(0, OpCode.And, value, new FieldReference(field, receiver, 0), Imm(0));
        ConstantFolder.Run(new ISILControlFlowGraph([masked, new(1, OpCode.Return)]));
        Assert.That(masked.OpCode, Is.EqualTo(OpCode.And), "The mask cannot suppress class initialization.");
    }

    private static LocalVariable Local(string name) => new(name, new Register(null, name));

    private static void RunPropagation(ISILControlFlowGraph graph, bool useSsa, params LocalVariable[] locals)
    {
        if (useSsa)
        {
            SsaSimplifier.Run(graph, []);
            return;
        }
        var method = (MethodAnalysisContext)RuntimeHelpers.GetUninitializedObject(typeof(MethodAnalysisContext));
        method.ControlFlowGraph = graph;
        method.Locals = locals.ToList();
        method.ParameterLocals = [];
        Simplifier.Simplify(method);
    }
}
