using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64EnumIntegerConversionProofTests
{
    [TestCase(8, false, false)]
    [TestCase(8, true, false)]
    [TestCase(16, false, false)]
    [TestCase(16, true, false)]
    [TestCase(8, false, true)]
    [TestCase(8, true, true)]
    [TestCase(16, false, true)]
    [TestCase(16, true, true)]
    public void ExactRegisterExtensionRetainsItsOwnWidthAndSignedness(int width, bool signed, bool nop)
    {
        var body = Body(width, signed, nop);
        var shape = X64EnumIntegerConversionProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Width, Is.EqualTo(width));
            Assert.That(shape.Signed, Is.EqualTo(signed));
            Assert.That(shape.Extension.Op1Register, Is.EqualTo(width == 8 ? Register.CL : Register.CX));
            Assert.That(shape.Extension.Op0Register, Is.EqualTo(Register.EAX));
        });
    }

    [TestCase("wrong-argument")]
    [TestCase("high-byte")]
    [TestCase("wrong-result")]
    [TestCase("narrow-result")]
    [TestCase("wide-result")]
    [TestCase("plain-move")]
    [TestCase("source32")]
    [TestCase("memory")]
    [TestCase("gap")]
    [TestCase("code32")]
    [TestCase("lock")]
    [TestCase("rep")]
    [TestCase("repne")]
    [TestCase("segment")]
    [TestCase("return-adjustment")]
    [TestCase("prefix-call")]
    [TestCase("prefix-branch")]
    [TestCase("prefix-write")]
    public void UnprovedRegisterLanesControlFlowAndEffectsFailClosed(string mutation)
    {
        var body = Body(8, true, mutation.StartsWith("prefix-"));
        var index = body.Length - 2;
        switch (mutation)
        {
            case "wrong-argument": body[index].Op1Register = Register.DL; break;
            case "high-byte": body[index].Op1Register = Register.CH; break;
            case "wrong-result": body[index].Op0Register = Register.EDX; break;
            case "narrow-result": body[index].Code = Code.Movsx_r16_rm8; body[index].Op0Register = Register.AX; break;
            case "wide-result": body[index].Code = Code.Movsx_r64_rm8; body[index].Op0Register = Register.RAX; break;
            case "plain-move": body[index].Code = Code.Mov_r32_rm32; body[index].Op1Register = Register.ECX; break;
            case "source32": body[index].Code = Code.Movsxd_r64_rm32; body[index].Op1Register = Register.ECX; break;
            case "memory": body[index].Op1Kind = OpKind.Memory; body[index].MemoryBase = Register.RCX; break;
            case "gap": body[^1].IP++; break;
            case "code32": body[index].CodeSize = CodeSize.Code32; break;
            case "lock": body[index].HasLockPrefix = true; break;
            case "rep": body[index].HasRepPrefix = true; break;
            case "repne": body[index].HasRepnePrefix = true; break;
            case "segment": body[index].SegmentPrefix = Register.FS; break;
            case "return-adjustment": body[^1].Code = Code.Retnq_imm16; break;
            case "prefix-call": body[0].Code = Code.Call_rel32_64; break;
            case "prefix-branch": body[0].Code = Code.Jmp_rel32_64; break;
            case "prefix-write": body[0] = body[index]; break;
        }
        Assert.That(X64EnumIntegerConversionProof.TryProveShape(body), Is.Null, mutation);
    }

    [Test]
    public void MissingReturnAdditionalExtensionAndAbsentContextDoNotCloseTheLeaf()
    {
        var body = Body(16, false, false);
        Assert.That(X64EnumIntegerConversionProof.TryProveShape(body[..^1]), Is.Null);
        Assert.That(X64EnumIntegerConversionProof.TryProveShape(body.Concat(body).ToArray()), Is.Null);
        Assert.That(X64EnumIntegerConversionProof.Find(null), Is.Null);
    }

    private static Instruction[] Body(int width, bool signed, bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        if (width == 8)
        {
            if (signed) assembler.movsx(eax, cl); else assembler.movzx(eax, cl);
        }
        else if (signed) assembler.movsx(eax, cx); else assembler.movzx(eax, cx);
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
