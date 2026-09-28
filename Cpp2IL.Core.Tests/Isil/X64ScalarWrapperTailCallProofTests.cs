using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarWrapperTailCallProofTests
{
    [Test]
    public void ExactTailDistinguishesZeroAndOneForwardedValue()
    {
        var zero = X64ScalarWrapperTailCallProof.TryProveShape(
            Decode("33D2E910000000"));
        var one = X64ScalarWrapperTailCallProof.TryProveShape(
            Decode("4533C0E910000000"));

        Assert.Multiple(() =>
        {
            Assert.That(zero?.HasValueArgument, Is.False);
            Assert.That(one?.HasValueArgument, Is.True);
            Assert.That(zero?.TargetAddress, Is.EqualTo(0x1017));
            Assert.That(one?.TargetAddress, Is.EqualTo(0x1018));
        });
    }

    [TestCase("33C0E910000000")]
    [TestCase("33D2E810000000")]
    [TestCase("33D290E910000000")]
    [TestCase("6633D2E910000000")]
    public void ChangedMethodInfoRegisterOrExitDoesNotProveTail(string bytes)
        => Assert.That(X64ScalarWrapperTailCallProof.TryProveShape(Decode(bytes)),
            Is.Null);

    private static List<Instruction> Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(bytes), 0x1000);
        var result = new List<Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
