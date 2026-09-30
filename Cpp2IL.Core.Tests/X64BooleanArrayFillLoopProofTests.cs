using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64BooleanArrayFillLoopProofTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CompleteLoopBindsItsStoreInputAndReload(bool parameter, bool padding)
    {
        var proof = X64BooleanArrayFillLoopProof.TryProveShape(Body(parameter, padding));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.FieldOffset, Is.EqualTo(0x10));
            Assert.That(proof.UsesParameter, Is.EqualTo(parameter));
            Assert.That(proof.NullCall, Is.EqualTo(padding ? 26 : 25));
            Assert.That(proof.BoundsCall, Is.EqualTo(padding ? 28 : 27));
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InitialCopiedArrayHasTheSameBoundOrigins(bool parameter, bool padding)
    {
        var proof = X64BooleanArrayFillLoopProof.TryProveShape(Body(parameter, padding, "rax-load"));
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.UsesParameter, Is.EqualTo(parameter));
    }

    [TestCase("signed-element-guard")]
    [TestCase("unsigned-loop-test")]
    [TestCase("backedge-to-store")]
    [TestCase("bounds-to-null")]
    [TestCase("post-store-null-to-return")]
    [TestCase("different-reload-field")]
    [TestCase("cached-reload")]
    [TestCase("wide-store")]
    [TestCase("wrong-store-index")]
    [TestCase("wrong-store-value")]
    [TestCase("wrong-counter-copy")]
    [TestCase("decrement")]
    [TestCase("wrong-length-width")]
    [TestCase("prior-effect")]
    [TestCase("missing-trap")]
    public void ChangedLoopEffectsAndExceptionalPathsAreRejected(string variant)
    {
        Assert.That(X64BooleanArrayFillLoopProof.TryProveShape(Body(true, true, variant)), Is.Null);
    }

    [Test]
    public void NonzeroLiteralAndDiscontinuousNativeBodyAreRejected()
    {
        Assert.That(X64BooleanArrayFillLoopProof.TryProveShape(Body(false, true, "literal-one")), Is.Null);
        var body = Body(false, true);
        var changed = body[10];
        changed.IP++;
        body[10] = changed;
        Assert.That(X64BooleanArrayFillLoopProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void ExactPlayerRequiresOriginalArraySignatureLayoutAndBytes()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_ARRAY_FILL_LOOP_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_ARRAY_FILL_LOOP_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var holder = app.GetAssemblyByName("BooleanArrayFillLoopFixture")!.Types
                .Single(type => type.Name == "ArrayHolder");
            var field = holder.Fields.Single(candidate => candidate.Name == "Values");
            foreach (var method in holder.Methods.Where(candidate => candidate.Name is "FillFalse" or "Fill"))
            {
                var evidence = X64BooleanArrayFillLoopProof.Find(method);
                Assert.That(evidence, Is.Not.Null, method.Name);
                Assert.That(evidence!.ArrayField, Is.SameAs(field));
                Assert.That(evidence.UsesParameter, Is.EqualTo(method.Name == "Fill"));

                var flags = method.Definition!.iflags;
                method.Definition.iflags = (ushort)(flags | 0xF000);
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { method.Definition.iflags = flags; }

                method.Definition.iflags = (ushort)(flags | 0x0020);
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { method.Definition.iflags = flags; }

                var offset = field.OverrideOffset;
                field.OverrideOffset = holder.Fields.Single(candidate => candidate.Name == "Neighbor").Offset;
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { field.OverrideOffset = offset; }

                var raw = field.BackingData!.Field.RawFieldType!;
                var modifiers = raw.NumMods;
                raw.NumMods = 1;
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { raw.NumMods = modifiers; }
                var element = raw.GetEncapsulatedType()!;
                var pinned = element.Pinned;
                element.Pinned = 1;
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { element.Pinned = pinned; }

                var bindings = app.MethodsByAddress[method.UnderlyingPointer];
                bindings.Add(method);
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { bindings.RemoveAt(bindings.Count - 1); }

                var bytes = method.RawBytes;
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                try { Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Null); }
                finally { method.RawBytes = bytes; }
                Assert.That(X64BooleanArrayFillLoopProof.Find(method), Is.Not.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static NativeInstruction[] Body(bool parameter, bool padding, string variant = "")
    {
        var assembler = new Assembler(64);
        var loop = assembler.CreateLabel();
        var store = assembler.CreateLabel();
        var done = assembler.CreateLabel();
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        if (parameter)
        {
            if (variant == "rax-load") assembler.mov(rax, __qword_ptr[rcx + 0x10]);
            else assembler.mov(r9, __qword_ptr[rcx + 0x10]);
            assembler.xor(r8d, r8d);
            assembler.mov(r11, rcx);
            assembler.mov(r10d, r8d);
            if (variant == "rax-load")
            {
                assembler.mov(r9, rax);
                assembler.test(rax, rax);
                assembler.je(nullExit);
            }
            else
            {
                assembler.test(r9, r9);
                assembler.je(nullExit);
                assembler.mov(rax, r9);
            }
        }
        else
        {
            if (variant == "rax-load") assembler.mov(rax, __qword_ptr[rcx + 0x10]);
            else assembler.mov(r8, __qword_ptr[rcx + 0x10]);
            assembler.xor(edx, edx);
            assembler.mov(r10, rcx);
            assembler.mov(r9d, edx);
            if (variant == "rax-load")
            {
                assembler.mov(r8, rax);
                assembler.test(rax, rax);
                assembler.je(nullExit);
            }
            else
            {
                assembler.test(r8, r8);
                assembler.je(nullExit);
                assembler.mov(rax, r8);
            }
        }
        if (padding) assembler.nop();
        assembler.Label(ref loop);
        if (variant == "wrong-length-width") assembler.cmp(r10, __qword_ptr[r9 + 0x18]);
        else if (parameter) assembler.cmp(r10d, __dword_ptr[r9 + 0x18]);
        else assembler.cmp(r9d, __dword_ptr[r8 + 0x18]);
        if (variant == "unsigned-loop-test") assembler.jae(done);
        else assembler.jge(done);
        assembler.test(rax, rax);
        assembler.je(nullExit);
        if (parameter) assembler.cmp(r8d, __dword_ptr[rax + 0x18]);
        else assembler.cmp(edx, __dword_ptr[rax + 0x18]);
        if (variant == "signed-element-guard") assembler.jge(boundsExit);
        else assembler.jae(variant == "bounds-to-null" ? nullExit : boundsExit);
        if (parameter) assembler.movsxd(rcx, r8d);
        else assembler.movsxd(rcx, edx);
        if (variant == "decrement") assembler.dec(r8d);
        else if (parameter) assembler.inc(r8d);
        else assembler.inc(edx);
        if (variant == "wrong-counter-copy") assembler.mov(r10d, edx);
        else if (parameter) assembler.mov(r10d, r8d);
        else assembler.mov(r9d, edx);
        assembler.Label(ref store);
        if (variant == "wide-store") assembler.mov(__dword_ptr[rcx + rax + 0x20], edx);
        else if (variant == "wrong-store-index") assembler.mov(__byte_ptr[r8 + rax + 0x20], dl);
        else if (variant == "wrong-store-value") assembler.mov(__byte_ptr[rcx + rax + 0x20], r8b);
        else if (parameter) assembler.mov(__byte_ptr[rcx + rax + 0x20], dl);
        else assembler.mov(__byte_ptr[rcx + rax + 0x20], variant == "literal-one" ? 1 : 0);
        if (variant == "prior-effect") assembler.inc(__dword_ptr[r11 + 0x18]);
        if (variant == "cached-reload") assembler.mov(rax, r9);
        else if (parameter) assembler.mov(rax, __qword_ptr[r11 + (variant == "different-reload-field" ? 0x18 : 0x10)]);
        else assembler.mov(rax, __qword_ptr[r10 + 0x10]);
        if (parameter) assembler.mov(r9, rax);
        else assembler.mov(r8, rax);
        assembler.test(rax, rax);
        assembler.je(variant == "post-store-null-to-return" ? done : nullExit);
        assembler.jmp(variant == "backedge-to-store" ? store : loop);
        assembler.Label(ref done);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref nullExit);
        assembler.call(0x4000UL);
        if (variant != "missing-trap") assembler.int3();
        assembler.Label(ref boundsExit);
        assembler.call(0x5000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), 0x1000, false).ToArray();
    }
}
