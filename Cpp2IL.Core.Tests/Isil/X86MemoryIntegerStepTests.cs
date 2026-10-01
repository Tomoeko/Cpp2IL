using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Decoder = Iced.Intel.Decoder;
using ByteArrayCodeReader = Iced.Intel.ByteArrayCodeReader;

namespace Cpp2IL.Core.Tests.Isil;

public class X86MemoryIntegerStepTests
{
    [TestCase("FF01", true, 32)]
    [TestCase("FF09", false, 32)]
    [TestCase("48FF01", true, 64)]
    [TestCase("48FF09", false, 64)]
    [TestCase("FE01", true, 0)]
    [TestCase("FE09", false, 0)]
    [TestCase("66FF01", true, 0)]
    [TestCase("66FF09", false, 0)]
    public void MemoryStepRetainsNativeScalarWidthWithoutInventingFlagValues(string encoding, bool increment, int width)
    {
        var native = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(encoding))).Decode();
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native);
        var step = lifted.Single(instruction => instruction.OpCode is OpCode.Add or OpCode.Subtract);
        Assert.Multiple(() =>
        {
            Assert.That(step.OpCode, Is.EqualTo(increment ? OpCode.Add : OpCode.Subtract));
            Assert.That(step.IntegerBitWidth, Is.EqualTo(width));
            Assert.That(step.Operands[0], Is.TypeOf<MemoryOperand>());
            Assert.That(step.Operands[1], Is.SameAs(step.Operands[0]));
            Assert.That(step.Operands[2], Is.TypeOf<Immediate>());
            Assert.That(((Immediate)step.Operands[2]).Value, Is.EqualTo(1));
            Assert.That(lifted.Any(instruction => instruction.OpCode == OpCode.UnresolvedValue &&
                instruction.Destination is Register { Name: "ZF" }), Is.True);
            Assert.That(lifted.Any(instruction => instruction.Destination is Register { Name: "CF" }), Is.False);
        });
    }

    [TestCase("F048FF01")]
    [TestCase("F0FF09")]
    [TestCase("6548FF01")]
    [TestCase("F3FF01")]
    [TestCase("F2FF09")]
    public void PrefixedStepsCannotBecomeOrdinaryManagedArithmetic(string encoding)
    {
        var native = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(encoding))).Decode();
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native);
        Assert.That(lifted.Any(instruction => instruction.OpCode == OpCode.NotImplemented), Is.True);
        Assert.That(lifted.Any(instruction => instruction.OpCode is OpCode.Add or OpCode.Subtract), Is.False);
    }

    [TestCase("6748FF01")]
    [TestCase("6748FF0C11")]
    [TestCase("6748FF048D00000000")]
    [TestCase("6748FF0500000000")]
    [TestCase("67FF01")]
    public void TruncatedAddressesCannotBecomeFullWidthManagedMemory(string encoding)
    {
        var native = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(encoding))).Decode();
        Assert.That(Iced.Intel.RegisterExtensions.GetSize(native.MemoryBase) == 4 ||
            Iced.Intel.RegisterExtensions.GetSize(native.MemoryIndex) == 4, Is.True);
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native);
        Assert.That(lifted.Any(instruction => instruction.OpCode == OpCode.NotImplemented), Is.True);
        Assert.That(lifted.Any(instruction => instruction.OpCode is OpCode.Add or OpCode.Subtract), Is.False);
    }
}
