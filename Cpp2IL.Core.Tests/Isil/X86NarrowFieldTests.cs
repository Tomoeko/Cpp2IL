using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86NarrowFieldTests
{
    [Test]
    public void DirectMemoryZeroComparisonCapturesOneReadAndOnlyProvesZeroFlag()
    {
        var instructions = Lift("80791000"); // cmp byte[rcx+16],0
        var read = instructions.Single(i => i.OpCode == OpCode.Move);
        Assert.That(read.IntegerBitWidth, Is.EqualTo(8));
        Assert.That(read.Operands[1], Is.TypeOf<MemoryOperand>());
        var compare = instructions.Single(i => i.OpCode == OpCode.CheckEqual);
        Assert.That(compare.IntegerBitWidth, Is.EqualTo(8));
        Assert.That(compare.Operands[1], Is.EqualTo(read.Destination));
        Assert.That(((Register)compare.Destination!).Name, Is.EqualTo("ZF"));
        Assert.That(instructions.Where(i => i.OpCode == OpCode.UnresolvedValue).Select(i => ((Register)i.Destination!).Name),
            Is.EquivalentTo(new[] { "CF", "PF", "AF", "SF", "OF" }));
    }

    [TestCase("80791001")] // nonzero literal
    [TestCase("80F900")] // partial register CMP
    [TestCase("84DB")] // volatile load/barrier is followed by TEST BL,BL
    [TestCase("6683791001")] // word memory CMP with nonzero immediate8to16
    [TestCase("668179100001")] // word memory CMP with nonzero immediate16
    [TestCase("6683F900")] // partial-register word CMP
    [TestCase("646683791000")] // segment-relative word memory
    [TestCase("676683791000")] // address-size override truncates receiver to ECX
    [TestCase("66837C111000")] // indexed word memory
    [TestCase("66833D0000000000")] // RIP-relative word global
    [TestCase("6480791000")] // segment-relative byte memory
    [TestCase("6780791000")] // address-size override truncates the receiver to ECX
    [TestCase("807C111000")] // indexed memory is not a direct declared field
    [TestCase("803D0000000000")] // RIP-relative global is not an instance field
    public void UnprovedWidthRegisterAndAddressFormsStayExplicit(string bytes)
    {
        Assert.That(Lift(bytes).Single().OpCode, Is.EqualTo(OpCode.NotImplemented));
    }

    [TestCase("6683791000")] // cmp word[rcx+16],imm8 sign-extended to16
    [TestCase("668179100000")] // cmp word[rcx+16],imm16
    public void WordZeroComparisonCapturesOneWordReadWithoutProvingOrderingFlags(string bytes)
    {
        var instructions = Lift(bytes);
        var read = instructions.Single(i => i.OpCode == OpCode.Move);
        Assert.That(read.IntegerBitWidth, Is.EqualTo(16));
        Assert.That(read.Operands[1], Is.TypeOf<MemoryOperand>());
        var comparison = instructions.Single(i => i.OpCode == OpCode.CheckEqual);
        Assert.That(comparison.IntegerBitWidth, Is.EqualTo(16));
        Assert.That(comparison.Operands[1], Is.EqualTo(read.Destination));
        Assert.That(comparison.Operands[2], Is.EqualTo(new Immediate(0)));
        Assert.That(((Register)comparison.Destination!).Name, Is.EqualTo("ZF"));
        Assert.That(instructions.Where(i => i.OpCode == OpCode.UnresolvedValue).Select(i => ((Register)i.Destination!).Name),
            Is.EquivalentTo(new[] { "CF", "PF", "AF", "SF", "OF" }));
    }

    private static System.Collections.Generic.List<Instruction> Lift(string bytes)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(bytes)));
        return new X86InstructionSet().GetIsilFromInstruction(decoder.Decode());
    }
}
