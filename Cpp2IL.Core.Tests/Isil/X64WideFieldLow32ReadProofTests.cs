using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64WideFieldLow32ReadProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CompleteLow32FieldReadRetainsItsNativeAddressAndPhysicalWidth(bool nop)
    {
        var body = Body(nop);
        var shape = X64WideFieldLow32ReadProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Offset, Is.EqualTo(16));
            Assert.That(shape.Load, Is.EqualTo(body[^2]));
            Assert.That(shape.Return, Is.EqualTo(body[^1]));
            Assert.That(shape.Load.MemorySize.GetSize(), Is.EqualTo(4));
        });
    }

    [TestCase("other-receiver")]
    [TestCase("indexed")]
    [TestCase("scale")]
    [TestCase("header")]
    [TestCase("negative-offset")]
    [TestCase("unbounded-offset")]
    [TestCase("other-result")]
    [TestCase("wide-read")]
    [TestCase("narrow-read")]
    [TestCase("store")]
    [TestCase("rep")]
    [TestCase("repne")]
    [TestCase("lock")]
    [TestCase("segment")]
    [TestCase("code32")]
    [TestCase("gap")]
    [TestCase("return-immediate")]
    [TestCase("extra-effect")]
    [TestCase("branch")]
    public void OtherWidthsAliasesControlFlowAndEffectsDoNotProveAFieldProjection(string mutation)
    {
        var body = Body(mutation is "extra-effect" or "branch");
        var index = body.Length - 2;
        switch (mutation)
        {
            case "other-receiver": body[index].MemoryBase = Register.RDX; break;
            case "indexed": body[index].MemoryIndex = Register.R8; break;
            case "scale": body[index].MemoryIndexScale = 2; break;
            case "header": body[index].MemoryDisplacement64 = 8; break;
            case "negative-offset": body[index].MemoryDisplacement64 = ulong.MaxValue; break;
            case "unbounded-offset": body[index].MemoryDisplacement64 = 4096; break;
            case "other-result": body[index].Op0Register = Register.EDX; break;
            case "wide-read": body[index].Code = Code.Mov_r64_rm64; body[index].Op0Register = Register.RAX; break;
            case "narrow-read": body[index].Code = Code.Mov_r16_rm16; body[index].Op0Register = Register.AX; break;
            case "store": body[index].Code = Code.Mov_rm32_r32; break;
            case "rep": body[index].HasRepPrefix = true; break;
            case "repne": body[index].HasRepnePrefix = true; break;
            case "lock": body[index].HasLockPrefix = true; break;
            case "segment": body[index].SegmentPrefix = Register.FS; break;
            case "code32": body[index].CodeSize = CodeSize.Code32; break;
            case "gap": body[^1].IP++; break;
            case "return-immediate": body[^1].Code = Code.Retnq_imm16; break;
            case "extra-effect": body[0].Code = Code.Inc_rm32; break;
            case "branch": body[0].Code = Code.Jmp_rel32_64; break;
        }
        Assert.That(X64WideFieldLow32ReadProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void MissingReturnOrAdditionalInstructionsCannotCloseTheBody()
    {
        var body = Body(false);
        Assert.That(X64WideFieldLow32ReadProof.TryProveShape(body[..^1]), Is.Null);
        Assert.That(X64WideFieldLow32ReadProof.TryProveShape(body.Concat(body).ToArray()), Is.Null);
        Assert.That(X64WideFieldLow32ReadProof.Find(null), Is.Null);
    }

    private static Instruction[] Body(bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        assembler.mov(eax, __dword_ptr[rcx + 16]);
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
