using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class NarrowFieldEqualityProofTests
{
    private ApplicationAnalysisContext _app = null!;

    [OneTimeSetUp]
    public void LoadPublicTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
    }

    [OneTimeTearDown]
    public void ReleasePublicTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
    }

    [TestCase(OpCode.CheckEqual)]
    [TestCase(OpCode.CheckNotEqual)]
    public void IndependentlyProvedFieldCanBeCapturedOnceAndComparedWithZero(OpCode opcode)
    {
        var (instructions, _, _) = GraphInstructions();
        instructions[1].OpCode = opcode;
        Assert.That(() => NarrowFieldEqualityProof.Validate(new(instructions), _ => true), Throws.Nothing);
    }

    [Test]
    public void BooleanSimplificationCannotEraseTheNativeReadWidthContract()
    {
        var (instructions, captured, field) = GraphInstructions();
        captured.Type = _app.SystemTypes.SystemBooleanType;
        var method = new InjectedMethodAnalysisContext(field.Field.DeclaringType, "Read", _app.SystemTypes.SystemBooleanType,
            MethodAttributes.Public, []) { ControlFlowGraph = new ISILControlFlowGraph(instructions) };
        BooleanFlagSimplifier.Run(method);
        Assert.That(instructions[1].OpCode, Is.EqualTo(OpCode.CheckEqual));
        Assert.That(instructions[1].IntegerBitWidth, Is.EqualTo(8));
    }

    [Test]
    public void ADeclaredByteTypeWithoutNativeFieldLayoutIsNotEnough()
    {
        var (_, _, field) = GraphInstructions();
        Assert.That(NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(field), Is.False);
    }

    [TestCase("unproved-field")]
    [TestCase("changed-capture-type")]
    [TestCase("overwritten-capture")]
    [TestCase("addressed-capture")]
    [TestCase("nonzero-comparison")]
    [TestCase("untyped-memory")]
    [TestCase("capture-after-comparison")]
    [TestCase("different-block")]
    [TestCase("missing-native-capture-width")]
    [TestCase("ordering-comparison")]
    public void MissingFieldOrValueFlowProofRemainsUnresolved(string defect)
    {
        var (instructions, captured, field) = GraphInstructions();
        var capture = instructions[0];
        var comparison = instructions[1];
        switch (defect)
        {
            case "changed-capture-type": captured.Type = _app.SystemTypes.SystemInt32Type; break;
            case "overwritten-capture": instructions.Insert(1, new(3, OpCode.Move, captured, new Immediate(0))); break;
            case "addressed-capture": instructions.Insert(1, new(3, OpCode.Move, Local("address"), new AddressOf(captured))); break;
            case "nonzero-comparison": comparison.SetOperand(2, new Immediate(1)); break;
            case "untyped-memory": capture.SetOperand(1, new MemoryOperand(field.Local, addend: field.Offset)); break;
            case "capture-after-comparison": instructions[0] = comparison; instructions[1] = capture; break;
            case "different-block": instructions.Insert(1, new(3, OpCode.Jump, comparison)); break;
            case "missing-native-capture-width": capture.IntegerBitWidth = 0; break;
            case "ordering-comparison": comparison.OpCode = OpCode.CheckLess; break;
        }
        Assert.That(() => NarrowFieldEqualityProof.Validate(new ISILControlFlowGraph(instructions), _ => defect != "unproved-field"),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Native byte"));
    }

    private (List<Instruction> Instructions, LocalVariable Captured, FieldReference Field) GraphInstructions()
    {
        var owner = new InjectedTypeAnalysisContext(_app.AssembliesByName["Assembly-CSharp"], "Synthetic", "ByteFields",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var field = new InjectedFieldAnalysisContext("Value", _app.SystemTypes.SystemByteType, FieldAttributes.Public, owner, 16);
        var receiver = new LocalVariable("receiver", new Register(0, "receiver"), owner);
        var reference = new FieldReference(field, receiver, 16);
        var captured = new LocalVariable("captured", new Register(1, "captured"), field.FieldType);
        var result = new LocalVariable("result", new Register(2, "result"), _app.SystemTypes.SystemBooleanType);
        return ([
            new(0, OpCode.Move, captured, reference) { IntegerBitWidth = 8 },
            new(1, OpCode.CheckEqual, result, captured, new Immediate(0)) { IntegerBitWidth = 8 },
            new(2, OpCode.Return, result),
        ], captured, reference);
    }

    private static LocalVariable Local(string name) => new(name, new Register(null, name), null);
}
