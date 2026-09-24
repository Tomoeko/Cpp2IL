using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ReferenceFieldStoreProofTests
{
    [Test]
    public void CardMarkerDataMustBeContiguousEvenWhenBothEndpointsAreWritable()
    {
        const uint writableData = 0xC0000040;
        const uint tableRva = 0x1000;
        const uint tableLastRva = tableRva + (1U << 21) / 8 - 1;
        var split = new X64UnwindProof.Index(0x180000000, 0x50000,
        [
            new X64UnwindProof.Section(0x1000, 0x20000, 0, 0x20000, writableData),
            new X64UnwindProof.Section(0x22000, 0x21000, 0x20000, 0x21000, writableData),
        ], []);
        Assert.Multiple(() =>
        {
            Assert.That(split.IsWritableFileBackedRva(tableRva), Is.True);
            Assert.That(split.IsWritableFileBackedRva(tableLastRva), Is.True);
            Assert.That(X64ReferenceWriteBarrierProof.HasWritableCardMarkerData(split, 0x2000,
                tableRva), Is.False);
        });

        // File-backed bytes and the zero-initialized virtual tail are one mapped section.
        var contiguous = new X64UnwindProof.Index(0x180000000, 0x50000,
            [new X64UnwindProof.Section(0x1000, 0x45000, 0, 0x1000, writableData)], []);
        Assert.That(X64ReferenceWriteBarrierProof.HasWritableCardMarkerData(contiguous,
            0x1ffe, tableRva), Is.True);
    }

    [Test]
    public void ExactShapeKeepsTheValueRegisterAndFieldOffset()
    {
        var shape = X64ReferenceFieldStoreProof.TryProveShape(SyntheticBody());
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.FieldOffset, Is.EqualTo(0x30));
            Assert.That(shape.BarrierTarget, Is.EqualTo(0x2000));
            Assert.That(shape.NullTarget, Is.EqualTo(0x3000));
        });
    }

    [TestCase("value-register")]
    [TestCase("store-width")]
    [TestCase("null-branch")]
    [TestCase("field-base")]
    [TestCase("stack")]
    [TestCase("tail-transfer")]
    [TestCase("extra-exit")]
    public void NeighboringNativeShapesRemainUnproved(string defect)
    {
        var body = SyntheticBody();
        if (defect == "extra-exit")
            body = [..body, Instruction.Create(Code.Retnq)];
        else
        {
            var index = defect switch
            {
                "null-branch" => 2,
                "field-base" => 3,
                "value-register" or "store-width" => 4,
                "stack" => 5,
                _ => 6,
            };
            var instruction = body[index];
            switch (defect)
            {
                case "value-register": instruction.Op1Register = Register.RAX; break;
                case "store-width": instruction.Code = Code.Mov_rm32_r32; break;
                case "null-branch": instruction.NearBranch64 = body[6].IP; break;
                case "field-base": instruction.Op0Register = Register.RDX; break;
                case "stack": instruction.Immediate8to64 = 0x20; break;
                case "tail-transfer": instruction.Code = Code.Call_rel32_64; break;
            }
            body[index] = instruction;
        }
        Assert.That(X64ReferenceFieldStoreProof.TryProveShape(body), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresMetadataAndBothRuntimeHelpers()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_STORE_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ReferenceStoreFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "Store");
            var native = X86Utils.Iterate(method).ToArray();
            var evidence = X64ReferenceFieldStoreProof.Find(method, native);
            Assert.That(evidence?.Field.Name, Is.EqualTo("Next"));
            Assert.That(X64ReferenceFieldStoreProof.TryLift(method, native)?.Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.Return }));
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.Return }));

            var field = evidence!.Field;
            try
            {
                field.OverrideOffset = field.DefaultOffset + 8;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null);
            }
            finally { field.OverrideOffset = null; }
            try
            {
                field.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null);
            }
            finally { field.Attributes = field.DefaultAttributes; }

            var wrongBarrier = native.ToArray();
            wrongBarrier[6].NearBranch64 = native[7].NearBranchTarget;
            Assert.That(X64ReferenceFieldStoreProof.Find(method, wrongBarrier), Is.Null);
            var wrongNull = native.ToArray();
            wrongNull[7].NearBranch64 = native[6].NearBranchTarget;
            Assert.That(X64ReferenceFieldStoreProof.Find(method, wrongNull), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] SyntheticBody()
    {
        // Neutral x64 instructions with a different field displacement and dummy helper targets.
        var bytes = Convert.FromHexString("4883EC284885C974104883C1304889114883C428E9E70F0000E8E21F0000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        var body = new Instruction[8];
        for (var index = 0; index < body.Length; index++)
            body[index] = decoder.Decode();
        return body;
    }
}
