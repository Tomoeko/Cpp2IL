using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64SmallAggregateFieldGetterProofTests
{
    [TestCase(8, false, false)]
    [TestCase(8, false, true)]
    [TestCase(8, true, false)]
    [TestCase(8, true, true)]
    [TestCase(16, false, false)]
    [TestCase(16, false, true)]
    [TestCase(16, true, false)]
    [TestCase(16, true, true)]
    public void TheNativeLeafReadsOnlyItsDeclaredLowArgumentBits(int width, bool signed, bool nop)
    {
        var body = Body(width, signed, nop);
        var shape = X64SmallAggregateFieldGetterProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Width, Is.EqualTo(width));
            Assert.That(shape.Signed, Is.EqualTo(signed));
            Assert.That(shape.Load.Op1Register, Is.EqualTo(width == 8 ? Register.CL : Register.CX));
            Assert.That(shape.Load, Is.EqualTo(body[^2]));
            Assert.That(shape.Return, Is.EqualTo(body[^1]));
        });
    }

    [TestCase("other-slot")]
    [TestCase("high-byte")]
    [TestCase("wrong-source-width")]
    [TestCase("memory-source")]
    [TestCase("partial-result")]
    [TestCase("wide-result")]
    [TestCase("segment")]
    [TestCase("rep")]
    [TestCase("repne")]
    [TestCase("lock")]
    [TestCase("code32")]
    [TestCase("gap")]
    [TestCase("return-immediate")]
    [TestCase("missing-return")]
    [TestCase("extra-effect")]
    [TestCase("branch")]
    public void OtherArgumentLanesEffectsAndIncompleteBodiesRemainUnproved(string mutation)
    {
        var body = Body(8, false, false);
        switch (mutation)
        {
            case "other-slot": body[0].Op1Register = Register.DL; break;
            case "high-byte": body[0].Op1Register = Register.CH; break;
            case "wrong-source-width": body[0].Op1Register = Register.CX; break;
            case "memory-source": body[0].Op1Kind = OpKind.Memory; body[0].MemoryBase = Register.RCX; break;
            case "partial-result": body[0].Op0Register = Register.AX; break;
            case "wide-result": body[0].Op0Register = Register.RAX; break;
            case "segment": body[0].SegmentPrefix = Register.FS; break;
            case "rep": body[0].HasRepPrefix = true; break;
            case "repne": body[0].HasRepnePrefix = true; break;
            case "lock": body[0].HasLockPrefix = true; break;
            case "code32": body[0].CodeSize = CodeSize.Code32; break;
            case "gap": body[1].IP++; break;
            case "return-immediate": body[1].Code = Code.Retnq_imm16; break;
            case "missing-return": body = body[..^1]; break;
            case "extra-effect": body = Body(8, false, true); body[0].Code = Code.Inc_rm32; break;
            case "branch": body = Body(8, false, true); body[0].Code = Code.Jmp_rel32_64; break;
        }
        Assert.That(X64SmallAggregateFieldGetterProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void MissingMetadataCannotTurnAnAggregateIntoAScalar()
    {
        Assert.That(X64SmallAggregateFieldGetterProof.Find(null), Is.Null);
    }

    private static Instruction[] Body(int width, bool signed, bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        if (width == 8)
        {
            if (signed) assembler.movsx(eax, cl);
            else assembler.movzx(eax, cl);
        }
        else
        {
            if (signed) assembler.movsx(eax, cx);
            else assembler.movzx(eax, cx);
        }
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
