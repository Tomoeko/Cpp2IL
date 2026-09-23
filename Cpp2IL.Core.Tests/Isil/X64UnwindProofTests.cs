using System;
using System.Buffers.Binary;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X64UnwindProofTests
{
    private const ulong ImageBase = 0x180000000;
    [TestCase(1)] // language-specific exception handler
    [TestCase(2)] // unwind/termination handler
    [TestCase(3)]
    [TestCase(4)] // chained unwind may ultimately reach a handler
    [TestCase(5)]
    [TestCase(8)] // reserved/unknown flags
    [TestCase(16)]
    public void NormalInstructionsCannotHideNativeHandlersOrUnsupportedUnwind(int flags)
    {
        var image = Image();
        image[0x900] = (byte)(1 | flags << 3);
        Assert.That(Classify(image, 0x1000, 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(Classify(image, 0x1100, 0x1116).Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
    }

    [TestCase("duplicate")]
    [TestCase("overlap")]
    [TestCase("unsorted")]
    [TestCase("empty-record")]
    [TestCase("missing-directory")]
    [TestCase("partial-entry")]
    [TestCase("unaligned-directory")]
    [TestCase("ambiguous-section")]
    [TestCase("x86-header")]
    [TestCase("pe32-header")]
    [TestCase("raw-bounds")]
    [TestCase("image-bounds")]
    public void MalformedOrAmbiguousDirectoryCannotEstablishAnIndex(string defect)
    {
        var image = Image();
        switch (defect)
        {
            case "duplicate": image.AsSpan(0x800, 12).CopyTo(image.AsSpan(0x80C)); break;
            case "overlap": U32(image, 0x804, 0x1120); break;
            case "unsorted": U32(image, 0x800, 0x1150); U32(image, 0x804, 0x1160); break;
            case "empty-record": U32(image, 0x804, 0x1000); break;
            case "missing-directory": U32(image, 0x120, 0); break;
            case "partial-entry": U32(image, 0x124, 47); break;
            case "unaligned-directory": image.AsSpan(0x800, 48).CopyTo(image.AsSpan(0x801)); U32(image, 0x120, 0x2001); break;
            case "ambiguous-section": U32(image, 0x1E4, 0x2000); break;
            case "x86-header": U16(image, 0x84, 0x14C); break;
            case "pe32-header": U16(image, 0x98, 0x10B); break;
            case "raw-bounds": U32(image, 0x1EC, 0xA00); break;
            case "image-bounds": U32(image, 0x1E0, 0x100000); break;
        }
        Assert.That(X64UnwindProof.Parse(image), Is.Null);
    }

    [TestCase("unaligned-unwind")]
    [TestCase("unbacked-unwind")]
    [TestCase("unknown-version")]
    [TestCase("frame-register")]
    [TestCase("unknown-operation")]
    [TestCase("volatile-push")]
    [TestCase("truncated-operation")]
    [TestCase("offset-outside-prolog")]
    [TestCase("zero-code-offset")]
    [TestCase("truncated-codes")]
    [TestCase("prolog-outside-function")]
    public void InvalidUnwindRecordIsUnsupportedRatherThanMistakenForAnAbsentLeafRecord(string defect)
    {
        var image = Image();
        switch (defect)
        {
            case "unaligned-unwind": U32(image, 0x808, 0x3001); break;
            case "unbacked-unwind": U32(image, 0x808, 0x3200); break;
            case "unknown-version": image[0x900] = 2; break;
            case "frame-register": image[0x903] = 5; break;
            case "unknown-operation": image[0x905] = 7; break;
            case "volatile-push": image[0x905] = 0x10; break;
            case "truncated-operation": image[0x905] = 0x11; break;
            case "offset-outside-prolog": image[0x904] = 5; break;
            case "zero-code-offset": image[0x904] = 0; break;
            case "truncated-codes": U32(image, 0x1E8, 6); break;
            case "prolog-outside-function": image[0x901] = 17; break;
        }
        Assert.That(Classify(image, 0x1000, 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [Test]
    public void ExecutableAndWholeSpanBoundsAreRequired()
    {
        var image = Image();
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1120).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.ClassifySpan(ImageBase - 1, ImageBase + 9).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1000).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(X64UnwindProof.Parse(image.AsSpan(0, 0x904)), Is.Null);
        U32(image, 0x1AC, 0x40000040);
        Assert.That(Classify(image, 0x1000, 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [Test]
    public void MissingRecordsAndInteriorEntriesRemainExplicitForTheCallerProof()
    {
        var image = Image();
        U32(image, 0x124, 36); // The last function is no longer described by the directory.
        Assert.That(Classify(image, 0x1400, 0x1464).Kind, Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
        U32(image, 0x800, 0xFFF);
        var interior = Classify(image, 0x1000, 0x1009);
        Assert.That(interior.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That(interior.Start, Is.EqualTo(ImageBase + 0xFFF));
        U32(image, 0x804, 0x1008);
        Assert.That(Classify(image, 0x1000, 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    private static X64UnwindProof.SpanClassification Classify(byte[] image, uint start, uint end)
    {
        var index = X64UnwindProof.Parse(image);
        Assert.That(index, Is.Not.Null);
        return index!.ClassifySpan(ImageBase + start, ImageBase + end);
    }

    [Test]
    public void ClassifierDistinguishesInteriorInstructionSpansLeafGapsAndHiddenHandlers()
    {
        var image = Image();
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index, Is.Not.Null);
        var interior = index.ClassifySpan(ImageBase + 0x1004, ImageBase + 0x1009);
        Assert.That(interior.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That((interior.Start, interior.End), Is.EqualTo((ImageBase + 0x1000, ImageBase + 0x1010)));
        Assert.That(index.ClassifySpan(ImageBase + 0x1200, ImageBase + 0x120D).Kind, Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
        Assert.That(index.ClassifySpan(ImageBase + 0x1004, ImageBase + 0x1104).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.ClassifySpan(ImageBase + 0x2000, ImageBase + 0x2004).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        image[0x900] = 9;
        var withHandler = X64UnwindProof.Parse(image)!;
        Assert.That(withHandler, Is.Not.Null, "An unrelated handler is classified per region, not silently discarded or globally accepted.");
        Assert.That(withHandler.ClassifySpan(ImageBase + 0x1004, ImageBase + 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.ClassifySpan(ImageBase + 0x1004, ImageBase + 0x1009).Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree),
            "Parsed evidence is an immutable snapshot of the input bytes.");
    }

    private static byte[] Image()
    {
        var image = new byte[0xA00];
        U16(image, 0, 0x5A4D); U32(image, 0x3C, 0x80);
        U32(image, 0x80, 0x4550); U16(image, 0x84, 0x8664); U16(image, 0x86, 3); U16(image, 0x94, 0xF0);
        U16(image, 0x98, 0x20B); BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0), ImageBase);
        U32(image, 0xD0, 0x4000); U32(image, 0x104, 16); U32(image, 0x120, 0x2000); U32(image, 0x124, 48);
        Section(0x188, 0x1000, 0x200, 0x600, 0x60000020);
        Section(0x1B0, 0x2000, 0x800, 0x100, 0x40000040);
        Section(0x1D8, 0x3000, 0x900, 0x100, 0x40000040);
        Record(0x800, 0x1000, 0x1010, 0x3000);
        Record(0x80C, 0x1100, 0x1120, 0x3008);
        Record(0x818, 0x1300, 0x1320, 0x3000);
        Record(0x824, 0x1400, 0x1464, 0x3010);
        Convert.FromHexString("01040100044200000104010004620000010A04000A3406000A320670").CopyTo(image, 0x900);
        return image;

        void Section(int at, uint rva, uint raw, uint size, uint flags)
        {
            U32(image, at + 8, size); U32(image, at + 12, rva); U32(image, at + 16, size);
            U32(image, at + 20, raw); U32(image, at + 36, flags);
        }
        void Record(int at, uint start, uint end, uint unwind)
        { U32(image, at, start); U32(image, at + 4, end); U32(image, at + 8, unwind); }
    }

    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
