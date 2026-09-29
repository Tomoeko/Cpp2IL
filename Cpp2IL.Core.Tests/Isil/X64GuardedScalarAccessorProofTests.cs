using System.IO;
using System.Collections.Generic;
using LibCpp2IL.BinaryStructures;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64GuardedScalarAccessorProofTests
{
    [TestCase(8, false)]
    [TestCase(8, true)]
    [TestCase(32, false)]
    [TestCase(32, true)]
    public void TheAccessibleGetterIsACompleteTypedFieldLeaf(int width, bool nop)
    {
        var body = Getter(width, nop);
        var shape = X64GuardedScalarAccessorProof.TryProveGetterShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Width, Is.EqualTo(width));
        Assert.That(shape.Offset, Is.EqualTo(24));
        foreach (var mutation in new[] { "receiver", "index", "header", "width", "result", "segment", "rep",
                     "repne", "lock", "code32", "gap", "ret-pop", "missing-ret", "extra-effect" })
        {
            var changed = Getter(width, nop);
            var load = changed.Length - 2;
            switch (mutation)
            {
                case "receiver": changed[load].MemoryBase = Register.RDX; break;
                case "index": changed[load].MemoryIndex = Register.RCX; break;
                case "header": changed[load].MemoryDisplacement64 = 8; break;
                case "width": changed[load].Code = Code.Mov_r64_rm64; break;
                case "result": changed[load].Op0Register = Register.EDX; break;
                case "segment": changed[load].SegmentPrefix = Register.FS; break;
                case "rep": changed[load].HasRepPrefix = true; break;
                case "repne": changed[load].HasRepnePrefix = true; break;
                case "lock": changed[load].HasLockPrefix = true; break;
                case "code32": changed[load].CodeSize = CodeSize.Code32; break;
                case "gap": changed[^1].IP++; break;
                case "ret-pop": changed[^1].Code = Code.Retnq_imm16; break;
                case "missing-ret": changed = changed[..^1]; break;
                case "extra-effect": changed = Getter(width, true); changed[0].Code = Code.Inc_rm32; break;
            }
            Assert.That(X64GuardedScalarAccessorProof.TryProveGetterShape(changed), Is.Null, mutation);
        }
    }

    [TestCase(8, false)]
    [TestCase(8, true)]
    [TestCase(32, false)]
    [TestCase(32, true)]
    public void TheCallerReadsOneReferenceBeforeOneScalarAndAnAuthenticatedNullTerminal(int width, bool padding)
    {
        var body = Caller(width, padding);
        var shape = X64GuardedScalarAccessorProof.TryProveCallerShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.That(shape!.Width, Is.EqualTo(width));
        Assert.That(shape.SourceOffset, Is.EqualTo(16));
        Assert.That(shape.ValueOffset, Is.EqualTo(24));
        Assert.That(shape.NullCall.NearBranchTarget, Is.EqualTo(0x4000));
        foreach (var mutation in new[] { "stack", "capture-source", "capture-result", "capture-index", "capture-width",
                     "capture-header", "tested-source", "tested-width", "branch-condition", "branch-target",
                     "read-source", "read-index", "read-width", "read-result", "restore", "ret-pop", "helper",
                     "indirect-helper", "segment", "rep", "gap", "missing-call", "extra-effect", "excess-padding" })
        {
            var changed = Caller(width, padding);
            switch (mutation)
            {
                case "stack": changed[0].Immediate8 = 0x20; break;
                case "capture-source": changed[1].MemoryBase = Register.RDX; break;
                case "capture-result": changed[1].Op0Register = Register.RDX; break;
                case "capture-index": changed[1].MemoryIndex = Register.RDX; break;
                case "capture-width": changed[1].Code = Code.Mov_r32_rm32; break;
                case "capture-header": changed[1].MemoryDisplacement64 = 8; break;
                case "tested-source": changed[2].Op1Register = Register.RCX; break;
                case "tested-width": changed[2].Code = Code.Test_rm32_r32; break;
                case "branch-condition": changed[3].Code = Code.Jne_rel8_64; break;
                case "branch-target": changed[3].NearBranch64 = changed[6].IP; break;
                case "read-source": changed[4].MemoryBase = Register.RCX; break;
                case "read-index": changed[4].MemoryIndex = Register.RDX; break;
                case "read-width": changed[4].Code = Code.Mov_r64_rm64; break;
                case "read-result": changed[4].Op0Register = Register.EDX; break;
                case "restore": changed[5].Immediate8 = 0x20; break;
                case "ret-pop": changed[6].Code = Code.Retnq_imm16; break;
                case "helper": changed[7].NearBranch64 = 0; break;
                case "indirect-helper": changed[7].Code = Code.Call_rm64; break;
                case "segment": changed[4].SegmentPrefix = Register.FS; break;
                case "rep": changed[4].HasRepPrefix = true; break;
                case "gap": changed[4].IP++; break;
                case "missing-call": changed = changed[..7]; break;
                case "extra-effect": changed = Caller(width, true); changed[8].Code = Code.Inc_rm32; break;
                case "excess-padding": changed = changed.Concat(Enumerable.Repeat(changed[^1], 40)).ToArray(); break;
            }
            Assert.That(X64GuardedScalarAccessorProof.TryProveCallerShape(changed), Is.Null, mutation);
        }
    }

    [Test]
    public void MissingMetadataCannotCreateAnAccessibleGetter() =>
        Assert.That(X64GuardedScalarAccessorProof.Find(null), Is.Null);

    [Test]
    public void EqualArrayShapesCompareByDescriptorFactsRatherThanWrapperObjects()
    {
        var first = ArrayState(ArrayDescriptor(), [4, 7], [-2, 3]);
        var second = ArrayState(ArrayDescriptor(), [4, 7], [-2, 3]);
        Assert.That(first.Matches(second), Is.True);
    }

    [TestCase("element")]
    [TestCase("rank")]
    [TestCase("size-count")]
    [TestCase("bound-count")]
    [TestCase("size-pointer")]
    [TestCase("bound-pointer")]
    [TestCase("size")]
    [TestCase("lower-bound")]
    public void ArrayShapeDimensionsAndPointersRemainImmutable(string mutation)
    {
        var original = ArrayState(ArrayDescriptor(), [4, 7], [-2, 3]);
        var array = ArrayDescriptor();
        int[] sizes = [4, 7];
        int[] bounds = [-2, 3];
        switch (mutation)
        {
            case "element": array.etype++; break;
            case "rank": array.rank = 3; break;
            case "size-count": array.numsizes = 1; sizes = [4]; break;
            case "bound-count": array.numlobounds = 1; bounds = [-2]; break;
            case "size-pointer": array.sizes++; break;
            case "bound-pointer": array.lobounds++; break;
            case "size": sizes[0]++; break;
            case "lower-bound": bounds[0]++; break;
        }
        Assert.That(original.Matches(ArrayState(array, sizes, bounds)), Is.False, mutation);
    }

    [TestCase("zero-rank")]
    [TestCase("large-rank")]
    [TestCase("oversized-count")]
    [TestCase("missing-sizes")]
    [TestCase("missing-bounds")]
    public void IncompleteOrUnsupportedArrayShapeTablesRemainUnproved(string mutation)
    {
        var array = ArrayDescriptor();
        int[] sizes = [4, 7];
        int[] bounds = [-2, 3];
        switch (mutation)
        {
            case "zero-rank": array.rank = 0; break;
            case "large-rank": array.rank = 33; break;
            case "oversized-count": array.numsizes = 3; sizes = [4, 7, 9]; break;
            case "missing-sizes": sizes = [4]; break;
            case "missing-bounds": bounds = [-2]; break;
        }
        var values = new List<object>();
        Assert.That(X64GuardedScalarAccessorProof.TryCaptureArrayShape(array, sizes, bounds, values), Is.False);
        Assert.That(values, Is.Empty);
    }

    private static Il2CppArrayType ArrayDescriptor() => new()
    {
        etype = 0x5000, rank = 2, numsizes = 2, numlobounds = 2, sizes = 0x6000, lobounds = 0x7000
    };
    private static X64GuardedScalarAccessorProof.InputState ArrayState(Il2CppArrayType array, int[] sizes, int[] bounds)
    {
        var values = new List<object>();
        Assert.That(X64GuardedScalarAccessorProof.TryCaptureArrayShape(array, sizes, bounds, values), Is.True);
        return new X64GuardedScalarAccessorProof.InputState(values, [], []);
    }

    private static Instruction[] Getter(int width, bool nop)
    {
        var assembler = new Assembler(64);
        if (nop) assembler.nop();
        if (width == 8) assembler.movzx(eax, __byte_ptr[rcx + 24]);
        else assembler.mov(eax, __dword_ptr[rcx + 24]);
        assembler.ret();
        return Decode(assembler);
    }
    private static Instruction[] Caller(int width, bool padding)
    {
        var assembler = new Assembler(64);
        var failure = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        assembler.mov(rax, __qword_ptr[rcx + 16]);
        assembler.test(rax, rax);
        assembler.je(failure);
        if (width == 8) assembler.movzx(eax, __byte_ptr[rax + 24]);
        else assembler.mov(eax, __dword_ptr[rax + 24]);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref failure);
        assembler.call(0x4000);
        if (padding) assembler.int3();
        return Decode(assembler);
    }
    private static Instruction[] Decode(Assembler assembler)
    {
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
