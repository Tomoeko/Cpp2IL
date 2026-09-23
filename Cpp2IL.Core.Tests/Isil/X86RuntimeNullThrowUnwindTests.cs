using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86RuntimeNullThrowUnwindTests
{
    private const ulong ImageBase = 0x180000000;
    private static X86RuntimeNullThrowProof.Region[] Regions =>
    [
        new(ImageBase + 0x1000, ImageBase + 0x1009, X86RuntimeNullThrowProof.Frame.Stack28),
        new(ImageBase + 0x1100, ImageBase + 0x1116, X86RuntimeNullThrowProof.Frame.Stack38),
        new(ImageBase + 0x1200, ImageBase + 0x120D, X86RuntimeNullThrowProof.Frame.Leaf),
        new(ImageBase + 0x1300, ImageBase + 0x1313, X86RuntimeNullThrowProof.Frame.Stack28),
        new(ImageBase + 0x1400, ImageBase + 0x1464, X86RuntimeNullThrowProof.Frame.SavedRbxRdiStack20),
    ];

    [Test]
    public void RequiresExactOrdinaryUnwindAndPermitsTheSeparatelyProvedLeafWithoutARecord()
        => Assert.That(Allows(Image(), Regions), Is.True);

    [TestCase(1)] // language-specific exception handler
    [TestCase(2)] // unwind/termination handler
    [TestCase(3)]
    [TestCase(4)] // chained unwind may ultimately reach a handler
    [TestCase(5)]
    [TestCase(8)] // reserved/unknown flags
    [TestCase(16)]
    public void MatchingNativeCallInstructionsDoNotEraseHiddenHandlers(int flags)
    {
        var image = Image();
        image[0x900] = (byte)(1 | flags << 3);
        Assert.That(Allows(image, Regions), Is.False);
    }

    [TestCase(0x900)]
    [TestCase(0x908)]
    [TestCase(0x910)]
    public void ChecksEveryCandidateFunctionIncludingItsFactory(int header)
    {
        var image = Image();
        image[header] = 9;
        Assert.That(Allows(image, Regions), Is.False);
    }

    [TestCase("missing-nonleaf")]
    [TestCase("interior-entry")]
    [TestCase("short-region")]
    [TestCase("incorrect-allocate")]
    [TestCase("incorrect-save-offset")]
    [TestCase("incorrect-restore-order")]
    public void UnknownOrInconsistentNativeUnwindEvidenceRejects(string defect)
    {
        var image = Image();
        switch (defect)
        {
            case "missing-nonleaf": U32(image, 0x124, 36); break;
            case "interior-entry": U32(image, 0x800, 0xFFF); break;
            case "short-region": U32(image, 0x804, 0x1008); break;
            case "incorrect-allocate": image[0x905] = 0x32; break;
            case "incorrect-save-offset": image[0x916] = 7; break;
            case "incorrect-restore-order": Convert.FromHexString("0A32067005340600").CopyTo(image, 0x914); break;
        }
        Assert.That(Allows(image, Regions), Is.False);
    }

    [Test]
    public void ALeafCoveredByAHandlerRegionIsNotMistakenForMissingUnwind()
    {
        var image = Image();
        U32(image, 0x80C, 0x1200);
        U32(image, 0x810, 0x1210);
        U32(image, 0x814, 0x3020);
        image[0x920] = 9;
        Assert.That(Allows(image, [Regions[2]]), Is.False);
        image[0x920] = 1;
        Assert.That(Allows(image, [Regions[2]]), Is.True);
    }

    [Test]
    public void ASpanCrossingFunctionBoundariesCannotBorrowTheFirstUnwindRecord()
    {
        var image = Image();
        Assert.That(Allows(image,
            [new(ImageBase + 0x1000, ImageBase + 0x1120, X86RuntimeNullThrowProof.Frame.Stack28)]), Is.False);
        Assert.That(Allows(image,
            [new(ImageBase - 1, ImageBase + 9, X86RuntimeNullThrowProof.Frame.Leaf)]), Is.False);
        Assert.That(Allows(image, []), Is.False);
        Assert.That(Allows(image.AsSpan(0, 0x904), Regions), Is.False);
    }

    [Test]
    public void GenericUnwindMatcherRequiresExactEntryExtentHeaderAndOperationBytes()
    {
        var index = X64UnwindProof.Parse(Image())!;
        Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1009, 4, 0, new byte[] { 4, 0x42 }), Is.True);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1004, ImageBase + 0x1009, 4, 0, new byte[] { 4, 0x42 }), Is.False);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1020, 4, 0, new byte[] { 4, 0x42 }), Is.False);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1009, 3, 0, new byte[] { 4, 0x42 }), Is.False);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1009, 4, 5, new byte[] { 4, 0x42 }), Is.False);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1009, 4, 0, new byte[] { 4, 0x32 }), Is.False);
    }

    [Test]
    public void HandlerEvidencePreservesTheNativeRejectionAndExposesOnlyMappedStructure()
    {
        var image = Image();
        image[0x900] = 1 | 3 << 3; // version 1, EHANDLER and UHANDLER
        U32(image, 0x908, 0x1300); // executable, file-backed handler entry
        image[0x90C] = 0x38; // first byte of language-specific data; not interpreted here
        var index = X64UnwindProof.Parse(image)!;
        var entry = ImageBase + 0x1000;
        var handler = index.GetHandler(entry);
        Assert.That(handler, Is.EqualTo(new X64UnwindProof.HandlerInfo(
            entry, ImageBase + 0x1010, 3, ImageBase + 0x1300, ImageBase + 0x300C)));
        Assert.That(index.MapReadOnlyData(handler!.Value.HandlerDataAddress, 1), Is.EqualTo(0x90C));
        Assert.That(index.GetHandler(entry + 1), Is.Null);
        Assert.That(index.GetHandler(ImageBase + 0x1100), Is.Null);
        Assert.That(index.ClassifySpan(entry, entry + 1).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.MatchesUnwind(entry, entry + 9, 4, 0, new byte[] { 4, 0x42 }), Is.False);
    }

    [TestCase(0x3000U)] // handler pointer into non-executable data
    [TestCase(0x4000U)] // handler pointer outside the image
    public void InvalidHandlerPointersCannotProvideHandlerEvidence(uint invalidHandler)
    {
        var image = Image();
        image[0x900] = 1 | 3 << 3;
        U32(image, 0x908, invalidHandler);
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.GetHandler(ImageBase + 0x1000), Is.Null);
        Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [Test]
    public void MutableLanguageHandlerDataCannotBeUsedAsImmutableEvidence()
    {
        var image = Image();
        image[0x900] = 1 | 3 << 3;
        U32(image, 0x908, 0x1300);
        U32(image, 0x1FC, 0xC0000040); // the handler data section is writable
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.GetHandler(ImageBase + 0x1000), Is.Null);
        Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [TestCase("System")]
    [TestCase("NullReferenceException")]
    public void OnlyCompleteReadOnlyAsciiTypeNameLiteralsCanBindTheRuntimeOperation(string name)
    {
        var image = Image();
        System.Text.Encoding.ASCII.GetBytes(name + "\0").CopyTo(image, 0x980);
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(X86RuntimeNullThrowProof.ReadReadOnlyName(image, index, ImageBase + 0x3080), Is.EqualTo(name));
        Assert.That(index.MapReadOnlyData(ImageBase + 0x3080, (uint)name.Length + 1), Is.EqualTo(0x980));
    }

    [TestCase("writable")]
    [TestCase("executable")]
    [TestCase("unreadable")]
    [TestCase("unmapped")]
    [TestCase("cross-section")]
    [TestCase("unbacked")]
    [TestCase("unterminated")]
    [TestCase("non-ascii")]
    public void MutableOrPartlyMappedLiteralBytesCannotProvideTypeIdentity(string defect)
    {
        var image = Image();
        Array.Resize(ref image, image.Length + 16);
        var address = ImageBase + 0x3080;
        System.Text.Encoding.ASCII.GetBytes("System\0").CopyTo(image, 0x980);
        switch (defect)
        {
            case "writable": U32(image, 0x1FC, 0xC0000040); break;
            case "executable": U32(image, 0x1FC, 0x60000020); break;
            case "unreadable": U32(image, 0x1FC, 0x40); break;
            case "unmapped": address = ImageBase + 0x3200; break;
            case "cross-section": address = ImageBase + 0x30FC; System.Text.Encoding.ASCII.GetBytes("System\0").CopyTo(image, 0x9FC); break;
            case "unbacked": U32(image, 0x1E8, 0x84); break;
            case "unterminated": image.AsSpan(0x980, 65).Fill((byte)'A'); break;
            case "non-ascii": image[0x981] = 0x80; break;
        }
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index, Is.Not.Null);
        Assert.That(X86RuntimeNullThrowProof.ReadReadOnlyName(image, index, address), Is.Null);
    }

    private static bool Allows(ReadOnlySpan<byte> image, IReadOnlyList<X86RuntimeNullThrowProof.Region> regions)
        => X86RuntimeNullThrowProof.AllowsUnwind(X64UnwindProof.Parse(image), regions);

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
