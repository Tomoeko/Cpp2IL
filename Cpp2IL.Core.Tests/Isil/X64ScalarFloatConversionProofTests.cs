using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarFloatConversionProofTests
{
    [TestCase("F30F5AC0C3", 32, 64)]
    [TestCase("F20F5AC0C3", 64, 32)]
    [TestCase("6690F30F5AC0C3", 32, 64)]
    [TestCase("6690F20F5AC0C3", 64, 32)]
    public void CompleteScalarLeafBindsInputAndReturnWidths(string hex, int source, int result)
    {
        var proof = X64ScalarFloatConversionProof.TryProveShape(Decode(hex));
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.SourceWidth, Is.EqualTo(source));
        Assert.That(proof.ResultWidth, Is.EqualTo(result));
    }

    [TestCase("F30F5AC1C3")] // XMM1 is not the original first static argument.
    [TestCase("F20F5AC8C3")] // The result is not returned in XMM0.
    [TestCase("F30F5A00C3")] // Memory reads need their own fault and width proof.
    [TestCase("660F5AC0C3")] // Packed conversion observes additional lanes.
    [TestCase("0F5AC0C3")]
    [TestCase("F30F5AC0E800000000C3")] // Additional calls cannot disappear.
    [TestCase("F30F5AC0C20800")] // A stack-adjusting return is a different ABI.
    [TestCase("67F30F5AC0C3")] // Redundant address prefix is outside the exact leaf encoding.
    [TestCase("90F30F5AC0C3")] // Optional padding is exactly the authenticated two-byte NOP.
    public void NativeLeafRejectsUnprovedEffectsRegistersLanesOrPrefixes(string hex)
        => Assert.That(X64ScalarFloatConversionProof.TryProveShape(Decode(hex)), Is.Null);

    [Test]
    public void NativeInstructionAddressesMustBeContiguous()
    {
        var body = Decode("F30F5AC0C3");
        var returned = body[1];
        returned.IP++;
        body[1] = returned;
        Assert.That(X64ScalarFloatConversionProof.TryProveShape(body), Is.Null);
    }

    private static Instruction[] Decode(string hex) =>
        Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), 0x1000).ToArray();
}
