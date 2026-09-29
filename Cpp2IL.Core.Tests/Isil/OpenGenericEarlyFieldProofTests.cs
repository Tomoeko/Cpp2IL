using System.IO;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class OpenGenericEarlyFieldProofTests
{
    [TestCase(8)]
    [TestCase(32)]
    [TestCase(64)]
    public void OnlyCompleteNativeReceiverFieldLeavesBindTheirPhysicalWidth(int width)
    {
        var body = Body(width);
        var shape = OpenGenericEarlyFieldProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Width, Is.EqualTo(width));
            Assert.That(shape.Offset, Is.EqualTo(32));
            Assert.That(shape.Load, Is.EqualTo(body[0]));
            Assert.That(shape.Return, Is.EqualTo(body[1]));
        });
    }

    [TestCase("other-base")]
    [TestCase("indexed")]
    [TestCase("below-header")]
    [TestCase("unbounded-offset")]
    [TestCase("wrong-destination")]
    [TestCase("wrong-width")]
    [TestCase("segment")]
    [TestCase("rep")]
    [TestCase("lock")]
    [TestCase("code32")]
    [TestCase("gap")]
    [TestCase("return-immediate")]
    public void AliasingRelocationSensitivePrefixesAndUnprovedReturnsReject(string mutation)
    {
        var body = Body(32);
        switch (mutation)
        {
            case "other-base": body[0].MemoryBase = Register.RDX; break;
            case "indexed": body[0].MemoryIndex = Register.R8; break;
            case "below-header": body[0].MemoryDisplacement64 = 8; break;
            case "unbounded-offset": body[0].MemoryDisplacement64 = 4096; break;
            case "wrong-destination": body[0].Op0Register = Register.ECX; break;
            case "wrong-width": body[0].Code = Code.Mov_r64_rm64; break;
            case "segment": body[0].SegmentPrefix = Register.FS; break;
            case "rep": body[0].HasRepPrefix = true; break;
            case "lock": body[0].HasLockPrefix = true; break;
            case "code32": body[0].CodeSize = CodeSize.Code32; break;
            case "gap": body[1].IP++; break;
            case "return-immediate": body[1].Code = Code.Retnq_imm16; break;
        }
        Assert.That(OpenGenericEarlyFieldProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void AdditionalNativeEffectsAndMissingTerminalRemainUnresolved()
    {
        var body = Body(8);
        Assert.That(OpenGenericEarlyFieldProof.TryProveShape(body[..^1]), Is.Null);
        Assert.That(OpenGenericEarlyFieldProof.TryProveShape([body[0], body[0], body[1]]), Is.Null);
        body[0].Code = Code.Call_rel32_64;
        Assert.That(OpenGenericEarlyFieldProof.TryProveShape(body), Is.Null);
    }

    private static Instruction[] Body(int width)
    {
        var assembler = new Assembler(64);
        switch (width)
        {
            case 8: assembler.movzx(eax, __byte_ptr[rcx + 32]); break;
            case 32: assembler.mov(eax, __dword_ptr[rcx + 32]); break;
            default: assembler.mov(rax, __qword_ptr[rcx + 32]); break;
        }
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
