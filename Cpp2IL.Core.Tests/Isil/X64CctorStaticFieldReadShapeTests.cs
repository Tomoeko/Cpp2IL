using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64CctorStaticFieldReadShapeTests
{
    [TestCase(0x2000UL, 0)]
    [TestCase(0x2000UL, 8)]
    [TestCase(0x190000000UL, 24)]
    public void RelocatedGuardsRetainBothHelperCallsAndTheReloadedClass(ulong address, int offset)
    {
        var body = Body(address, offset);
        var shape = X64CctorStaticFieldReadProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.OnceFlag, Is.EqualTo(body[1].IPRelativeMemoryAddress));
            Assert.That(shape.TypeInfoSlot, Is.EqualTo(body[3].IPRelativeMemoryAddress));
            Assert.That(shape.MetadataInitializer, Is.EqualTo(address + 0x1000));
            Assert.That(shape.ClassInitializer, Is.EqualTo(address + 0x2000));
            Assert.That(shape.FieldOffset, Is.EqualTo(offset));
        });
    }

    [TestCase("flag-width", 1)]
    [TestCase("flag-value", 1)]
    [TestCase("metadata-inverted", 2)]
    [TestCase("metadata-skip-class", 2)]
    [TestCase("type-address-register", 3)]
    [TestCase("type-address-index", 3)]
    [TestCase("metadata-tailcall", 4)]
    [TestCase("metadata-zero-target", 4)]
    [TestCase("flag-store-slot", 5)]
    [TestCase("flag-store-value", 5)]
    [TestCase("truncated-type", 6)]
    [TestCase("type-load-slot", 6)]
    [TestCase("class-check-width", 7)]
    [TestCase("class-check-offset", 7)]
    [TestCase("class-check-value", 7)]
    [TestCase("class-inverted", 8)]
    [TestCase("class-skip-storage", 8)]
    [TestCase("class-argument", 9)]
    [TestCase("class-tailcall", 10)]
    [TestCase("class-zero-target", 10)]
    [TestCase("class-reload-slot", 11)]
    [TestCase("class-reload-register", 11)]
    [TestCase("storage-offset", 12)]
    [TestCase("storage-register", 12)]
    [TestCase("field-width", 13)]
    [TestCase("field-index", 13)]
    [TestCase("field-negative-offset", 13)]
    [TestCase("field-locked", 13)]
    [TestCase("field-segment", 13)]
    [TestCase("wrong-frame", 14)]
    [TestCase("return-adjustment", 15)]
    [TestCase("instruction-gap", 11)]
    [TestCase("extra-effect", 15)]
    public void EveryGuardWidthOrderAliasAndReachableEffectMustRemainExact(string defect, int ordinal)
    {
        var body = Body(0x2000, 8);
        var instruction = body[ordinal];
        switch (defect)
        {
            case "flag-width": instruction.Code = Code.Cmp_rm32_imm8; break;
            case "flag-value": case "class-check-value": instruction.Immediate8 = 1; break;
            case "metadata-inverted": case "class-inverted": instruction.Code = Code.Je_rel8_64; break;
            case "metadata-skip-class": instruction.NearBranch64 = body[12].IP; break;
            case "type-address-register": instruction.Op0Register = Register.RDX; break;
            case "type-address-index": case "field-index": instruction.MemoryIndex = Register.RDX; break;
            case "metadata-tailcall": case "class-tailcall": instruction.Code = Code.Jmp_rel32_64; break;
            case "metadata-zero-target": case "class-zero-target": instruction.NearBranch64 = 0; break;
            case "flag-store-slot": case "type-load-slot": case "class-reload-slot": instruction.MemoryDisplacement64++; break;
            case "flag-store-value": instruction.Immediate8 = 0; break;
            case "truncated-type": case "field-width": instruction.Code = Code.Mov_r32_rm32; instruction.Op0Register = Register.EAX; break;
            case "class-check-width": instruction.Code = Code.Cmp_rm8_imm8; break;
            case "class-check-offset": case "storage-offset": instruction.MemoryDisplacement64 += 8; break;
            case "class-skip-storage": instruction.NearBranch64 = body[13].IP; break;
            case "class-argument": instruction.Op1Register = Register.RDX; break;
            case "class-reload-register": instruction.Op0Register = Register.RCX; break;
            case "storage-register": instruction.MemoryBase = Register.RCX; break;
            case "field-negative-offset": instruction.MemoryDisplacement64 = ulong.MaxValue; break;
            case "field-locked": instruction.HasLockPrefix = true; break;
            case "field-segment": instruction.SegmentPrefix = Register.FS; break;
            case "wrong-frame": instruction.Immediate8 = 0x20; break;
            case "return-adjustment": instruction.Code = Code.Retnq_imm16; break;
            case "instruction-gap": instruction.IP++; break;
            case "extra-effect": body = body.Concat(new[] { body[13] }).ToArray(); break;
        }
        if (defect != "extra-effect") body[ordinal] = instruction;
        Assert.That(X64CctorStaticFieldReadProof.TryProveShape(body), Is.Null, defect);
    }

    private static Instruction[] Body(ulong address, int offset)
    {
        var assembler = new Assembler(64);
        var initializedMetadata = assembler.CreateLabel();
        var initializedClass = assembler.CreateLabel();
        var flag = assembler.CreateLabel();
        var type = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.cmp(__byte_ptr[flag], 0);
        assembler.jne(initializedMetadata);
        assembler.lea(rcx, __qword_ptr[type]);
        assembler.call(address + 0x1000);
        assembler.mov(__byte_ptr[flag], 1);
        assembler.Label(ref initializedMetadata);
        assembler.mov(rax, __qword_ptr[type]);
        assembler.cmp(__dword_ptr[rax + checked((int)Il2CppClassLayout.CctorFinishedOrNoCctorOffset64)], 0);
        assembler.jne(initializedClass);
        assembler.mov(rcx, rax);
        assembler.call(address + 0x2000);
        assembler.mov(rax, __qword_ptr[type]);
        assembler.Label(ref initializedClass);
        assembler.mov(rax, __qword_ptr[rax + checked((int)Il2CppClassLayout.StaticFieldsOffset64)]);
        assembler.mov(rax, __qword_ptr[rax + offset]);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref flag);
        assembler.db(0);
        assembler.Label(ref type);
        assembler.db(new byte[8]);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), address);
        return X86Utils.Disassemble(stream.ToArray(), address, false).Take(16).ToArray();
    }
}
