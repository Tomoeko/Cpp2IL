using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86SimdRejectionTests
{
    public static IEnumerable<TestCaseData> Cases()
    {
        yield return new("660F6EC1", Mnemonic.Movd); // GPR -> XMM raw bits
        yield return new("660F7EC0", Mnemonic.Movd); // XMM -> GPR raw bits
        yield return new("660F6E01", Mnemonic.Movd); // load
        yield return new("660F7E01", Mnemonic.Movd); // store
        yield return new("0F6EC1", Mnemonic.Movd); // MMX transfer
        yield return new("0F7EC0", Mnemonic.Movd);
        yield return new("66480F6EC1", Mnemonic.Movq);
        yield return new("66480F7EC0", Mnemonic.Movq);
        yield return new("F30F7EC1", Mnemonic.Movq); // low XMM transfer with high zeroing
        yield return new("660FD6C1", Mnemonic.Movq);
        yield return new("F30F7E01", Mnemonic.Movq);
        yield return new("660FD601", Mnemonic.Movq);
        yield return new("0F6FC1", Mnemonic.Movq); // MMX register/load/store forms
        yield return new("0F6F01", Mnemonic.Movq);
        yield return new("0F7F01", Mnemonic.Movq);
        yield return new("0F28C1", Mnemonic.Movaps);
        yield return new("0F2801", Mnemonic.Movaps);
        yield return new("0F2901", Mnemonic.Movaps);
        yield return new("0F10C1", Mnemonic.Movups);
        yield return new("0F1001", Mnemonic.Movups);
        yield return new("0F1101", Mnemonic.Movups);
        yield return new("660F6FC1", Mnemonic.Movdqa);
        yield return new("660F6F01", Mnemonic.Movdqa);
        yield return new("660F7F01", Mnemonic.Movdqa);
        yield return new("F30F6FC1", Mnemonic.Movdqu);
        yield return new("F30F6F01", Mnemonic.Movdqu);
        yield return new("F30F7F01", Mnemonic.Movdqu);
        yield return new("0FC6C11B", Mnemonic.Shufps);
        yield return new("0FC6011B", Mnemonic.Shufps);
        yield return new("0FC6C0E4", Mnemonic.Shufps); // self/identity still has no typed lane proof
        yield return new("0F14C1", Mnemonic.Unpcklps);
        yield return new("0F1401", Mnemonic.Unpcklps);
        yield return new("0F14C0", Mnemonic.Unpcklps);
        yield return new("0F54C1", Mnemonic.Andps);
        yield return new("0F5401", Mnemonic.Andps);
        yield return new("0F54C0", Mnemonic.Andps);
        yield return new("0F56C1", Mnemonic.Orps);
        yield return new("0F5601", Mnemonic.Orps);
        yield return new("0F56C0", Mnemonic.Orps);
        yield return new("0F57C1", Mnemonic.Xorps);
        yield return new("0F5701", Mnemonic.Xorps);
        yield return new("0F57C0", Mnemonic.Xorps);
    }

    [TestCaseSource(nameof(Cases))]
    public void SimdTransferOrPackedOperationRequiresAnIndependentRepresentationProof(string bytes, Mnemonic expected)
    {
        var native = Decode(bytes);
        Assert.That(native.Mnemonic, Is.EqualTo(expected));
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native).Single();
        Assert.That(lifted.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(lifted.Operands.Single(), Is.TypeOf<StringLiteral>());
        Assert.That(lifted.Operands[0].ToString(), Does.Contain(expected.ToString()).IgnoreCase);
    }

    [TestCase("31C0")]
    [TestCase("4831C0")]
    public void OrdinaryFullWidthIntegerXorZeroRetainsItsExistingScalarMeaning(string bytes)
    {
        var lifted = new X86InstructionSet().GetIsilFromInstruction(Decode(bytes));
        Assert.That(lifted[0].OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(lifted[0].Operands[1], Is.EqualTo(new Immediate(0)));
        Assert.That(lifted.Any(i => i.OpCode == OpCode.NotImplemented), Is.False);
    }

    internal static Iced.Intel.Instruction Decode(string bytes) =>
        Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes))).Decode();
}
