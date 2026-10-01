using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarDoubleLeafShapeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CompleteFieldSumKeepsBothReadSitesAndTheirOrder(bool prefix)
    {
        var body = FieldSum(prefix);
        var shape = X64ScalarDoubleLeafProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Operation, Is.EqualTo(X64ScalarDoubleLeafProof.Kind.FieldSum));
        Assert.That(shape.First.MemoryDisplacement64, Is.EqualTo(24));
        Assert.That(shape.Arithmetic.MemoryDisplacement64, Is.EqualTo(32));
        Assert.That(shape.First.IP, Is.LessThan(shape.Arithmetic.IP));
        Assert.That(shape.Return, Is.EqualTo(body[^1]));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Signed64ScaleOverwritesTheLowLaneAndDoesNotReadHiddenContext(bool prefix, bool clear)
    {
        var body = Scale(prefix, clear);
        var shape = X64ScalarDoubleLeafProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Operation, Is.EqualTo(X64ScalarDoubleLeafProof.Kind.SignedParameterScale));
        Assert.That(shape.First.Code, Is.EqualTo(Code.Cvtsi2sd_xmm_rm64));
        Assert.That(shape.First.Op1Register, Is.EqualTo(Register.RCX));
        Assert.That(shape.Clear.HasValue, Is.EqualTo(clear));
        Assert.That(shape.Arithmetic.IsIPRelativeMemoryOperand, Is.True);
    }

    [TestCase("different-owner")]
    [TestCase("index")]
    [TestCase("header")]
    [TestCase("misaligned")]
    [TestCase("narrow-read")]
    [TestCase("wrong-return-register")]
    [TestCase("memory-write")]
    [TestCase("extra-effect")]
    [TestCase("locked")]
    [TestCase("discontinuous")]
    [TestCase("adjusted-return")]
    [TestCase("missing-return")]
    public void FieldRecipeRejectsChangedAccessesAndIncompleteOrExtraEffects(string defect)
    {
        var body = FieldSum(false);
        var position = defect is "discontinuous" or "adjusted-return" ? 2 : 0;
        var changed = body[position];
        switch (defect)
        {
            case "different-owner": changed.MemoryBase = Register.RDX; break;
            case "index": changed.MemoryIndex = Register.R8; break;
            case "header": changed.MemoryDisplacement64 = 8; break;
            case "misaligned": changed.MemoryDisplacement64++; break;
            case "narrow-read": changed.Code = Code.Movss_xmm_xmmm32; break;
            case "wrong-return-register": changed.Op0Register = Register.XMM1; break;
            case "memory-write": changed.Code = Code.Movsd_xmmm64_xmm; changed.Op0Kind = OpKind.Memory; break;
            case "locked": changed.HasLockPrefix = true; break;
            case "discontinuous": changed.IP++; break;
            case "adjusted-return": changed.Code = Code.Retnq_imm16; break;
            case "missing-return": body = body[..^1]; break;
            case "extra-effect": body = body.Concat(new[] { body[0] }).ToArray(); break;
        }
        if (defect is not ("missing-return" or "extra-effect")) body[position] = changed;
        Assert.That(X64ScalarDoubleLeafProof.TryProveShape(body), Is.Null);
    }

    [TestCase("narrow-source")]
    [TestCase("argument-slot")]
    [TestCase("memory-source")]
    [TestCase("parameter-coefficient")]
    [TestCase("narrow-multiply")]
    [TestCase("observed-upper-lane")]
    [TestCase("nonself-zero")]
    public void IntegerScaleCannotBroadenSignedWidthOrLaneAndLiteralSemantics(string defect)
    {
        var body = Scale(false, true);
        var position = defect is "parameter-coefficient" or "narrow-multiply" ? 2 : defect == "nonself-zero" ? 0 : 1;
        var changed = body[position];
        switch (defect)
        {
            case "narrow-source": changed.Code = Code.Cvtsi2sd_xmm_rm32; changed.Op1Register = Register.ECX; break;
            case "argument-slot": changed.Op1Register = Register.RDX; break;
            case "memory-source": changed.Op1Kind = OpKind.Memory; changed.MemoryBase = Register.RCX; break;
            case "parameter-coefficient": changed.MemoryBase = Register.RDX; break;
            case "narrow-multiply": changed.Code = Code.Mulss_xmm_xmmm32; break;
            case "nonself-zero": changed.Op1Register = Register.XMM1; break;
            case "observed-upper-lane":
                body = body.Take(3).Append(body[0] with { Code = Code.Shufps_xmm_xmmm128_imm8 }).Append(body[^1]).ToArray(); break;
        }
        if (defect != "observed-upper-lane") body[position] = changed;
        Assert.That(X64ScalarDoubleLeafProof.TryProveShape(body), Is.Null);
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(1983, true)]
    [TestCase(1984, false)]
    [TestCase(2047, false)]
    public void CoefficientDomainBoundsEverySigned64InputWithoutExceptionalResults(int exponent, bool expected)
    {
        foreach (var sign in new[] { 0UL, 1UL << 63 })
        foreach (var mantissa in new[] { 0UL, (1UL << 52) - 1 })
            Assert.That(X64ScalarDoubleLeafProof.HasFiniteNormalScaleDomain(sign | (ulong)exponent << 52 | mantissa),
                Is.EqualTo(expected));
        if (expected)
        {
            var smallest = BitConverter.ToDouble(BitConverter.GetBytes((ulong)exponent << 52), 0);
            var largest = BitConverter.ToDouble(BitConverter.GetBytes((ulong)exponent << 52 | ((1UL << 52) - 1)), 0);
            Assert.That(1d * smallest, Is.GreaterThanOrEqualTo(Math.Pow(2, -1022)));
            Assert.That(double.IsInfinity((double)long.MaxValue * largest), Is.False);
            Assert.That(double.IsNaN((double)long.MinValue * largest), Is.False);
        }
    }

    private static Instruction[] FieldSum(bool prefix)
    {
        var assembler = new Assembler(64);
        if (prefix) assembler.AddInstruction(Instruction.Create(Code.Nopw));
        assembler.movsd(xmm0, __qword_ptr[rcx + 24]);
        assembler.addsd(xmm0, __qword_ptr[rcx + 32]);
        assembler.ret();
        return Decode(assembler);
    }

    private static Instruction[] Scale(bool prefix, bool clear)
    {
        var assembler = new Assembler(64);
        if (prefix) assembler.AddInstruction(Instruction.Create(Code.Nopw));
        if (clear) assembler.xorps(xmm0, xmm0);
        assembler.cvtsi2sd(xmm0, rcx);
        assembler.AddInstruction(Instruction.Create(Code.Mulsd_xmm_xmmm64, Register.XMM0,
            new MemoryOperand(Register.RIP, 0x9000, 8)));
        assembler.ret();
        return Decode(assembler);
    }

    private static Instruction[] Decode(Assembler assembler)
    {
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x4000);
        return X86Utils.Disassemble(stream.ToArray(), 0x4000, false).ToArray();
    }
}
