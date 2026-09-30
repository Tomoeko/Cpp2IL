using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(BlockType.Entry)]
    [TestCase(BlockType.Exit)]
    public void SyntheticSentinelEffectsCannotBecomeDefaultValues(BlockType sentinel)
    {
        var (context, definition, _) = CreateMethod("CapturedValue", _app.SystemTypes.SystemInt32Type, []);
        var value = new LocalVariable("captured", new Register(900, "scratch"), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(value);
        var capture = new Instruction(0, OpCode.Move, value, Imm(41));
        Emit(context, definition, [capture, new(1, OpCode.Return, value)]);

        using (var runtime = Load())
            Assert.That(runtime.Type.GetMethod("CapturedValue")!.Invoke(null, null), Is.EqualTo(41));

        var graph = context.ControlFlowGraph!;
        graph.Blocks.Single(block => block.Instructions.Contains(capture)).Instructions.Remove(capture);
        var destination = sentinel == BlockType.Entry ? graph.EntryBlock : graph.ExitBlock;
        destination.Instructions.Add(capture);
        definition.CilMethodBody = null;

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("entry and exit blocks must be empty"));
        Assert.That(definition.CilMethodBody, Is.Null,
            "A dropped capture would otherwise produce valid IL that returns a fabricated zero.");
    }
}
