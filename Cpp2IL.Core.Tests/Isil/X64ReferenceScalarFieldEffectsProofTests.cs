using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ReferenceScalarFieldEffectsProofTests
{
    private static Instruction[] Body() => Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(
        "534883EC20488B5118488BD94883C110488911E810000000807B24008B43288943200F94C0FF432C8843244883C4205BC3")),
        0x1000).ToArray();

    [Test]
    public void CapturedBytePredicatePrecedesIntegerCopyAndLaterStores()
    {
        var shape = X64ReferenceScalarFieldEffectsProof.TryProveShape(Body());
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Boolean, Is.EqualTo(36));
        Assert.That(shape.IntegerSource, Is.EqualTo(40));
        Assert.That(shape.IntegerDestination, Is.EqualTo(32));
    }

    [TestCase(10, Code.Setne_rm8)]
    [TestCase(11, Code.Dec_rm32)]
    [TestCase(15, Code.Int3)]
    public void NativePredicateCounterAndReturnCannotChange(int index, Code code)
    {
        var body = Body();
        var changed = body[index];
        changed.Code = code;
        body[index] = changed;
        Assert.That(X64ReferenceScalarFieldEffectsProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void NativeEffectReorderingAndInstructionGapsAreRejected()
    {
        var body = Body();
        (body[10], body[11]) = (body[11], body[10]);
        Assert.That(X64ReferenceScalarFieldEffectsProof.TryProveShape(body), Is.Null);
        body = Body();
        var changed = body[5];
        changed.IP++;
        body[5] = changed;
        Assert.That(X64ReferenceScalarFieldEffectsProof.TryProveShape(body), Is.Null);
    }
}
