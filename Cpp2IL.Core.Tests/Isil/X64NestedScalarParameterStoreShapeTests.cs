using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NestedScalarParameterStoreShapeTests
{
    [TestCase("boolean", 8, false)]
    [TestCase("word", 32, false)]
    [TestCase("single", 32, true)]
    public void ScalarWidthAndArgumentSlotComeFromTheCompleteStore(string kind, int width, bool floating)
    {
        var shape = X64NestedScalarParameterStoreProof.TryProveShape(Body(kind));
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Width, Is.EqualTo(width));
            Assert.That(shape.Floating, Is.EqualTo(floating));
            Assert.That(shape.SourceOffset, Is.EqualTo(24));
            Assert.That(shape.ValueOffset, Is.EqualTo(36));
        });
    }

    [TestCase("word-owner-load")]
    [TestCase("parameter-as-owner")]
    [TestCase("indexed-owner")]
    [TestCase("owner-header")]
    [TestCase("narrow-null-test")]
    [TestCase("wrong-null-register")]
    [TestCase("inverted-guard")]
    [TestCase("guard-to-store")]
    [TestCase("boolean-wrong-argument")]
    [TestCase("word-wide-store")]
    [TestCase("single-wrong-slot")]
    [TestCase("integer-as-single")]
    [TestCase("destination-owner")]
    [TestCase("indexed-store")]
    [TestCase("store-header")]
    [TestCase("locked-store")]
    [TestCase("segmented-store")]
    [TestCase("stack-restore")]
    [TestCase("adjusted-return")]
    [TestCase("tail-helper")]
    [TestCase("missing-helper")]
    [TestCase("discontinuous")]
    [TestCase("extra-effect")]
    public void AChangedGuardWidthArgumentOrEffectCannotMatch(string defect)
    {
        var body = Body(defect.StartsWith("boolean", StringComparison.Ordinal) ? "boolean" :
            defect.StartsWith("single", StringComparison.Ordinal) || defect == "integer-as-single" ? "single" : "word");
        var ordinal = defect.Contains("owner") ? 1 : defect.Contains("guard") ? 3 :
            defect.Contains("null") ? 2 : defect == "stack-restore" ? 5 :
            defect == "adjusted-return" ? 6 : defect.Contains("helper") ? 7 : 4;
        if (defect == "destination-owner") ordinal = 4;
        var instruction = body[ordinal];
        switch (defect)
        {
            case "word-owner-load": instruction.Code = Code.Mov_r32_rm32; instruction.Op0Register = Register.EAX; break;
            case "parameter-as-owner": instruction.MemoryBase = Register.RDX; break;
            case "indexed-owner": case "indexed-store": instruction.MemoryIndex = Register.R8; break;
            case "owner-header": case "store-header": instruction.MemoryDisplacement64 = 8; break;
            case "narrow-null-test": instruction.Code = Code.Test_rm32_r32; instruction.Op0Register = Register.EAX; instruction.Op1Register = Register.EAX; break;
            case "wrong-null-register": instruction.Op1Register = Register.RCX; break;
            case "inverted-guard": instruction.Code = Code.Jne_rel8_64; break;
            case "guard-to-store": instruction.NearBranch64 = body[4].IP; break;
            case "boolean-wrong-argument": instruction.Op1Register = Register.CL; break;
            case "word-wide-store": instruction.Code = Code.Mov_rm64_r64; instruction.Op1Register = Register.RDX; break;
            case "single-wrong-slot": instruction.Op1Register = Register.XMM0; break;
            case "integer-as-single": instruction.Op1Register = Register.EDX; break;
            case "destination-owner": instruction.MemoryBase = Register.RCX; break;
            case "locked-store": instruction.HasLockPrefix = true; break;
            case "segmented-store": instruction.SegmentPrefix = Register.FS; break;
            case "stack-restore": instruction.Immediate8 = 32; break;
            case "adjusted-return": instruction.Code = Code.Retnq_imm16; break;
            case "tail-helper": instruction.Code = Code.Jmp_rel32_64; break;
            case "missing-helper": instruction.NearBranch64 = 0; break;
            case "discontinuous": instruction.IP++; break;
            case "extra-effect": body = body.Concat(new[] { body[4] }).ToArray(); break;
        }
        if (defect != "extra-effect") body[ordinal] = instruction;
        Assert.That(X64NestedScalarParameterStoreProof.TryProveShape(body), Is.Null, defect);
    }

    private static Instruction[] Body(string kind)
    {
        var assembler = new Assembler(64);
        var nullTarget = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(rax, __qword_ptr[rcx + 24]);
        assembler.test(rax, rax);
        assembler.je(nullTarget);
        if (kind == "boolean") assembler.mov(__byte_ptr[rax + 36], dl);
        else if (kind == "single") assembler.movss(__dword_ptr[rax + 36], xmm1);
        else assembler.mov(__dword_ptr[rax + 36], edx);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref nullTarget);
        assembler.call(0x3000);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x2000);
        return X86Utils.Disassemble(stream.ToArray(), 0x2000, false).ToArray();
    }
}
