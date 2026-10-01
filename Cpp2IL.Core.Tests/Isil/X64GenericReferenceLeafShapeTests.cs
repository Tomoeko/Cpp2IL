using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64GenericReferenceLeafShapeTests
{
    [TestCase(16, Code.Mov_r64_rm64)]
    [TestCase(36, Code.Mov_r64_rm64)]
    [TestCase(4092, Code.Mov_r64_rm64)]
    [TestCase(16, Code.Mov_rm64_r64)]
    [TestCase(36, Code.Mov_rm64_r64)]
    [TestCase(4092, Code.Mov_rm64_r64)]
    public void ScalarOffsetIsDerivedFromTheCompleteReferenceReturnLeaf(int offset, Code copyEncoding)
    {
        var body = Body(offset, copyEncoding);
        Assert.That(body[1].Code, Is.EqualTo(copyEncoding), "Both native MOV encodings must be assembled and decoded.");
        var shape = X64GenericReferenceLeafShape.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.CounterOffset, Is.EqualTo(offset));
        Assert.That(shape.Start, Is.EqualTo(body[0].IP));
        Assert.That(shape.End, Is.EqualTo(body[^1].NextIP));
    }

    [TestCase("wide-increment")]
    [TestCase("argument-as-owner")]
    [TestCase("indexed-field")]
    [TestCase("locked-increment")]
    [TestCase("unaligned-field")]
    [TestCase("object-header")]
    [TestCase("owner-return")]
    [TestCase("hidden-context-return")]
    [TestCase("narrow-return")]
    [TestCase("memory-return")]
    [TestCase("memory-destination")]
    [TestCase("adjusted-return")]
    [TestCase("discontinuous")]
    [TestCase("extra-effect")]
    public void SimilarInstructionsCannotReplaceTheRequiredAbiOrEffects(string defect)
    {
        var body = Body(36);
        var ordinal = defect.Contains("return") || defect == "memory-destination" ? 1 : 0;
        if (defect is "adjusted-return" or "discontinuous") ordinal = 2;
        var instruction = body[ordinal];
        switch (defect)
        {
            case "wide-increment": instruction.Code = Code.Inc_rm64; break;
            case "argument-as-owner": instruction.MemoryBase = Register.RDX; break;
            case "indexed-field": instruction.MemoryIndex = Register.R8; break;
            case "locked-increment": instruction.HasLockPrefix = true; break;
            case "unaligned-field": instruction.MemoryDisplacement64++; break;
            case "object-header": instruction.MemoryDisplacement64 = 8; break;
            case "owner-return": instruction.Op1Register = Register.RCX; break;
            case "hidden-context-return": instruction.Op1Register = Register.R8; break;
            case "narrow-return": instruction.Code = Code.Mov_r32_rm32; break;
            case "memory-return": instruction.Code = Code.Mov_r64_rm64; instruction.Op1Kind = OpKind.Memory; instruction.MemoryBase = Register.RDX; break;
            case "memory-destination": instruction.Op0Kind = OpKind.Memory; instruction.MemoryBase = Register.RCX; break;
            case "adjusted-return": instruction.Code = Code.Retnq_imm16; break;
            case "discontinuous": instruction.IP++; break;
            case "extra-effect": body = body.Concat(new[] { body[0] }).ToArray(); break;
        }
        if (defect != "extra-effect") body[ordinal] = instruction;
        Assert.That(X64GenericReferenceLeafShape.TryProveShape(body), Is.Null, defect);
    }

    private static Instruction[] Body(int offset, Code copyEncoding = Code.Mov_rm64_r64)
    {
        var assembler = new Assembler(64);
        assembler.inc(__dword_ptr[rcx + offset]);
        assembler.AddInstruction(Instruction.Create(copyEncoding, Register.RAX, Register.RDX));
        assembler.ret();
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x2000);
        return X86Utils.Disassemble(stream.ToArray(), 0x2000, false).ToArray();
    }
}
