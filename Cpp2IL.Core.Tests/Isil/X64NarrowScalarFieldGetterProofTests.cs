using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NarrowScalarFieldGetterProofTests
{
    [TestCase(8, false, false)]
    [TestCase(8, false, true)]
    [TestCase(8, true, false)]
    [TestCase(8, true, true)]
    [TestCase(16, false, false)]
    [TestCase(16, false, true)]
    [TestCase(16, true, false)]
    [TestCase(16, true, true)]
    public void OneFieldLoadAndReturnRetainExactWidthAndSign(int width, bool signed, bool nop)
    {
        var body = Body(width, signed, nop);
        var shape = X64NarrowScalarFieldGetterProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Width, Is.EqualTo(width));
            Assert.That(shape.Signed, Is.EqualTo(signed));
            Assert.That(shape.Offset, Is.EqualTo(2));
            Assert.That(shape.Load, Is.EqualTo(body[^2]));
            Assert.That(shape.Return, Is.EqualTo(body[^1]));
        });
    }

    [TestCase("other-base")]
    [TestCase("indexed")]
    [TestCase("negative-offset")]
    [TestCase("unbounded-offset")]
    [TestCase("partial-destination")]
    [TestCase("wide-destination")]
    [TestCase("segment")]
    [TestCase("rep")]
    [TestCase("lock")]
    [TestCase("code32")]
    [TestCase("gap")]
    [TestCase("return-immediate")]
    public void AddressAliasesPrefixesAndOtherReturnAbisStayUnresolved(string mutation)
    {
        var body = Body(16, false, false);
        switch (mutation)
        {
            case "other-base": body[0].MemoryBase = Register.RDX; break;
            case "indexed": body[0].MemoryIndex = Register.R8; break;
            case "negative-offset": body[0].MemoryDisplacement64 = ulong.MaxValue; break;
            case "unbounded-offset": body[0].MemoryDisplacement64 = 4096; break;
            case "partial-destination": body[0].Op0Register = Register.AX; break;
            case "wide-destination": body[0].Op0Register = Register.RAX; break;
            case "segment": body[0].SegmentPrefix = Register.FS; break;
            case "rep": body[0].HasRepPrefix = true; break;
            case "lock": body[0].HasLockPrefix = true; break;
            case "code32": body[0].CodeSize = CodeSize.Code32; break;
            case "gap": body[1].IP++; break;
            case "return-immediate": body[1].Code = Code.Retnq_imm16; break;
        }
        Assert.That(X64NarrowScalarFieldGetterProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void MissingReturnsExtraEffectsAndBranchesDoNotCompleteTheLeaf()
    {
        var body = Body(8, true, false);
        Assert.That(X64NarrowScalarFieldGetterProof.TryProveShape(body[..^1]), Is.Null);
        var effects = Body(8, true, true);
        effects[0].Code = Code.Inc_rm32;
        Assert.That(X64NarrowScalarFieldGetterProof.TryProveShape(effects), Is.Null);
        effects[0].Code = Code.Jmp_rel32_64;
        Assert.That(X64NarrowScalarFieldGetterProof.TryProveShape(effects), Is.Null);
        Assert.That(X64NarrowScalarFieldGetterProof.Find(null), Is.Null);
    }

    private static Instruction[] Body(int width, bool signed, bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        if (width == 8)
        {
            if (signed) assembler.movsx(eax, __byte_ptr[rcx + 2]);
            else assembler.movzx(eax, __byte_ptr[rcx + 2]);
        }
        else
        {
            if (signed) assembler.movsx(eax, __word_ptr[rcx + 2]);
            else assembler.movzx(eax, __word_ptr[rcx + 2]);
        }
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
