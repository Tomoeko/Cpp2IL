using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64VirtualScalarZeroReturnShapeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void WholeLeafClearsEveryReturnBitWithoutReadingTheReceiver(bool prefix)
    {
        var body = Decode(prefix ? [0x66, 0x90, 0x0F, 0x57, 0xC0, 0xC3] : [0x0F, 0x57, 0xC0, 0xC3]);
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(body, 0x1000), Is.True);
        var clear = body[^2];
        Assert.That(clear.Op0Register, Is.EqualTo(Register.XMM0));
        Assert.That(clear.Op1Register, Is.EqualTo(Register.XMM0));
        Assert.That(clear.IsIPRelativeMemoryOperand, Is.False);
        Assert.That(body[^1].FlowControl, Is.EqualTo(FlowControl.Return));
    }

    [TestCase("argument-source")]
    [TestCase("other-lane")]
    [TestCase("scalar-memory")]
    [TestCase("missing-return")]
    [TestCase("extra-effect")]
    [TestCase("return-adjustment")]
    [TestCase("discontinuous")]
    [TestCase("locked")]
    [TestCase("wrong-root")]
    public void AnAliasCannotTurnAPrefixOrArgumentDependentValueIntoAnEvidencedZero(string defect)
    {
        var bytes = defect switch
        {
            "argument-source" => new byte[] { 0x0F, 0x57, 0xC1, 0xC3 },
            "other-lane" => [0x0F, 0x57, 0xC9, 0xC3],
            "scalar-memory" => [0xF3, 0x0F, 0x10, 0x01, 0xC3],
            "missing-return" => [0x0F, 0x57, 0xC0],
            "extra-effect" => [0x0F, 0x57, 0xC0, 0xFF, 0x01, 0xC3],
            "return-adjustment" => [0x0F, 0x57, 0xC0, 0xC2, 0x08, 0x00],
            _ => [0x0F, 0x57, 0xC0, 0xC3]
        };
        var body = Decode(bytes);
        if (defect == "discontinuous") { var changed = body[^1]; changed.IP++; body[^1] = changed; }
        if (defect == "locked") { var changed = body[0]; changed.HasLockPrefix = true; body[0] = changed; }
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(body, defect == "wrong-root" ? 0x1001UL : 0x1000UL), Is.False);
    }

    private static Instruction[] Decode(byte[] bytes)
    {
        return X86Utils.Disassemble(bytes, 0x1000, false).ToArray();
    }
}
