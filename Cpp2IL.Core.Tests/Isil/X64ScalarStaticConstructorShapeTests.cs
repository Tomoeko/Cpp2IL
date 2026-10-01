using System.IO;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarStaticConstructorShapeTests
{
    [TestCase(0)]
    [TestCase(17)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void CompleteWordStoreRetainsTheExactSignedBitPattern(int value)
    {
        var body = Body(value);
        var shape = X64ScalarWrapperStaticConstructorProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Value.Width, Is.EqualTo(4));
        Assert.That(shape.Value.ValueBits, Is.EqualTo(unchecked((uint)value)));
        Assert.That(body[2].NearBranchTarget, Is.EqualTo(body[6].IP));
    }

    [TestCase("wrong-guard")]
    [TestCase("wrong-flag")]
    [TestCase("wrong-slot")]
    [TestCase("wrong-helper-argument")]
    [TestCase("wrong-class-storage")]
    [TestCase("wrong-store-owner")]
    [TestCase("wrong-field-offset")]
    [TestCase("wrong-width")]
    [TestCase("missing-return")]
    [TestCase("extra-store")]
    public void GuardStorageAndAllEffectsMustMatchTheClosedRecipe(string defect)
    {
        var body = Body(-1);
        var position = defect switch
        {
            "wrong-guard" => 2,
            "wrong-flag" => 5,
            "wrong-slot" => 6,
            "wrong-helper-argument" => 3,
            "wrong-class-storage" => 7,
            _ => 8,
        };
        var site = body[position];
        switch (defect)
        {
            case "wrong-guard": site.NearBranch64 = body[8].IP; break;
            case "wrong-flag": site.MemoryDisplacement64++; break;
            case "wrong-slot": site.MemoryDisplacement64 += 8; break;
            case "wrong-helper-argument": site.Op0Register = Register.RDX; break;
            case "wrong-class-storage": site.MemoryDisplacement64 += 8; break;
            case "wrong-store-owner": site.MemoryBase = Register.RAX; break;
            case "wrong-field-offset": site.MemoryDisplacement64 = 4; break;
            case "wrong-width": site.Code = Code.Mov_rm64_imm32; break;
            case "missing-return": body = body[..^1]; break;
            case "extra-store": body = body.Concat(new[] { body[8] }).ToArray(); break;
        }
        if (defect is not ("missing-return" or "extra-store")) body[position] = site;
        Assert.That(X64ScalarWrapperStaticConstructorProof.TryProveShape(body), Is.Null);
    }

    private static Instruction[] Body(int value)
    {
        var assembler = new Assembler(64);
        var flag = assembler.CreateLabel();
        var slot = assembler.CreateLabel();
        var helper = assembler.CreateLabel();
        var initialized = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.cmp(__byte_ptr[flag], 0);
        assembler.jne(initialized);
        assembler.lea(rcx, __qword_ptr[slot]);
        assembler.call(helper);
        assembler.mov(__byte_ptr[flag], 1);
        assembler.Label(ref initialized);
        assembler.mov(rax, __qword_ptr[slot]);
        assembler.mov(rcx, __qword_ptr[rax + checked((int)Il2CppClassLayout.StaticFieldsOffset64)]);
        assembler.mov(__dword_ptr[rcx], value);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref helper); assembler.ret();
        assembler.Label(ref flag); assembler.db(0);
        assembler.Label(ref slot); assembler.db(new byte[8]);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x4000);
        return X86Utils.Disassemble(stream.ToArray(), 0x4000, false).Take(11).ToArray();
    }
}
