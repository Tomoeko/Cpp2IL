using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ByRefIntegerSplitProofTests
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public void CompleteSplitRetainsTwoDistinctPointerSlotsAndExactShift(bool instance, bool arithmetic, bool nop)
    {
        var body = Body(instance, arithmetic, nop);
        var shape = X64ByRefIntegerSplitProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.Source, Is.EqualTo(instance ? Register.RDX : Register.RCX));
            Assert.That(shape.LowPointer, Is.EqualTo(instance ? Register.R8 : Register.RDX));
            Assert.That(shape.HighPointer, Is.EqualTo(instance ? Register.R9 : Register.R8));
            Assert.That(shape.Arithmetic, Is.EqualTo(arithmetic));
            Assert.That(shape.LowStore, Is.EqualTo(body[^4]));
            Assert.That(shape.HighStore, Is.EqualTo(body[^2]));
        });
    }

    [TestCase("wide-low-store")]
    [TestCase("wide-high-store")]
    [TestCase("narrow-store")]
    [TestCase("memory-read")]
    [TestCase("pointer-displacement")]
    [TestCase("indexed-pointer")]
    [TestCase("scaled-pointer")]
    [TestCase("stack-pointer")]
    [TestCase("nonvolatile-pointer")]
    [TestCase("source-clobbers-pointer")]
    [TestCase("same-pointer-slot")]
    [TestCase("different-high-value")]
    [TestCase("wrong-shift-value")]
    [TestCase("narrow-shift")]
    [TestCase("count31")]
    [TestCase("count33")]
    [TestCase("count-register")]
    [TestCase("shift-memory")]
    [TestCase("rotate")]
    [TestCase("lock")]
    [TestCase("rep")]
    [TestCase("repne")]
    [TestCase("segment")]
    [TestCase("code32")]
    [TestCase("gap")]
    [TestCase("return-immediate")]
    [TestCase("branch-prefix")]
    [TestCase("call-prefix")]
    [TestCase("store-prefix")]
    public void UnprovedWidthsPointerOriginsControlFlowAndSideEffectsAreRejected(string mutation)
    {
        var body = Body(true, false, mutation.EndsWith("-prefix", StringComparison.Ordinal));
        var low = body.Length - 4;
        var shift = body.Length - 3;
        var high = body.Length - 2;
        switch (mutation)
        {
            case "wide-low-store": body[low].Code = Code.Mov_rm64_r64; body[low].Op1Register = Register.RDX; break;
            case "wide-high-store": body[high].Code = Code.Mov_rm64_r64; body[high].Op1Register = Register.RDX; break;
            case "narrow-store": body[low].Code = Code.Mov_rm16_r16; body[low].Op1Register = Register.DX; break;
            case "memory-read": body[low].Code = Code.Mov_r32_rm32; break;
            case "pointer-displacement": body[high].MemoryDisplacement64 = 4; break;
            case "indexed-pointer": body[low].MemoryIndex = Register.RAX; break;
            case "scaled-pointer": body[low].MemoryIndexScale = 2; break;
            case "stack-pointer": body[low].MemoryBase = Register.RSP; break;
            case "nonvolatile-pointer": body[low].MemoryBase = Register.RBX; break;
            case "source-clobbers-pointer": body[high].MemoryBase = Register.RDX; break;
            case "same-pointer-slot": body[high].MemoryBase = Register.R8; break;
            case "different-high-value": body[high].Op1Register = Register.ECX; break;
            case "wrong-shift-value": body[shift].Op0Register = Register.RCX; break;
            case "narrow-shift": body[shift].Code = Code.Shr_rm32_imm8; body[shift].Op0Register = Register.EDX; break;
            case "count31": body[shift].Immediate8 = 31; break;
            case "count33": body[shift].Immediate8 = 33; break;
            case "count-register": body[shift].Code = Code.Shr_rm64_CL; body[shift].Op1Kind = OpKind.Register; body[shift].Op1Register = Register.CL; break;
            case "shift-memory": body[shift].Op0Kind = OpKind.Memory; break;
            case "rotate": body[shift].Code = Code.Ror_rm64_imm8; break;
            case "lock": body[low].HasLockPrefix = true; break;
            case "rep": body[high].HasRepPrefix = true; break;
            case "repne": body[shift].HasRepnePrefix = true; break;
            case "segment": body[low].SegmentPrefix = Register.FS; break;
            case "code32": body[shift].CodeSize = CodeSize.Code32; break;
            case "gap": body[high].IP++; break;
            case "return-immediate": body[^1].Code = Code.Retnq_imm16; break;
            case "branch-prefix": body[0].Code = Code.Jmp_rel32_64; break;
            case "call-prefix": body[0].Code = Code.Call_rel32_64; break;
            case "store-prefix": body[0] = body[low]; break;
        }
        Assert.That(X64ByRefIntegerSplitProof.TryProveShape(body), Is.Null, mutation);
    }

    [Test]
    public void MissingOrAdditionalStoresDoNotCloseTheWholeLeaf()
    {
        var body = Body(false, false, false);
        Assert.That(X64ByRefIntegerSplitProof.TryProveShape(body[..^1]), Is.Null);
        Assert.That(X64ByRefIntegerSplitProof.TryProveShape(body.Concat(body).ToArray()), Is.Null);
        Assert.That(X64ByRefIntegerSplitProof.Find(null), Is.Null);
    }

    private static Instruction[] Body(bool instance, bool arithmetic, bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        if (instance)
        {
            assembler.mov(__dword_ptr[r8], edx);
            if (arithmetic) assembler.sar(rdx, 32); else assembler.shr(rdx, 32);
            assembler.mov(__dword_ptr[r9], edx);
        }
        else
        {
            assembler.mov(__dword_ptr[rdx], ecx);
            if (arithmetic) assembler.sar(rcx, 32); else assembler.shr(rcx, 32);
            assembler.mov(__dword_ptr[r8], ecx);
        }
        assembler.ret();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
