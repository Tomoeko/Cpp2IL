using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class DeadCodeEliminationTests
{
    private static List<Instruction> Live(ISILControlFlowGraph graph)
        => graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.OpCode != OpCode.Nop).ToList();

    [Test]
    public void RemovesDeadDefinitionButKeepsLiveOnes()
    {
        var x = new LocalVariable("x", new Register(null, "x"));
        var dead = new LocalVariable("dead", new Register(null, "dead"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Subtract, dead, x, Imm(1)), // dead's result is never read
            new(2, OpCode.Return, x),
        });

        DeadCodeEliminator.Run(graph);

        var live = Live(graph);
        Assert.That(live.Any(i => i.OpCode == OpCode.Subtract), Is.False, "dead computation should be removed");
        Assert.That(live.Any(i => i.OpCode == OpCode.Move), Is.True, "live definition should remain");
        Assert.That(live.Any(i => i.OpCode == OpCode.Return), Is.True, "terminator should remain");
    }

    [Test]
    public void RemovesDeadChainToFixpoint()
    {
        // x = 5; temp = x - 1; flag = temp < 0; return x
        // 'flag' is unused -> dead; that makes 'temp' unused -> dead too (cascade).
        var x = new LocalVariable("x", new Register(null, "x"));
        var temp = new LocalVariable("temp", new Register(null, "temp"));
        var flag = new LocalVariable("flag", new Register(null, "flag"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, x, Imm(5)),
            new(1, OpCode.Subtract, temp, x, Imm(1)),
            new(2, OpCode.CheckLess, flag, temp, Imm(0)),
            new(3, OpCode.Return, x),
        });

        DeadCodeEliminator.Run(graph);

        var live = Live(graph);
        Assert.That(live.Any(i => i.OpCode == OpCode.CheckLess), Is.False, "dead flag should be removed");
        Assert.That(live.Any(i => i.OpCode == OpCode.Subtract), Is.False, "now-dead temp should be removed (cascade)");
        Assert.That(live.Count, Is.EqualTo(2), "only the live Move and Return should remain");
    }

    [Test]
    public void KeepsCallsEvenWhenResultUnused()
    {
        // A call with no observed result must be kept (side effects).
        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.CallVoid, Imm(0xDEADBEEFUL)),
            new(1, OpCode.Return),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(i => i.OpCode == OpCode.CallVoid), Is.True, "calls must never be removed");
    }

    [Test]
    public void ArrayOperandsCountTheirLocalsAsUses()
    {
        // arr and i are only read inside array operands, so their definitions have to survive
        var array = new LocalVariable("arr", new Register(null, "arr"));
        var index = new LocalVariable("i", new Register(null, "i"));
        var element = new LocalVariable("elem", new Register(null, "elem"));
        var length = new LocalVariable("len", new Register(null, "len"));
        var pointer = new LocalVariable("ptr", new Register(null, "ptr"));

        var graph = new ISILControlFlowGraph(new List<Instruction>
        {
            new(0, OpCode.Move, index, Imm(1)),
            new(1, OpCode.Move, element, new ArrayAccess(array, index)),
            new(2, OpCode.Move, length, new ArrayLength(array)),
            new(3, OpCode.Move, pointer, new AddressOf(new ArrayAccess(array, index))),
            new(4, OpCode.Add, element, element, length),
            new(5, OpCode.Add, element, element, pointer),
            new(6, OpCode.Return, element),
        });

        DeadCodeEliminator.Run(graph);

        Assert.That(Live(graph).Any(i => i.OpCode == OpCode.Move && ReferenceEquals(i.Operands[0], index)), Is.True,
            "index definition is used inside the array operands and must survive");
    }

    [TestCase(OpCode.Divide)]
    [TestCase(OpCode.Modulo)]
    [TestCase(OpCode.DivideUnsigned)]
    [TestCase(OpCode.ModuloUnsigned)]
    public void KeepsUnusedPotentiallyThrowingArithmetic(OpCode operation)
    {
        var value = new LocalVariable("value", new Register(null, "value"));
        var computation = new Instruction(0, operation, value, Imm(10), Imm(0));
        var graph = new ISILControlFlowGraph([computation, new(1, OpCode.Return)]);

        DeadCodeEliminator.Run(graph);

        Assert.That(computation.OpCode, Is.EqualTo(operation), "Discarding a result must not discard divide-by-zero behavior.");
    }

    [Test]
    public void CountsSourceOccurrenceWhenAnInstructionAlsoWritesTheSameLocal()
    {
        // The pipeline also invokes DCE after SSA removal. Losing this definition would replace
        // the dividend with an initialized zero and remove the signed division overflow.
        var value = new LocalVariable("value", new Register(null, "value"));
        var initial = new Instruction(0, OpCode.Move, value, Imm(int.MinValue));
        var division = new Instruction(1, OpCode.Divide, value, value, Imm(-1));
        var graph = new ISILControlFlowGraph([initial, division, new(2, OpCode.Return)]);

        DeadCodeEliminator.Run(graph);

        Assert.Multiple(() =>
        {
            Assert.That(initial.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(division.OpCode, Is.EqualTo(OpCode.Divide));
        });
    }

    [TestCase(OpCode.Move)]
    [TestCase(OpCode.Add)]
    public void KeepsUnusedInstructionsThatEvaluatePotentiallyThrowingLoads(OpCode operation)
    {
        var receiver = new LocalVariable("receiver", new Register(null, "receiver"));
        var index = new LocalVariable("index", new Register(null, "index"));
        var dead = new LocalVariable("dead", new Register(null, "dead"));
        IOperand[] sources = [new MemoryOperand(receiver, index), new ArrayAccess(receiver, index),
            new ArrayLength(receiver), new AddressOf(new ArrayAccess(receiver, index))];

        foreach (var source in sources)
        {
            var receiverDefinition = new Instruction(0, OpCode.Move, receiver, Imm(0));
            var indexDefinition = new Instruction(1, OpCode.Move, index, Imm(0));
            var computation = operation == OpCode.Move
                ? new Instruction(2, operation, dead, source)
                : new Instruction(2, operation, dead, source, Imm(1));
            var graph = new ISILControlFlowGraph([receiverDefinition, indexDefinition, computation, new(3, OpCode.Return)]);

            DeadCodeEliminator.Run(graph);

            Assert.Multiple(() =>
            {
                Assert.That(computation.OpCode, Is.EqualTo(operation), $"Evaluation of {source.GetType().Name} must be preserved.");
                Assert.That(receiverDefinition.OpCode, Is.EqualTo(OpCode.Move), "The receiver/address feeding a retained load is still read.");
            });
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void KeepsUnusedFieldReadIncludingStaticInitialization(bool isStatic)
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var type = app.InjectAssembly("DeadCodeFixture").InjectType("Fixture", "Fields",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public);
        var attributes = System.Reflection.FieldAttributes.Public;
        if (isStatic)
            attributes |= System.Reflection.FieldAttributes.Static;
        var field = type.InjectFieldContext("Value", app.SystemTypes.SystemInt32Type, attributes);
        var receiver = new LocalVariable("receiver", new Register(null, "receiver"));
        var dead = new LocalVariable("dead", new Register(null, "dead"));
        var load = new Instruction(0, OpCode.Move, dead, new FieldReference(field, receiver, 0));
        var graph = new ISILControlFlowGraph([load, new(1, OpCode.Return)]);

        DeadCodeEliminator.Run(graph);

        Assert.That(load.OpCode, Is.EqualTo(OpCode.Move),
            isStatic ? "A static read may trigger class initialization." : "An instance read may throw for a null receiver.");
    }
}
