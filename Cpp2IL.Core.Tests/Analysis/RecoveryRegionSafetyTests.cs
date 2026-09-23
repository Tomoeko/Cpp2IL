using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class RecoveryRegionSafetyTests
{
    private static IEnumerable<Instruction> TrappingInstructions()
    {
        yield return new(2, OpCode.Divide, Local("unused"), Imm(1), Imm(0));
        yield return new(2, OpCode.Modulo, Local("unused"), Imm(1), Imm(0));
        yield return new(2, OpCode.Divide, Local("unused"), Imm(long.MinValue), Imm(-1));
        yield return new(2, OpCode.DivideUnsigned, Local("unused"), Imm(1), Imm(0));
        yield return new(2, OpCode.ModuloUnsigned, Local("unused"), Imm(1), Imm(0));
        yield return new(2, OpCode.Move, Local("unused"), new MemoryOperand(Local("address")));
        yield return new(2, OpCode.Add, Local("unused"), new MemoryOperand(Local("address")), Imm(1));
        yield return new(2, OpCode.CheckEqual, Local("unused"), new MemoryOperand(Local("address")), Imm(1));
        yield return new(2, OpCode.Move, Local("unused"), new ArrayLength(Local("array")));
        yield return new(2, OpCode.Move, Local("unused"), new AddressOf(new ArrayAccess(Local("array"), Imm(0))));
        yield return new(2, OpCode.Phi, Local("unused"), new MemoryOperand(Local("address")), Imm(1));
        yield return new(2, OpCode.ShiftRightUnsigned, Local("unused"), new MemoryOperand(Local("address")), Imm(12));
    }

    [TestCaseSource(nameof(TrappingInstructions))]
    public void InterfaceLookupExcisionRetainsUnusedTrappingEvaluation(Instruction trap)
    {
        var (graph, branch, phi, slowCall, klass) = LookupGraph(trap);
        var originalBlocks = graph.Blocks.ToArray();
        ExciseLookup(graph, phi, slowCall, klass);
        Assert.That(graph.Instructions.Contains(trap), Is.True);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        Assert.That(graph.Blocks, Is.EqualTo(originalBlocks));
    }

    [Test]
    public void MatchedSlowHelperDoesNotAuthorizeErasingThrowingArgumentEvaluation()
    {
        var computation = new Instruction(2, OpCode.Add, Local("unused"), Imm(3), Imm(4));
        var (graph, branch, phi, slowCall, klass) = LookupGraph(computation);
        slowCall.AddOperands([new MemoryOperand(Local("receiverAddress"))]);
        ExciseLookup(graph, phi, slowCall, klass);
        Assert.That(graph.Instructions.Contains(slowCall), Is.True);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    [Test]
    public void PureClosedLookupRegionCanStillBeExcised()
    {
        var computation = new Instruction(2, OpCode.Add, Local("unused"), Imm(3), Imm(4));
        var (graph, branch, phi, slowCall, klass) = LookupGraph(computation);
        ExciseLookup(graph, phi, slowCall, klass);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.Jump));
        Assert.That(graph.Instructions.Contains(computation), Is.False);
        Assert.That(graph.Instructions.Contains(slowCall), Is.False);
        Assert.That(phi.OpCode, Is.EqualTo(OpCode.Nop));
    }

    [TestCase(0)]
    [TestCase(4096)]
    public void DeadReadFromMergePhiRequiresItsOwnEffectProof(long offset)
    {
        var computation = new Instruction(2, OpCode.Add, Local("unused"), Imm(3), Imm(4));
        var (graph, branch, phi, slowCall, klass) = LookupGraph(computation, offset);
        var load = graph.Instructions.Single(instruction => instruction.Index == 8);
        ExciseLookup(graph, phi, slowCall, klass);
        Assert.That(load.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(graph.Instructions.Contains(load), Is.True);
        Assert.That(phi.OpCode, Is.EqualTo(OpCode.Phi));
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    [Test]
    public void NestedAddressUseOutsideLookupRegionPreventsExcision()
    {
        var computation = new Instruction(2, OpCode.Add, Local("unused"), Imm(3), Imm(4));
        var (graph, branch, phi, slowCall, klass) = LookupGraph(computation);
        var address = graph.Instructions.Single(instruction => instruction.Index == 8);
        address.OpCode = OpCode.Move;
        address.SetOperands(Local("address"), new AddressOf(new ArrayAccess((LocalVariable)phi.Operands[0], Imm(0))));
        ExciseLookup(graph, phi, slowCall, klass);
        Assert.That(graph.Instructions.Contains(computation), Is.True);
        Assert.That(graph.Instructions.Contains(slowCall), Is.True);
        Assert.That(phi.OpCode, Is.EqualTo(OpCode.Phi), "An address nested around an array access still reads the phi value.");
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    [TestCaseSource(nameof(TrappingInstructions))]
    public void BarrierClassifierRejectsTrappingEvaluationBeforePatternFlags(Instruction trap)
    {
        var sawStore = false;
        var sawShift = false;
        Assert.That(WriteBarrierRecovery.IsBarrierInstruction(trap, ref sawStore, ref sawShift), Is.False);
        Assert.That(sawStore, Is.False);
        Assert.That(sawShift, Is.False, "A memory-reading shift by twelve is not harmless card arithmetic.");
    }

    [Test]
    public void BranchConditionsMustNotEvaluateUnprovedMemory()
    {
        var branch = new Instruction(0, OpCode.ConditionalJump, new Block(), new MemoryOperand(Local("address")));
        Assert.That(RecoveryRegionEffects.CanDiscard(branch), Is.False);
        branch.SetOperand(1, Local("condition"));
        Assert.That(RecoveryRegionEffects.CanDiscard(branch), Is.True);
    }

    [Test]
    public void FlagAddressAndPageShiftDoNotProveAnApplicationStoreIsGcBookkeeping()
    {
        var condition = Local("condition");
        var merge = new Instruction(5, OpCode.Return);
        var store = new Instruction(3, OpCode.Move, new MemoryOperand(Local("applicationAddress")), Imm(1));
        var branch = new Instruction(1, OpCode.ConditionalJump, merge, condition);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.CheckNotEqual, condition, new MemoryOperand(addend: 0x1000), Imm(0)),
            branch, new(2, OpCode.ShiftRightUnsigned, Local("page"), Local("address"), Imm(12)),
            store, new(4, OpCode.Jump, merge), merge]);
        WriteBarrierRecovery.Run(graph, 0x1000);
        Assert.That(graph.Instructions.Contains(store), Is.True);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    [TestCase(OpCode.Divide)]
    [TestCase(OpCode.Modulo)]
    [TestCase(OpCode.DivideUnsigned)]
    [TestCase(OpCode.ModuloUnsigned)]
    public void EquivalentReturnTailRewriteRetainsOneTrappingOperation(OpCode operation)
    {
        var result = Local("result");
        var dividend = Local("dividend");
        var divisor = Local("divisor");
        var tailOperation = new Instruction(3, operation, result, dividend, divisor);
        var mergeOperation = new Instruction(5, operation, result, dividend, divisor);
        var branch = new Instruction(0, OpCode.ConditionalJump, mergeOperation, Local("condition"));
        var body = new Instruction(1, OpCode.Add, Local("unused"), Imm(1), Imm(2));
        var graph = new ISILControlFlowGraph([
            branch, body, new(2, OpCode.Jump, tailOperation), tailOperation, new(4, OpCode.Return, result),
            mergeOperation, new(6, OpCode.Return, result)]);
        var guard = BlockOf(graph, branch);
        var entry = BlockOf(graph, body);
        var tail = BlockOf(graph, tailOperation);
        var merge = BlockOf(graph, mergeOperation);
        var definitions = Definitions(graph);
        Assert.That(WriteBarrierRecovery.TailBlockMatchesMerge(tail, merge, guard, definitions), Is.True);

        // Candidate provenance is checked separately. Isolate the retained equivalent-tail
        // rewrite: replacing one mutually exclusive tail with the same merge must keep its trap.
        WriteBarrierRecovery.Excise(graph, [new(guard, entry, merge, [entry, tail])]);
        Assert.That(graph.Instructions.Count(instruction => instruction.OpCode == operation), Is.EqualTo(1));
        Assert.That(graph.Instructions.Contains(mergeOperation), Is.True);
        Assert.That(graph.Instructions.Contains(tailOperation), Is.False);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.Jump));
    }

    [Test]
    public void ReturnTailWithDifferentTrappingOperandsDoesNotMatch()
    {
        var result = Local("result");
        var tail = new Block { Instructions = [new(0, OpCode.Divide, result, Imm(1), Imm(0)), new(1, OpCode.Return, result)] };
        var merge = new Block { Instructions = [new(2, OpCode.Divide, result, Imm(1), Imm(1)), new(3, OpCode.Return, result)] };
        Assert.That(WriteBarrierRecovery.TailBlockMatchesMerge(tail, merge, new Block(), []), Is.False);
    }

    private static (ISILControlFlowGraph Graph, Instruction Branch, Instruction Phi, Instruction SlowCall, LocalVariable Klass)
        LookupGraph(Instruction computation, long? mergeReadOffset = null)
    {
        var klass = Local("klass");
        var fast = Local("fast");
        var slow = Local("slow");
        var result = Local("invokeData");
        var phi = new Instruction(7, OpCode.Phi, result, fast, slow);
        var slowCall = new Instruction(5, OpCode.Call, Imm(0x2000), slow);
        var branch = new Instruction(1, OpCode.ConditionalJump, slowCall, Local("condition"));
        var mergeRead = mergeReadOffset is { } offset
            ? new Instruction(8, OpCode.Move, Local("unusedRead"), new MemoryOperand(result, addend: offset))
            : new Instruction(8, OpCode.Nop);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, klass, new MemoryOperand(Local("receiver"))), branch, computation,
            new(3, OpCode.Move, fast, Imm(0x3000)), new(4, OpCode.Jump, phi), slowCall,
            new(6, OpCode.Jump, phi), phi, mergeRead, new(9, OpCode.Return)]);
        return (graph, branch, phi, slowCall, klass);
    }

    private static void ExciseLookup(ISILControlFlowGraph graph, Instruction phi, Instruction slowCall, LocalVariable klass)
    {
        var homes = graph.Blocks.SelectMany(block => block.Instructions.Select(instruction => (instruction, block)))
            .ToDictionary(pair => pair.instruction, pair => pair.block);
        InterfaceDispatchRecovery.TryExciseLookup(graph, homes[phi], slowCall, klass, Definitions(graph), homes);
    }

    private static Dictionary<LocalVariable, Instruction> Definitions(ISILControlFlowGraph graph)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable local)
                definitions[local] = instruction;
        return definitions;
    }

    private static Block BlockOf(ISILControlFlowGraph graph, Instruction instruction) => graph.Blocks.Single(block => block.Instructions.Contains(instruction));
    private static LocalVariable Local(string name) => new(name, new Register(null, name));
}
