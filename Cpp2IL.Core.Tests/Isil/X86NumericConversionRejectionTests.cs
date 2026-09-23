using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86NumericConversionRejectionTests
{
    public static IEnumerable<TestCaseData> Cases()
    {
        yield return new("0F5BC1", Mnemonic.Cvtdq2ps);
        yield return new("0F5B01", Mnemonic.Cvtdq2ps);
        yield return new("0F5AC1", Mnemonic.Cvtps2pd);
        yield return new("0F5A01", Mnemonic.Cvtps2pd);
        yield return new("F30FE6C1", Mnemonic.Cvtdq2pd);
        yield return new("F30FE601", Mnemonic.Cvtdq2pd);
        yield return new("660F5AC1", Mnemonic.Cvtpd2ps);
        yield return new("660F5A01", Mnemonic.Cvtpd2ps);
        yield return new("F20F2CC1", Mnemonic.Cvttsd2si); // EAX result
        yield return new("F20F2C01", Mnemonic.Cvttsd2si);
        yield return new("F2480F2CC1", Mnemonic.Cvttsd2si); // RAX result
        yield return new("F2480F2C01", Mnemonic.Cvttsd2si);
    }

    [TestCaseSource(nameof(Cases))]
    public void NumericConversionCannotBeLoweredAsAnOrdinaryMove(string bytes, Mnemonic expected)
    {
        var native = Decode(bytes);
        Assert.That(native.Mnemonic, Is.EqualTo(expected));
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native).Single();
        Assert.That(lifted.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(lifted.Operands.Single(), Is.TypeOf<StringLiteral>());
        Assert.That(lifted.Operands[0].ToString(), Does.Contain(expected.ToString()).IgnoreCase);
    }

    internal static Iced.Intel.Instruction Decode(string bytes) =>
        Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes))).Decode();
}
