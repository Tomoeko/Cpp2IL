using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarLaneDemandShapeTests
{
    private static readonly Code[] Copies =
    [
        Code.Movaps_xmm_xmmm128, Code.Movaps_xmmm128_xmm,
        Code.Movups_xmm_xmmm128, Code.Movups_xmmm128_xmm,
        Code.Movapd_xmm_xmmm128, Code.Movapd_xmmm128_xmm,
        Code.Movupd_xmm_xmmm128, Code.Movupd_xmmm128_xmm
    ];

    [TestCase(32)]
    [TestCase(64)]
    public void BothCopyEncodingsCarryOnlyTheDemandedScalarLane(int width)
    {
        foreach (var encoding in Copies)
        {
            var body = Arithmetic(width, encoding);
            Assert.That(body[0].Code, Is.EqualTo(encoding), "Assemble and decode the actual encoding.");
            var shape = X64ScalarLaneDemandProof.TryProveShape(body, Inputs(width), width);
            Assert.That(shape, Is.Not.Null, encoding.ToString());
            Assert.That(shape!.Projections.Length, Is.EqualTo(2));
            Assert.That(shape.Projections.ToArray().All(site => site.Width == width && !site.IsZero), Is.True);
        }
    }

    [TestCase(32, Code.Xorps_xmm_xmmm128)]
    [TestCase(64, Code.Xorps_xmm_xmmm128)]
    [TestCase(32, Code.Xorpd_xmm_xmmm128)]
    [TestCase(64, Code.Xorpd_xmm_xmmm128)]
    public void SelfZeroDefinesPositiveZeroWithoutReadingAnIncomingValue(int width, Code encoding)
    {
        var body = Decode(
            Instruction.Create(encoding, Register.XMM4, Register.XMM4),
            Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM0, Register.XMM4),
            Instruction.Create(Code.Retnq));
        var shape = X64ScalarLaneDemandProof.TryProveShape(body, new Dictionary<Register, int>(), width);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Projections[0].IsZero, Is.True);
        Assert.That(shape.Projections[0].Source, Is.EqualTo(Register.None));
    }

    [Test]
    public void UnusedParametersDoNotChangeTheObservedLaneWidth()
    {
        var body = Decode(Instruction.Create(Code.Movapd_xmmm128_xmm, Register.XMM0, Register.XMM1),
            Instruction.Create(Code.Retnq));
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body,
            new Dictionary<Register, int> { [Register.XMM0] = 32, [Register.XMM1] = 64 }, 64), Is.Not.Null);
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body,
            new Dictionary<Register, int> { [Register.XMM0] = 32, [Register.XMM1] = 32 }, 64), Is.Null);
    }

    [TestCase(Code.Movss_xmm_xmmm32, 32)]
    [TestCase(Code.Movss_xmmm32_xmm, 32)]
    [TestCase(Code.Movsd_xmm_xmmm64, 64)]
    [TestCase(Code.Movsd_xmmm64_xmm, 64)]
    public void ScalarRegisterCopyEncodingsKeepTheDeclaredPrecision(Code encoding, int width)
    {
        var body = Decode(Instruction.Create(encoding, Register.XMM4, Register.XMM1),
            Instruction.Create(Code.Movups_xmm_xmmm128, Register.XMM0, Register.XMM4),
            Instruction.Create(Code.Retnq));
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, Inputs(width), width), Is.Not.Null);
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, Inputs(width == 32 ? 64 : 32), width), Is.Null);
    }

    [TestCase("undefined-source")]
    [TestCase("incoming-width")]
    [TestCase("return-width")]
    [TestCase("nonvolatile-register")]
    [TestCase("packed-load")]
    [TestCase("packed-store")]
    [TestCase("packed-arithmetic")]
    [TestCase("lane-observer")]
    [TestCase("distinct-xor")]
    [TestCase("dead-arithmetic")]
    [TestCase("environment")]
    [TestCase("call")]
    [TestCase("branch")]
    [TestCase("prefix")]
    [TestCase("discontinuous")]
    [TestCase("trailing-effect")]
    public void WidthLivenessAndCompleteBodyAreRequired(string defect)
    {
        var body = Arithmetic(32, Code.Movaps_xmm_xmmm128);
        var incoming = Inputs(32);
        var returnWidth = 32;
        var changed = body[0];
        switch (defect)
        {
            case "undefined-source": changed.Op1Register = Register.XMM5; break;
            case "incoming-width": incoming[Register.XMM0] = 64; break;
            case "return-width": returnWidth = 64; break;
            case "nonvolatile-register": changed.Op0Register = Register.XMM6; break;
            case "packed-load": changed.Op1Kind = OpKind.Memory; changed.MemoryBase = Register.RAX; break;
            case "packed-store": changed.Code = Code.Movaps_xmmm128_xmm; changed.Op0Kind = OpKind.Memory; changed.MemoryBase = Register.RAX; break;
            case "packed-arithmetic": changed.Code = Code.Addps_xmm_xmmm128; break;
            case "lane-observer": changed.Code = Code.Unpcklps_xmm_xmmm128; break;
            case "distinct-xor": changed.Code = Code.Xorps_xmm_xmmm128; break;
            case "dead-arithmetic": body = Decode(
                Instruction.Create(Code.Addss_xmm_xmmm32, Register.XMM3, Register.XMM2),
                Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM4, Register.XMM0),
                Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM0, Register.XMM4),
                Instruction.Create(Code.Retnq)); break;
            case "environment": body = Decode(
                Instruction.Create(Code.Ldmxcsr_m32, new MemoryOperand(Register.RAX)),
                Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM4, Register.XMM0),
                Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM0, Register.XMM4),
                Instruction.Create(Code.Retnq)); break;
            case "call": changed.Code = Code.Call_rel32_64; break;
            case "branch": changed.Code = Code.Jmp_rel32_64; break;
            case "prefix": changed.HasLockPrefix = true; break;
            case "discontinuous": changed.IP++; break;
            case "trailing-effect": body = body.Concat(new[] { body[0] }).ToArray(); break;
        }
        if (defect is not ("dead-arithmetic" or "trailing-effect" or "environment")) body[0] = changed;
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, incoming, returnWidth), Is.Null, defect);
    }

    [Test]
    public void OnlyActualScalarMemoryWidthsReachLiteralAuthentication()
    {
        var body = Decode(
            Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM4, Register.XMM0),
            Instruction.Create(Code.Mulss_xmm_xmmm32, Register.XMM4, new MemoryOperand(Register.RAX)),
            Instruction.Create(Code.Movaps_xmm_xmmm128, Register.XMM0, Register.XMM4),
            Instruction.Create(Code.Retnq));
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, Inputs(32), 32), Is.Null);
        var calls = 0;
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, Inputs(32), 32, (site, width) =>
        {
            calls++;
            Assert.That(site.MemorySize.GetSize(), Is.EqualTo(4));
            Assert.That(width, Is.EqualTo(32));
            return true;
        }), Is.Not.Null);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(X64ScalarLaneDemandProof.TryProveShape(body, Inputs(32), 32, (_, _) => false), Is.Null);
    }

    private static Dictionary<Register, int> Inputs(int width) =>
        new() { [Register.XMM0] = width, [Register.XMM1] = width };

    private static Instruction[] Arithmetic(int width, Code encoding) => Decode(
        Instruction.Create(encoding, Register.XMM4, Register.XMM0),
        Instruction.Create(width == 32 ? Code.Mulss_xmm_xmmm32 : Code.Mulsd_xmm_xmmm64, Register.XMM4, Register.XMM1),
        Instruction.Create(encoding, Register.XMM0, Register.XMM4), Instruction.Create(Code.Retnq));

    private static Instruction[] Decode(params Instruction[] instructions)
    {
        var assembler = new Assembler(64);
        foreach (var instruction in instructions) assembler.AddInstruction(instruction);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x3000);
        return X86Utils.Disassemble(stream.ToArray(), 0x3000, false).ToArray();
    }
}
