using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64PeOnceFlagProofTests
{
    [Test]
    public void RelocationTableMustNotChangeFlagByte()
    {
        var block = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), 0xA005);

        Assert.Multiple(() =>
        {
            Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block, 0x1010), Is.True);
            Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block, 0x1005), Is.False);
            Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block, 0x100C), Is.False);
            Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block, 0x100D), Is.True);
            Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block.AsSpan(0, 10),
                0x1010), Is.False);
        });

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), 0x3005);
        Assert.That(X64PeOnceFlagProof.HasNoRelocationOnByte(block, 0x1010), Is.False,
            "Unsupported relocation kinds must fail closed.");
    }

    [Test]
    public void ExactPlayerAcceptsFileBackedZeroFlagOnlyWithRelocationEvidence()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CLASS_CAST_LOOKUP_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CLASS_CAST_LOOKUP_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var lookup = app.GetAssemblyByName("ClassCastLookupFixture")!.Types
                .Single(type => type.Name == "Resolver").Methods
                .Single(method => method.Name == "Lookup");
            lookup.EnsureRawBytes();
            var native = X86Utils.Iterate(lookup).ToArray();
            Assert.That(native.Length, Is.GreaterThanOrEqualTo(37));
            var shape = X64ClassCastLookupProof.TryProveShape(native.Take(37).ToArray());
            Assert.That(shape, Is.Not.Null);
            Assert.That(native[36].Code, Is.EqualTo(Iced.Intel.Code.Retnq));

            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            Assert.Multiple(() =>
            {
                Assert.That(X64MetadataStaticGetterProof.ZeroInitializedWritableData(
                    unwind, shape!.Flag, 1), Is.False);
                Assert.That(X64MetadataStaticGetterProof.FileBackedWritableData(
                    pe, unwind, shape!.Flag, 1), Is.True);
                Assert.That(X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape!.Flag),
                    Is.True);
                Assert.That(X64ClassCastLookupProof.Find(lookup, native), Is.Not.Null);
            });

            var changedBytes = File.ReadAllBytes(binary);
            var flagRaw = pe.MapVirtualAddressToRaw(shape!.Flag, false);
            Assert.That(flagRaw, Is.GreaterThanOrEqualTo(0));
            changedBytes[(int)flagRaw] = 1;
            using var stream = new MemoryStream(changedBytes, 0, changedBytes.Length,
                writable: false, publiclyVisible: true);
            var changedPe = new PE(stream);
            var changedUnwind = X64UnwindProof.Parse(changedPe.GetRawBinaryContent());
            Assert.That(changedUnwind, Is.Not.Null);
            Assert.That(X64PeOnceFlagProof.IsInitiallyZero(changedPe,
                changedUnwind!, shape.Flag), Is.False,
                "An initialized nonzero once flag must not be accepted.");
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
