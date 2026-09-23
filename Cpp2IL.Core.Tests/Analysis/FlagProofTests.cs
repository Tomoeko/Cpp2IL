using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class FlagProofTests
{
    private static LocalVariable Local(string name) => new(name, new Register(null, name));

    [Test]
    public void SignOfWrappedSubtractionIsNotSignedLessThan()
    {
        var a = Local("a");
        var b = Local("b");
        var difference = Local("difference");
        var sign = Local("sign");
        var nonnegative = Local("nonnegative");
        List<Instruction> code =
        [
            new(0, OpCode.Subtract, difference, a, b) { IntegerBitWidth = 32 },
            new(1, OpCode.CheckLess, sign, difference, Imm(0)) { IntegerBitWidth = 32 },
            new(2, OpCode.CheckEqual, nonnegative, sign, Imm(0)),
            new(3, OpCode.Return, nonnegative),
        ];
        FlagConditionRecovery.Run(new ISILControlFlowGraph(code));
        // int.MinValue - 1 wraps positive: JS and JL give different answers.
        Assert.That(code[1].Operands[1], Is.SameAs(difference));
        Assert.That(code[2].Operands[1], Is.SameAs(sign));
    }

    [TestCase("unrelated-overflow")]
    [TestCase("wrong-xor-input")]
    [TestCase("wrong-width")]
    [TestCase("multiple-definitions")]
    public void SignedRecoveryRequiresCompleteMatchingOverflowProof(string invalidity)
    {
        var a = Local("a");
        var b = Local("b");
        var difference = Local("difference");
        var operandsXor = Local("operandsXor");
        var resultXor = Local("resultXor");
        var bits = Local("bits");
        var sign = Local("sign");
        var overflow = Local("overflow");
        var condition = Local("condition");
        List<Instruction> code =
        [
            new(0, OpCode.Subtract, difference, a, b) { IntegerBitWidth = 32 },
            new(1, OpCode.Xor, operandsXor, a, b) { IntegerBitWidth = 32 },
            new(2, OpCode.Xor, resultXor, a, difference) { IntegerBitWidth = 32 },
            new(3, OpCode.And, bits, operandsXor, resultXor) { IntegerBitWidth = 32 },
            new(4, OpCode.CheckLess, overflow, bits, Imm(0)) { IntegerBitWidth = 32 },
            new(5, OpCode.CheckLess, sign, difference, Imm(0)) { IntegerBitWidth = 32 },
            new(6, OpCode.CheckEqual, condition, sign, overflow),
        ];
        switch (invalidity)
        {
            case "unrelated-overflow": code[4].SetOperand(1, Local("unrelated")); break;
            case "wrong-xor-input": code[2].SetOperand(1, b); break;
            case "wrong-width": code[4].IntegerBitWidth = 64; break;
            case "multiple-definitions": code.Insert(6, new(6, OpCode.Move, overflow, Imm(0))); break;
        }
        code.Add(new(code.Count, OpCode.Return, condition));
        for (var i = 0; i < code.Count; i++)
            code[i].Index = i;
        FlagConditionRecovery.Run(new ISILControlFlowGraph(code));
        var predicate = code.Single(i => ReferenceEquals(i.Destination, condition));
        Assert.That(predicate.OpCode, Is.EqualTo(OpCode.CheckEqual));
        Assert.That(predicate.Operands[1], Is.SameAs(sign));
        Assert.That(predicate.Operands[2], Is.SameAs(overflow));
    }

    [Test]
    public void UnsignedAboveRequiresZeroAndCarryFromTheSameComparison()
    {
        var a = Local("a");
        var b = Local("b");
        var unrelated = Local("unrelated");
        var carry = Local("carry");
        var difference = Local("difference");
        var zero = Local("zero");
        var noCarry = Local("noCarry");
        var notZero = Local("notZero");
        var condition = Local("condition");
        List<Instruction> code =
        [
            new(0, OpCode.CheckLessUnsigned, carry, a, b) { IntegerBitWidth = 32 },
            new(1, OpCode.Subtract, difference, a, unrelated) { IntegerBitWidth = 32 },
            new(2, OpCode.CheckEqual, zero, difference, Imm(0)) { IntegerBitWidth = 32 },
            new(3, OpCode.CheckEqual, noCarry, carry, Imm(0)),
            new(4, OpCode.CheckEqual, notZero, zero, Imm(0)),
            new(5, OpCode.And, condition, noCarry, notZero),
            new(6, OpCode.Return, condition),
        ];
        FlagConditionRecovery.Run(new ISILControlFlowGraph(code));
        Assert.That(code[5].OpCode, Is.EqualTo(OpCode.And));
        Assert.That(code[3].OpCode, Is.EqualTo(OpCode.CheckGreaterOrEqualUnsigned));
    }

    [TestCase(OpCode.CheckLessUnsigned, 32, -1L, 1L, 0L)]
    [TestCase(OpCode.CheckLessUnsigned, 64, -1L, 1L, 0L)]
    [TestCase(OpCode.CheckGreaterUnsigned, 32, -1L, 1L, 1L)]
    [TestCase(OpCode.CheckGreaterUnsigned, 64, -1L, 1L, 1L)]
    [TestCase(OpCode.CheckLess, 32, 2147483648L, 0L, 1L)]
    [TestCase(OpCode.CheckLess, 64, 2147483648L, 0L, 0L)]
    [TestCase(OpCode.CheckEqual, 32, 4294967295L, -1L, 1L)]
    [TestCase(OpCode.CheckEqual, 64, 4294967295L, -1L, 0L)]
    public void ConstantComparisonsRespectNativeWidth(OpCode opcode, int width, long left, long right, long expected)
    {
        var result = Local("result");
        var comparison = new Instruction(0, opcode, result, Imm(left), Imm(right)) { IntegerBitWidth = width };
        ConstantFolder.Run(new ISILControlFlowGraph([comparison, new(1, OpCode.Return, result)]));
        Assert.That(comparison.OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(comparison.Operands[1], Is.EqualTo(Imm(expected)));
        Assert.That(comparison.IntegerBitWidth, Is.Zero, "a normalized Boolean result is no longer a native-width operation");
    }

    [Test]
    public void ArithmeticIdentityDoesNotDiscardNativeWidth()
    {
        var result = Local("result");
        var source = Local("source");
        var subtraction = new Instruction(0, OpCode.Subtract, result, source, Imm(0)) { IntegerBitWidth = 32 };
        ConstantFolder.Run(new ISILControlFlowGraph([subtraction, new(1, OpCode.Return, result)]));
        Assert.That(subtraction.OpCode, Is.EqualTo(OpCode.Subtract));
        Assert.That(subtraction.IntegerBitWidth, Is.EqualTo(32));
    }

    [Test]
    public void FoldingDoesNotHideUnsupportedWidth()
    {
        var result = Local("result");
        var comparison = new Instruction(0, OpCode.CheckEqual, result, Imm(65536), Imm(0)) { IntegerBitWidth = 16 };
        ConstantFolder.Run(new ISILControlFlowGraph([comparison, new(1, OpCode.Return, result)]));
        Assert.That(comparison.OpCode, Is.EqualTo(OpCode.CheckEqual));
        Assert.That(comparison.IntegerBitWidth, Is.EqualTo(16));
    }
}
