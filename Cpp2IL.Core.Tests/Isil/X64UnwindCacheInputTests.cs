using System;
using System.Buffers.Binary;
using System.IO;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64UnwindCacheInputTests
{
    private const ulong ImageBase = 0x180000000;

    [TestCase(0)] // DOS signature.
    [TestCase(0x3C)] // Header location.
    [TestCase(0x80)] // PE signature.
    [TestCase(0xB0)] // Image base.
    [TestCase(0x120)] // Exception directory.
    [TestCase(0x130)] // Relocation directory.
    [TestCase(0x19C)] // Section raw mapping.
    [TestCase(0x1AC)] // Section permissions.
    [TestCase(0x600)] // First .pdata record.
    [TestCase(0x620)] // Other record's final byte still establishes ordered gaps.
    [TestCase(0x800)] // Unwind header.
    [TestCase(0x804)] // Unwind operation.
    [TestCase(0x807)] // Alignment padding belongs to the unwind record.
    [TestCase(0x810)] // Chained unwind header.
    [TestCase(0x81F)] // Chain tuple's final byte.
    [TestCase(0x838)] // Handler RVA.
    [TestCase(0x83B)] // Handler RVA's final byte.
    [TestCase(0x988)] // Relocation entry.
    [TestCase(0x98B)] // Directory padding is authenticated too.
    public void CachedStructureRejectsChangedBackingStreamAndAcceptsRestoredEvidence(int offset)
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe);
        Assert.That(index, Is.Not.Null);
        Assert.That(index!.ClassifySpan(ImageBase + 0x1100, ImageBase + 0x1101).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        var original = bytes[offset];
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
        Assert.That(X64UnwindProof.ForBinary(pe), Is.Null,
            "Cached parsing must not authenticate the changed buffer.");
        Assert.That(index.HasUnchangedInput(pe.GetRawBinaryContent()), Is.False);
        stream.Position = offset;
        stream.WriteByte(original);
        Assert.That(X64UnwindProof.ForBinary(pe), Is.SameAs(index));
    }

    [TestCase(0x250)] // Native code is authenticated by the semantic caller.
    [TestCase(0x83C)] // Language-specific handler data is read afresh by its own cursor.
    [TestCase(0x870)] // Unconsumed xdata.
    [TestCase(0x5F0)] // Padding outside every structural record.
    public void UnrelatedWritableInputDoesNotInvalidateCachedStructure(int offset)
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe);
        Assert.That(index, Is.Not.Null);
        stream.Position = offset;
        stream.WriteByte(0xA5);
        Assert.That(X64UnwindProof.ForBinary(pe), Is.SameAs(index));
    }

    [Test]
    public void ShortenedInputCannotMatchRecordedRangesAndSyntheticIndicesRemainIndependent()
    {
        var image = Image();
        var parsed = X64UnwindProof.Parse(image)!;
        Assert.That(parsed.HasUnchangedInput(image.AsSpan(0, 0x98B)), Is.False);
        var synthetic = new X64UnwindProof.Index(ImageBase, 0x4000, [], []);
        Assert.That(synthetic.HasUnchangedInput(ReadOnlySpan<byte>.Empty), Is.True);
    }

    private static byte[] Image()
    {
        // These are synthetic PE directory records, not bytes copied from a player.
        var image = new byte[0xA00];
        U16(0, 0x5A4D); U32(0x3C, 0x80);
        U32(0x80, 0x4550); U16(0x84, 0x8664); U16(0x86, 3); U16(0x94, 0xF0);
        U16(0x98, 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0), ImageBase);
        U32(0xD0, 0x4000); U32(0x104, 16);
        U32(0x120, 0x2000); U32(0x124, 36);
        U32(0x130, 0x3180); U32(0x134, 12);
        Section(0x188, 0x1000, 0x200, 0x400, 0x60000020);
        Section(0x1B0, 0x2000, 0x600, 0x100, 0x40000040);
        Section(0x1D8, 0x3000, 0x800, 0x200, 0x40000040);
        Record(0x600, 0x1000, 0x1010, 0x3000);
        Record(0x60C, 0x1100, 0x1110, 0x3010);
        Record(0x618, 0x1200, 0x1220, 0x3030);
        Unwind(0x800, 0); Unwind(0x830, 1);
        image[0x810] = 1 | 4 << 3;
        U32(0x814, 0x1000); U32(0x818, 0x1010); U32(0x81C, 0x3000);
        U32(0x838, 0x1200); U32(0x83C, 0x3070);
        U32(0x980, 0x3000); U32(0x984, 12); U16(0x988, 0xA070);
        return image;

        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), value);
        void Section(int at, uint rva, uint raw, uint size, uint flags)
        {
            U32(at + 8, size); U32(at + 12, rva); U32(at + 16, size);
            U32(at + 20, raw); U32(at + 36, flags);
        }
        void Record(int at, uint start, uint end, uint unwind)
        { U32(at, start); U32(at + 4, end); U32(at + 8, unwind); }
        void Unwind(int at, int flags)
        {
            image[at] = (byte)(1 | flags << 3);
            image[at + 1] = 4; image[at + 2] = 1;
            image[at + 4] = 4; image[at + 5] = 0x42;
        }
    }
}
