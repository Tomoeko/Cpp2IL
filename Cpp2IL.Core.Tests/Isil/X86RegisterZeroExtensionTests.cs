using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86RegisterZeroExtensionTests
{
    [TestCase("0FB6C1", "rax", "rcx", 8)] // movzx eax, cl
    [TestCase("0FB7C1", "rax", "rcx", 16)] // movzx eax, cx
    [TestCase("450FB6C1", "r8", "r9", 8)] // movzx r8d, r9b
    [TestCase("0FB6C0", "rax", "rax", 8)] // movzx eax, al reads before the same-parent write
    [TestCase("0FB7C0", "rax", "rax", 16)] // movzx eax, ax reads before the same-parent write
    [TestCase("450FB6C0", "r8", "r8", 8)] // movzx r8d, r8b
    public void LowRegisterBitsAreTruncatedBeforeZeroExtension(
        string bytes, string destination, string source, long sourceBits)
    {
        var extension = Lift(bytes).Single();
        Assert.That(extension.OpCode, Is.EqualTo(OpCode.IntegerExtend));
        Assert.That(extension.Destination, Is.EqualTo(new Register(null, destination)));
        Assert.That(extension.Operands[1], Is.EqualTo(new Register(null, source)));
        Assert.That(extension.Operands.Skip(2).OfType<Immediate>().Select(value => value.Value),
            Is.EqualTo(new[] { sourceBits, 32L, 0L }));
    }

    [TestCase("0FB6C4")] // movzx eax, ah reads bits 8..15, not the low byte of rax
    [TestCase("400FB6C4")] // movzx eax, spl uses the stack pointer
    [TestCase("400FB6C5")] // movzx eax, bpl uses the frame pointer
    [TestCase("0FB7C4")] // movzx eax, sp uses the stack pointer
    [TestCase("0FB701")] // movzx eax, word ptr [rcx] requires memory-width proof
    [TestCase("480FB6C1")] // movzx rax, cl is outside the 32-bit result rule
    [TestCase("660FB6C1")] // movzx ax, cl is outside the 32-bit result rule
    public void UnprovedRegisterAndMemoryFormsRemainExplicit(string bytes)
    {
        Assert.That(Lift(bytes).Single().OpCode, Is.EqualTo(OpCode.NotImplemented));
    }

    private static System.Collections.Generic.List<Instruction> Lift(string bytes)
    {
        var decoder = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(bytes)));
        return new X86InstructionSet().GetIsilFromInstruction(decoder.Decode());
    }
}
