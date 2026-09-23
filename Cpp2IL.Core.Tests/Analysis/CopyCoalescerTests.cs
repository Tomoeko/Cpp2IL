using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

public class CopyCoalescerTests
{
    [Test]
    public void CoalescesNonoverlappingCopies()
    {
        var source = Local("source", 1);
        var result = Local("result", 2);
        var copy = new Instruction(1, OpCode.Move, result, source);
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, source, Imm(5)), copy, new(2, OpCode.Return, result)]);

        CopyCoalescer.Run(graph);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Nop));
        Assert.That(Evaluate(graph), Is.EqualTo(5));
    }

    [Test]
    public void ReadModifyWriteKeepsEarlierValueLive()
    {
        var source = Local("source", 1);
        var result = Local("result", 2);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, source, Imm(1)), new(1, OpCode.Move, result, Imm(2)),
            new(2, OpCode.Add, source, source, Imm(1)), new(3, OpCode.Move, result, source),
            new(4, OpCode.Return, result)]);

        CopyCoalescer.Run(graph);

        Assert.That(Evaluate(graph), Is.EqualTo(2),
            "The later result definition cannot replace the source value read by source += 1.");
    }

    [Test]
    public void WidthConversionIsNotACopyCandidate()
    {
        var source = Local("source", 1);
        var result = Local("result", 2);
        var conversion = new Instruction(1, OpCode.Move, result, source) { IntegerBitWidth = 32 };
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, source, Imm(0x100000001)), conversion,
            new(2, OpCode.Return, result)]);

        CopyCoalescer.Run(graph);

        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(conversion.Operands[0], Is.Not.SameAs(conversion.Operands[1]));
        Assert.That(conversion.IntegerBitWidth, Is.EqualTo(32));
    }

    [Test]
    public void WidthConversionSurvivesOtherCopyRewrites()
    {
        var source = Local("source", 1);
        var result = Local("result", 2);
        var conversion = new Instruction(1, OpCode.Move, source, source) { IntegerBitWidth = 32 };
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, source, Imm(0x100000001)), conversion,
            new(2, OpCode.Move, result, source), new(3, OpCode.Return, result)]);

        CopyCoalescer.Run(graph);

        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(conversion.IntegerBitWidth, Is.EqualTo(32), "A self-move can still truncate its operand.");
    }

    [Test]
    public void RefSlotVersionsShareTheirInitialization()
    {
        var before = Local("before", 1);
        var after = Local("after", 2);
        var initialize = new Instruction(0, OpCode.Move, before, Imm(5));
        var address = new AddressOf(after);
        var result = new Instruction(2, OpCode.Return, after);
        var graph = new ISILControlFlowGraph([initialize, new(1, OpCode.CallVoid, Str("Mutate"), address), result]);

        CopyCoalescer.Run(graph);

        Assert.That(initialize.Destination, Is.SameAs(address.Target), "The ref call must see the slot's initialized value.");
        Assert.That(result.Operands[0], Is.SameAs(address.Target));
    }

    [Test]
    public void RefSlotCannotMergeAnOldSnapshotLiveAfterMutation()
    {
        var before = Local("before", 1);
        var after = Local("after", 2);
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, before, Imm(5)),
            new(1, OpCode.CallVoid, Str("Mutate"), new AddressOf(after)), new(2, OpCode.Return, before)]);

        Assert.Throws<DecompilerException>(() => CopyCoalescer.Run(graph));
    }

    [Test]
    public void RefSlotRejectsIncompatibleVersionTypes()
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var before = Local("before", 1);
        var after = Local("after", 2);
        before.Type = app.SystemTypes.SystemInt32Type;
        after.Type = app.SystemTypes.SystemInt64Type;
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, before, Imm(5)),
            new(1, OpCode.CallVoid, Str("Mutate"), new AddressOf(after)), new(2, OpCode.Return, after)]);

        Assert.Throws<DecompilerException>(() => CopyCoalescer.Run(graph));
    }

    [Test]
    public void StructFieldMutationCannotChangeACopiedSnapshot()
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var type = app.InjectAssembly("CoalescingFixture").InjectType("Fixture", "Value",
            app.SystemTypes.SystemValueTypeType, System.Reflection.TypeAttributes.Public);
        var field = type.InjectFieldContext("Number", app.SystemTypes.SystemInt32Type, System.Reflection.FieldAttributes.Public);
        var source = Local("source", 1);
        var snapshot = Local("snapshot", 2);
        source.Type = snapshot.Type = type;
        var copy = new Instruction(0, OpCode.Move, snapshot, source);
        var result = new Instruction(2, OpCode.Return, snapshot);
        var graph = new ISILControlFlowGraph([copy,
            new(1, OpCode.Move, new FieldReference(field, source, 0), Imm(2)), result]);

        CopyCoalescer.Run(graph);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(result.Operands[0], Is.Not.SameAs(source));
    }

    [Test]
    public void NestedUsesFollowMergedDefinitions()
    {
        var source = Local("source", 1);
        var copy = Local("copy", 2);
        var receiver = new LocalVariable("array", new Register(2, "array"));
        var definition = new Instruction(0, OpCode.Move, source, Imm(1));
        var copyInstruction = new Instruction(1, OpCode.Move, copy, source);
        var access = new ArrayAccess(receiver, new MemoryOperand(copy));
        var graph = new ISILControlFlowGraph([definition, copyInstruction,
            new(2, OpCode.CallVoid, Str("Consume"), access), new(3, OpCode.Return)]);

        CopyCoalescer.Run(graph);

        Assert.That(copyInstruction.OpCode, Is.EqualTo(OpCode.Nop));
        Assert.That(((MemoryOperand)access.Index).Base, Is.SameAs(definition.Destination),
            "Deleting a copy must also rewrite references nested inside another operand.");
    }

    private static LocalVariable Local(string name, int version) => new(name, new Register(1, "slot", version));

    // Execute the small arithmetic regression to test preserved results, independent of the chosen representatives.
    private static long Evaluate(ISILControlFlowGraph graph)
    {
        var values = new Dictionary<LocalVariable, long>();
        long Read(IOperand operand) => operand is Immediate immediate ? immediate.Value : values[(LocalVariable)operand];
        foreach (var instruction in graph.Instructions)
            switch (instruction.OpCode)
            {
                case OpCode.Move:
                    values[(LocalVariable)instruction.Operands[0]] = Read(instruction.Operands[1]);
                    break;
                case OpCode.Add:
                    values[(LocalVariable)instruction.Operands[0]] = Read(instruction.Operands[1]) + Read(instruction.Operands[2]);
                    break;
                case OpCode.Return:
                    return Read(instruction.Operands[0]);
            }
        Assert.Fail("The regression graph must return a value.");
        return 0;
    }
}
