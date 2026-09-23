using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86FloatingComparisonTests
{
    [TestCase("0F2FC1", 32)]
    [TestCase("0F2EC1", 32)]
    [TestCase("660F2FC1", 64)]
    [TestCase("660F2EC1", 64)]
    public void ScalarComparePreservesEveryArchitecturalFlagOutcome(string hex, int width)
    {
        var lifted = Lift(hex);
        Assert.That(lifted.Any(i => i.OpCode is OpCode.NotImplemented or OpCode.UnresolvedValue), Is.False);
        var predicates = lifted.Where(i => i.OpCode == OpCode.FloatCompare).ToArray();
        Assert.That(predicates, Has.Length.EqualTo(3));
        foreach (var instruction in predicates)
        {
            Assert.That(((Immediate)instruction.Operands[3]).Value, Is.EqualTo(width));
            Assert.That(instruction.IntegerBitWidth, Is.Zero);
            Assert.That(instruction.Operands[1].ToString(), Is.EqualTo("xmm0"));
            Assert.That(instruction.Operands[2].ToString(), Is.EqualTo("xmm1"));
        }

        // Intel's four-result flag table, independently of the predicate mask representation.
        foreach (var (outcome, carry, zero, parity) in new[] { (1, 1, 0, 0), (2, 0, 1, 0), (4, 0, 0, 0), (8, 1, 1, 1) })
        {
            var actual = predicates.ToDictionary(i => ((Register)i.Destination!).Name,
                i => (((Immediate)i.Operands[4]).Value & outcome) != 0 ? 1 : 0);
            Assert.That(actual["CF"], Is.EqualTo(carry));
            Assert.That(actual["ZF"], Is.EqualTo(zero));
            Assert.That(actual["PF"], Is.EqualTo(parity));
        }
        foreach (var name in new[] { "AF", "SF", "OF" })
        {
            var flag = lifted.Single(i => i.Destination is Register register && register.Name == name);
            Assert.That(flag.OpCode, Is.EqualTo(OpCode.Move));
            Assert.That(((Immediate)flag.Operands[1]).Value, Is.Zero);
        }
    }

    [TestCase("0F2E01")]
    [TestCase("0F2F01")]
    [TestCase("660F2E01")]
    [TestCase("660F2F01")]
    public void MemoryComparisonRequiresSeparateReadWidthProof(string hex)
    {
        var instruction = Lift(hex).Single();
        Assert.That(instruction.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(instruction.Operands[0].ToString(), Does.Contain("read-width proof"));
    }

    [TestCase("7A00", false)]
    [TestCase("7B00", true)]
    public void ParityBranchesConsumeParityWithTheCorrectPolarity(string hex, bool inverted)
    {
        var code = Lift(hex);
        var branch = code.Last();
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
        if (inverted)
        {
            Assert.That(code[0].OpCode, Is.EqualTo(OpCode.CheckEqual));
            Assert.That(code[0].Operands[1].ToString(), Is.EqualTo("PF"));
            Assert.That(((Immediate)code[0].Operands[2]).Value, Is.Zero);
            Assert.That(branch.Operands[1], Is.EqualTo(code[0].Destination));
        }
        else
            Assert.That(branch.Operands[1].ToString(), Is.EqualTo("PF"));
    }

    private static List<Instruction> Lift(string hex)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(hex)));
        return new X86InstructionSet().GetIsilFromInstruction(decoder.Decode());
    }
}
