using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void CopyCoalescingPreservesDistinctIncomingParameterIdentities()
    {
        var (context, _, parameters) = CreateMethod("Copy", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemInt32Type]);
        var first = parameters[0];
        var second = parameters[1];
        second.Register = first.Register.Copy(2);
        var copy = new Instruction(0, OpCode.Move, first, second);
        context.ControlFlowGraph = new ISILControlFlowGraph([copy, new(1, OpCode.Return, first)]);

        CopyCoalescer.Run(context);

        Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move),
            "A register-slot match cannot erase a copy between separately initialized managed arguments.");
        Assert.That(copy.Operands[0], Is.SameAs(first));
        Assert.That(copy.Operands[1], Is.SameAs(second));
    }

    [Test]
    public void CoalescedParameterRetainsItsIncomingValueBeforeLaterWrites()
    {
        var (context, definition, parameters) = CreateMethod("Calculate", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type]);
        var incoming = parameters[0];
        var copy = new LocalVariable("copy", incoming.Register.Copy(1), incoming.Type);
        var before = new LocalVariable("before", new Register(1900, "before"), incoming.Type);
        var result = new LocalVariable("result", new Register(1901, "result"), incoming.Type);
        context.Locals.AddRange([copy, before, result]);
        var initialRead = new Instruction(0, OpCode.Add, before, incoming, Imm(1));
        context.ControlFlowGraph = new ISILControlFlowGraph([
            initialRead, new(1, OpCode.Move, copy, incoming),
            new(2, OpCode.Add, copy, copy, Imm(9)),
            new(3, OpCode.Add, result, before, copy), new(4, OpCode.Return, result)]);

        CopyCoalescer.Run(context);

        Assert.That(initialRead.Operands[1], Is.SameAs(incoming));
        Assert.That(context.ControlFlowGraph.Instructions.Single(instruction => instruction.Index == 2).Destination,
            Is.SameAs(incoming), "A merged value must use the initialized managed argument storage.");
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("Calculate")!.Invoke(null, [41]), Is.EqualTo(92));
        Assert.That(runtime.Type.GetMethod("Calculate")!.Invoke(null, [-10]), Is.EqualTo(-10));
    }
}
