using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64FixedBooleanConjunctionShapeTests
{
    [TestCase(false, 0, 0)]
    [TestCase(true, 0, 0)]
    [TestCase(false, 1, 1)]
    [TestCase(true, 1, 1)]
    [TestCase(false, 0, 127)]
    [TestCase(true, 127, 1)]
    public void BranchPlacementPreservesBothConstantIndices(bool falseReturnLast,
        int firstIndex, int secondIndex)
    {
        var shape = X64FixedBooleanConjunctionProof.TryProveShape(
            Body(falseReturnLast, firstIndex, secondIndex), 8);
        Assert.That(shape, Is.EqualTo(new X64FixedBooleanConjunctionProof.Shape(
            16, firstIndex, 24, secondIndex)));
    }

    [TestCase(-1)]
    [TestCase(128)]
    public void SignExtendedOrWideConstantsNeedSeparateEvidence(int index)
    {
        Assert.That(X64FixedBooleanConjunctionProof.TryProveShape(Body(true, index, 0), 8),
            Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReorderedReadsAndAlteredGuardsCannotProveTheSameConjunction(bool falseReturnLast)
    {
        var original = Body(falseReturnLast, 0, 1);
        var second = falseReturnLast ? 8 : 11;
        var earlyFalse = falseReturnLast ? 17 : 8;
        foreach (var mutation in new[]
                 {
                     "opposite-first-predicate", "first-predicate-enters-middle",
                     "first-null-to-bounds", "first-signed-bounds", "first-index-address",
                     "second-field-alias", "second-null-to-bounds", "second-signed-bounds",
                     "second-index-address", "second-element-width", "second-predicate",
                     "false-return-value", "second-stack-restoration", "nonzero-byte-test",
                     "missing-null-trap", "discontinuous-native-body", "extra-instruction"
                 })
        {
            var changed = original.ToArray();
            switch (mutation)
            {
                case "opposite-first-predicate":
                    changed[7].Code = falseReturnLast ? Code.Je_rel8_64 : Code.Jne_rel8_64;
                    break;
                case "first-predicate-enters-middle": changed[7].NearBranch64 = changed[second + 1].IP; break;
                case "first-null-to-bounds": changed[3].NearBranch64 = changed[22].IP; break;
                case "first-signed-bounds": changed[5].Code = Code.Jle_rel8_64; break;
                case "first-index-address": changed[6].MemoryDisplacement64++; break;
                case "second-field-alias": changed[second].MemoryDisplacement64 = 16; break;
                case "second-null-to-bounds": changed[second + 2].NearBranch64 = changed[22].IP; break;
                case "second-signed-bounds": changed[second + 4].Code = Code.Jle_rel8_64; break;
                case "second-index-address": changed[second + 3].Immediate8 = 0; break;
                case "second-element-width": changed[second + 5].Code = Code.Cmp_rm32_imm8; break;
                case "second-predicate": changed[second + 6].Code = Code.Setne_rm8; break;
                case "false-return-value": changed[earlyFalse].Code = Code.Or_r8_rm8; break;
                case "second-stack-restoration": changed[second + 7].Immediate8 = 0x20; break;
                case "nonzero-byte-test": changed[6].Immediate8 = 1; break;
                case "missing-null-trap": changed[21].Code = Code.Nopd; break;
                case "discontinuous-native-body": changed[second].IP++; break;
                case "extra-instruction": changed = [..changed, changed[^1]]; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64FixedBooleanConjunctionProof.TryProveShape(changed, 8),
                Is.Null, mutation);
        }
    }

    [Test]
    public void X64ShapeCannotEstablishA32BitArrayLayout()
    {
        Assert.That(X64FixedBooleanConjunctionProof.TryProveShape(Body(true, 0, 1), 4),
            Is.Null);
    }

    private static Instruction[] Body(bool falseReturnLast, int firstIndex, int secondIndex)
    {
        var assembler = new Assembler(64);
        var secondRead = assembler.CreateLabel();
        var falseReturn = assembler.CreateLabel();
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(rax, __qword_ptr[rcx + 16]);
        assembler.test(rax, rax);
        assembler.je(nullExit);
        assembler.cmp(__dword_ptr[rax + 24], firstIndex);
        assembler.jbe(boundsExit);
        assembler.cmp(__byte_ptr[rax + 32 + firstIndex], 0);
        if (falseReturnLast)
            assembler.jne(falseReturn);
        else
        {
            assembler.je(secondRead);
            EmitFalseReturn();
        }
        assembler.Label(ref secondRead);
        assembler.mov(rax, __qword_ptr[rcx + 24]);
        assembler.test(rax, rax);
        assembler.je(nullExit);
        assembler.cmp(__dword_ptr[rax + 24], secondIndex);
        assembler.jbe(boundsExit);
        assembler.cmp(__byte_ptr[rax + 32 + secondIndex], 0);
        assembler.sete(al);
        assembler.add(rsp, 0x28);
        assembler.ret();
        if (falseReturnLast)
        {
            assembler.Label(ref falseReturn);
            EmitFalseReturn();
        }
        assembler.Label(ref nullExit);
        assembler.call(0x2000UL);
        assembler.int3();
        assembler.Label(ref boundsExit);
        assembler.call(0x3000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Disassemble(stream.ToArray(), 0x1000, false).ToArray();

        void EmitFalseReturn()
        {
            assembler.xor(al, al);
            assembler.add(rsp, 0x28);
            assembler.ret();
        }
    }
}
