using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>
/// The self-contained image exercises the public dense-switch layout without
/// storing any generated player bytes. The optional exact-player control uses
/// the ignored output of Validation/DenseSwitchFixture at test time.
/// </summary>
[NonParallelizable]
public class X64ClosedSwitchTableFixtureTests
{
    private const ulong ImageBase = 0x180000000;
    private const uint EntryRva = 0x1000;
    private const int RawStart = 0x200;
    private const int SectionHeader = 0x188;

    [Test]
    public void NativeWidthEvidenceDoesNotAssumeAManagedByRefWithoutMethodContext()
    {
        var load = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString("418B00"))).Decode();
        var liftedLoad = new X86InstructionSet().GetIsilFromInstruction(load).Single();
        Assert.Multiple(() =>
        {
            Assert.That(liftedLoad.OpCode, Is.EqualTo(Cpp2IL.Core.ISIL.OpCode.Move));
            Assert.That(liftedLoad.IntegerBitWidth, Is.Zero);
        });

        foreach (var (hex, expectedOpcode, expectedWidth) in new[]
                 {
                     ("2BC1", Cpp2IL.Core.ISIL.OpCode.Subtract, 32),
                     ("482BC1", Cpp2IL.Core.ISIL.OpCode.Subtract, 64),
                     ("6BC103", Cpp2IL.Core.ISIL.OpCode.Multiply, 32),
                     ("486BC103", Cpp2IL.Core.ISIL.OpCode.Multiply, 64),
                 })
        {
            var native = Iced.Intel.Decoder.Create(64,
                new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(hex))).Decode();
            var lifted = new X86InstructionSet().GetIsilFromInstruction(native)
                .Single(instruction => instruction.OpCode == expectedOpcode);
            Assert.That(lifted.IntegerBitWidth, Is.EqualTo(expectedWidth), hex);
        }
    }

    [Test]
    public void ClosedDenseTableSeparatesExecutableCasesFromEmbeddedData()
    {
        var image = BuildNeutralImage();
        var evidence = Prove(image);
        Assert.That(evidence, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(evidence!.CaseTargets, Has.Count.EqualTo(16));
            Assert.That(evidence.DefaultTarget, Is.LessThan(evidence.Table));
            Assert.That(evidence.Code.All(instruction => instruction.IP < evidence.Table), Is.True);
            Assert.That(evidence.Code.Any(instruction => instruction.IP == evidence.Dispatch), Is.True);
        });
    }

    [Test]
    public void GuardTableAndCodeBoundaryMutationsAreRejected()
    {
        var control = BuildNeutralImage();
        var proved = Prove(control)!;
        Assert.That(proved, Is.Not.Null);
        var tableRaw = RawStart + checked((int)(proved.Table - (ImageBase + EntryRva)));

        AssertRejected("signed guard", image => image[RawStart + 3] = 0x7F);
        AssertRejected("changed bound", image => image[RawStart + 2] = 14);
        AssertRejected("table target in instruction interior", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tableRaw, 4),
                checked((uint)(proved.CaseTargets[0] - ImageBase + 1))));
        AssertRejected("unbacked table tail", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 16, 4),
                checked((uint)(tableRaw - RawStart + 60))));
        AssertRejected("writable executable table", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 36, 4),
                0xE0000020));
        AssertRejected("default edge into table", image => image[RawStart + 4] = 126);
        AssertRejected("section raw offset overflow", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 20, 4),
                uint.MaxValue));
        AssertRejected("section virtual extent overflow", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 8, 4),
                uint.MaxValue));
        AssertRejected("PE header offset overflow", image =>
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C, 4),
                uint.MaxValue));

        Assert.That(Prove(control, [ImageBase + EntryRva, proved.CaseTargets[0]]),
            Is.Null, "another method root cannot overlap a case");

        void AssertRejected(string defect, Action<byte[]> mutate)
        {
            var image = (byte[])control.Clone();
            mutate(image);
            Assert.That(Prove(image), Is.Null, defect);
        }
    }

    [Test]
    public void RelocationReaderRejectsFixupsAndMalformedBlocksInProvedSpan()
    {
        var evidence = Prove(BuildNeutralImage())!;
        Assert.That(evidence, Is.Not.Null);
        var blocks = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(0, 4), EntryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(4, 4), 10);
        var spanLength = checked((uint)(evidence.Table - evidence.Entry + 16 * 4));
        var tableOffsetInPage = checked((ushort)(evidence.Table - ImageBase - EntryRva));
        BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(8, 2),
            (ushort)(0xA000 | tableOffsetInPage));
        Assert.That(X64PeOnceFlagProof.HasNoRelocationInRange(blocks,
            EntryRva, spanLength), Is.False, "a table entry may be loader-patched");

        BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(8, 2), 0xA000);
        Assert.That(X64PeOnceFlagProof.HasNoRelocationInRange(blocks,
            EntryRva, spanLength), Is.False, "a dispatch instruction may be loader-patched");

        BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(8, 2), 0);
        Assert.That(X64PeOnceFlagProof.HasNoRelocationInRange(blocks,
            EntryRva, spanLength), Is.True);
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(4, 4), 9);
        Assert.That(X64PeOnceFlagProof.HasNoRelocationInRange(blocks,
            EntryRva, spanLength), Is.False, "malformed block size must fail closed");
    }

    [Test]
    public void ExactOriginalDenseSwitchAndMutatedTable()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_DENSE_SWITCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_DENSE_SWITCH_FIXTURE_INPUT to the neutral player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("DenseSwitchFixture")!.Types
                .Single(type => type.Name == "SwitchMethods").Methods
                .Single(candidate => candidate.Name == "Dense");
            var evidence = X64ClosedSwitchTableProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.CaseTargets, Has.Count.EqualTo(16));
            Assert.Multiple(() =>
            {
                Assert.That(X64ClosedSwitchDispatchRecovery.BoundInt32Selector(method, evidence),
                    Is.True, "the selector must bind an unchanged managed Int32 argument");
                Assert.That(X64ClosedSwitchDispatchRecovery.DispatchValuesDeadAtTargets(evidence),
                    Is.True, "the dispatch scratch values must be dead at all targets");
            });
            Assert.That(X64ClosedSwitchDispatchRecovery.Find(method), Is.Not.Null);

            method.EnsureRawBytes();
            method.Analyze();
            var unresolved = method.Locals.Where(local => local.Type == null)
                .Select(local => $"{local.Name}:{local.Register}").ToArray();
            Assert.That(unresolved, Is.Empty);

            var byRef = method.ParameterLocals.Single(local => local.Name == "trace");
            var directAccess = new Cpp2IL.Core.ISIL.MemoryOperand(byRef);
            var rewrittenRegister = new Cpp2IL.Core.ISIL.LocalVariable("alias",
                byRef.Register.Copy(0), byRef.Type);
            Assert.Multiple(() =>
            {
                Assert.That(Cpp2IL.Core.Analysis.X64Int32ByRefAccessProof.IsManagedAccess(
                    method, directAccess), Is.True);
                Assert.That(Cpp2IL.Core.Analysis.X64Int32ByRefAccessProof.IsManagedAccess(
                    method, new Cpp2IL.Core.ISIL.MemoryOperand(rewrittenRegister)), Is.False,
                    "an SSA alias does not identify the original by-ref element");
                Assert.That(Cpp2IL.Core.Analysis.X64Int32ByRefAccessProof.IsManagedAccess(
                    method, new Cpp2IL.Core.ISIL.MemoryOperand(byRef, addend: 4)), Is.False);
                Assert.That(NativeInt32ByRef("418B00"), Is.True,
                    "a direct 32-bit native load establishes its read width");
                Assert.That(NativeInt32ByRef("498B00"), Is.False,
                    "an otherwise identical 64-bit load cannot be typed as Int32");
                Assert.That(NativeInt32ByRef("418B4004"), Is.False,
                    "an offset access is not the by-ref Int32 element");
            });

            bool NativeInt32ByRef(string hex) =>
                Cpp2IL.Core.Analysis.X64Int32ByRefAccessProof.IsNativeAccess(method,
                    Iced.Intel.Decoder.Create(64,
                        new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(hex))).Decode());

            var literalMove = evidence.Code.First(instruction =>
                instruction.Code == Iced.Intel.Code.Mov_r32_imm32 &&
                instruction.GetImmediate(1) > int.MaxValue);
            var literal = new Cpp2IL.Core.ISIL.LocalVariable("literal",
                new Cpp2IL.Core.ISIL.Register(null,
                    X86Utils.GetRegisterName(literalMove.Op0Register), 0));
            var typed = new Cpp2IL.Core.ISIL.LocalVariable("typed",
                new Cpp2IL.Core.ISIL.Register(null, "typed", 0),
                app.SystemTypes.SystemInt32Type);
            var product = new Cpp2IL.Core.ISIL.LocalVariable("product",
                new Cpp2IL.Core.ISIL.Register(null, "product", 0));
            var definition = new Cpp2IL.Core.ISIL.Instruction(0,
                Cpp2IL.Core.ISIL.OpCode.Move, literal,
                new Cpp2IL.Core.ISIL.Immediate((long)literalMove.GetImmediate(1)))
                { NativeAddress = literalMove.IP };
            var originalGraph = method.ControlFlowGraph;
            try
            {
                method.ControlFlowGraph = new ISILControlFlowGraph([
                    definition,
                    new Cpp2IL.Core.ISIL.Instruction(1,
                        Cpp2IL.Core.ISIL.OpCode.Subtract, product, literal, typed)
                        { IntegerBitWidth = 32 },
                    new Cpp2IL.Core.ISIL.Instruction(2,
                        Cpp2IL.Core.ISIL.OpCode.Return, product),
                ]);
                Assert.That(X86Int32ImmediateSourceProof.IsExclusiveArithmeticSource(method, literal),
                    Is.True);
                definition.NativeAddress = evidence.Dispatch;
                Assert.That(X86Int32ImmediateSourceProof.IsExclusiveArithmeticSource(method, literal),
                    Is.False, "a different native instruction cannot authenticate the literal");
                definition.NativeAddress = literalMove.IP;
                definition.SetOperand(1, new Cpp2IL.Core.ISIL.Immediate(
                    (long)literalMove.GetImmediate(1) ^ 1));
                Assert.That(X86Int32ImmediateSourceProof.IsExclusiveArithmeticSource(method, literal),
                    Is.False, "the ISIL literal must match the PE-backed native bytes");
            }
            finally { method.ControlFlowGraph = originalGraph; }

            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var image = pe.GetRawBinaryContent().ToArray();
            var offset = checked((int)pe.MapVirtualAddressToRaw(evidence.Table, false));
            var original = image.AsSpan(offset, 4).ToArray();
            try
            {
                // One entry now points into the first instruction, which is a
                // valid byte address but not an established instruction start.
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4),
                    checked((uint)(evidence.CaseTargets[0] - unwind.ImageBase + 1)));
                Assert.That(X64ClosedSwitchTableProof.TryProve(image,
                    method.UnderlyingPointer, unwind.ImageBase,
                    app.MethodsByAddress.Keys, unwind.ClassifySpan), Is.Null);
            }
            finally { original.CopyTo(image.AsSpan(offset, 4)); }

            var peHeader = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                image.AsSpan(0x3C, 4)));
            var relocationSizeOffset = peHeader + 24 + 112 + 5 * 8 + 4;
            BinaryPrimitives.WriteUInt32LittleEndian(
                image.AsSpan(relocationSizeOffset, 4), 7);
            using var malformedStream = new MemoryStream();
            malformedStream.Write(image);
            malformedStream.Position = 0;
            var malformedPe = new PE(malformedStream);
            Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(malformedPe,
                unwind, evidence.Entry,
                checked((uint)(evidence.Table - evidence.Entry + 16 * 4))),
                Is.False, "a malformed relocation directory cannot authenticate the table");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static X64ClosedSwitchTableProof.Evidence? Prove(byte[] image,
        ulong[]? roots = null) =>
        X64ClosedSwitchTableProof.TryProve(image, ImageBase + EntryRva,
            ImageBase, roots ?? [ImageBase + EntryRva],
            (start, end) => new(X64UnwindProof.SpanKind.NoEntry, start, end));

    private static byte[] BuildNeutralImage()
    {
        var image = new byte[0x400];
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0, 2), 0x5A4D);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x80, 4), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84, 2), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94, 2), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98, 2), 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x98 + 24, 8), ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98 + 56, 4), 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 8, 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 12, 4), EntryRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 16, 4), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 20, 4), RawStart);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SectionHeader + 36, 4), 0x60000020);

        // An independently assembled 16-way unsigned guarded switch. Each
        // target body returns a different constant; the source fixture supplies
        // the meaningful arithmetic and by-reference behavior for Unity.
        var code = new byte[]
        {
            0x83, 0xF9, 0x0F,              // cmp ecx, 15
            0x77, 0x00,                    // ja default (patched below)
            0x48, 0x63, 0xC1,              // movsxd rax, ecx
            0x4C, 0x8D, 0x0D, 0, 0, 0, 0, // lea r9, [rip + image base]
            0x41, 0x8B, 0x8C, 0x81, 0, 0, 0, 0, // mov ecx, [r9+rax*4+table]
            0x49, 0x03, 0xC9,              // add rcx, r9
            0xFF, 0xE1,                    // jmp rcx
        };
        var dispatchLength = code.Length;
        var bytes = new List<byte>(code);
        for (var i = 0; i < 17; i++)
        {
            bytes.Add(0xB8); // mov eax, case-specific immediate
            AppendUInt32(bytes, (uint)(i + 1));
            bytes.Add(0xC3); // ret
        }
        bytes.Add(0x90); // alignment between code and table
        var tableOffset = bytes.Count;
        for (var i = 0; i < 16; i++)
            AppendUInt32(bytes, EntryRva + (uint)(code.Length + i * 6));

        code = bytes.ToArray();
        var defaultOffset = dispatchLength + 16 * 6;
        code[4] = checked((byte)(defaultOffset - 5)); // default - guard next
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(11, 4),
            -checked((int)(EntryRva + 15)));
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(19, 4),
            EntryRva + (uint)tableOffset);
        code.CopyTo(image.AsSpan(RawStart));
        return image;
    }

    private static void AppendUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }
}
