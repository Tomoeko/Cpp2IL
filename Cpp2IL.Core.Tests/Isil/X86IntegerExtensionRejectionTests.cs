using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Isil;

public class X86IntegerExtensionRejectionTests
{
    [TestCase("0FBEC1")] // movsx eax, cl
    [TestCase("480FBFC1")] // movsx rax, cx
    [TestCase("4863C1")] // movsxd rax, ecx
    [TestCase("0FB6C1")] // movzx eax, cl
    [TestCase("0FB601")] // movzx eax, byte [rcx]
    [TestCase("0FB6C4")] // movzx eax, ah
    [TestCase("660FB6C1")] // movzx ax, cl
    [TestCase("6698")] // cbw
    [TestCase("98")] // cwde
    [TestCase("4898")] // cdqe
    [TestCase("6699")] // cwd
    public void ExtensionCannotFallBackToAnOrdinaryMoveWithoutMethodProof(string hex)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(hex)));
        var lifted = new X86InstructionSet().GetIsilFromInstruction(decoder.Decode()).Single();
        Assert.That(lifted.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(lifted.Operands[0].ToString(), Does.Contain("source bits and destination register semantics"));
    }
}
