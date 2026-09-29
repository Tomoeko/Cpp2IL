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
    public void FileBackedZeroProofExcludesLoaderRelocationsAndMalformedTables()
    {
        var image = Image();
        Assert.That(X64UnwindProof.Parse(image)!.IsUnaffectedByBaseRelocationRva(0x2004), Is.True);

        U32(image, 0x130, 0x3080); // Base relocation directory in file-backed data.
        U32(image, 0x134, 12);
        U32(image, 0x980, 0x2000);
        U32(image, 0x984, 12);
        U16(image, 0x988, 0xA004); // DIR64 covers 0x2004 through 0x200B.
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.IsUnaffectedByBaseRelocationRva(0x2003), Is.True);
        Assert.That(index.IsUnaffectedByBaseRelocationRva(0x2004), Is.False);
        Assert.That(index.IsUnaffectedByBaseRelocationRva(0x200B), Is.False);
        Assert.That(index.IsUnaffectedByBaseRelocationRva(0x200C), Is.True);

        U16(image, 0x988, 0x3004); // Unknown relocation semantics are not guessed.
        Assert.That(X64UnwindProof.Parse(image)!.IsUnaffectedByBaseRelocationRva(0x200C), Is.False);
        U16(image, 0x988, 0xA004);
        U32(image, 0x984, 14); // Block extends beyond its directory.
        Assert.That(X64UnwindProof.Parse(image)!.IsUnaffectedByBaseRelocationRva(0x200C), Is.False);
    }

    [TestCase(0x1FF9)] // Only the last byte of a preceding DIR64 overlaps .pdata.
    [TestCase(0x2000)]
    [TestCase(0x202F)] // The entire table, including unrelated records, establishes gaps.
    [TestCase(0x003C)] // PE header location.
    [TestCase(0x0120)] // Exception directory location and size.
    [TestCase(0x01F9)] // Section characteristics and mapping.
    public void RelocatedDirectoryEvidenceCannotEstablishFunctionsOrAbsentLeaves(int target)
    {
        var image = Image();
        Relocate(image, (uint)target);
        var index = X64UnwindProof.Parse(image)!;
        Assert.Multiple(() =>
        {
            Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
            Assert.That(index.ClassifySpan(ImageBase + 0x1200, ImageBase + 0x1201).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
            Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1000, ImageBase + 0x1001), Is.False);
            Assert.That(index.GetHandler(ImageBase + 0x1000), Is.Null);
        });
    }

    [TestCase("unknown-kind")]
    [TestCase("truncated-block")]
    [TestCase("self-relocation")]
    [TestCase("missing-directory-width")]
    public void UnauthenticatedRelocationTablesCannotEstablishAbsentLeaves(string defect)
    {
        var image = Image();
        Relocate(image, 0x3070);
        switch (defect)
        {
            case "unknown-kind": U16(image, 0x988, 0x3070); break;
            case "truncated-block": U32(image, 0x984, 14); break;
            case "self-relocation": Relocate(image, 0x3079); break;
            case "missing-directory-width": U32(image, 0x104, 5); break;
        }
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.ClassifySpan(ImageBase + 0x1200, ImageBase + 0x1201).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.IsUnaffectedByBaseRelocationRva(0x1000, 1), Is.False);
    }

    [TestCase(0x2FF9)] // A preceding DIR64 overlaps the first header byte.
    [TestCase(0x3000)]
    [TestCase(0x3004)] // Unwind operation.
    [TestCase(0x3007)] // Aligned code-array padding is part of the record.
    public void RelocatedUnwindRecordsRetainOnlyTheirAuthenticatedFunctionBoundary(int target)
    {
        var image = Image();
        Relocate(image, (uint)target);
        var index = X64UnwindProof.Parse(image)!;
        Assert.Multiple(() =>
        {
            Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
            Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1000, ImageBase + 0x1001), Is.True);
            Assert.That(index.MatchesUnwind(ImageBase + 0x1000, ImageBase + 0x1010,
                4, 0, new byte[] { 4, 0x42 }), Is.False);
            Assert.That(index.ClassifySpan(ImageBase + 0x1200, ImageBase + 0x1201).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
        });
    }

    [TestCase(0x3000)] // Primary unwind header.
    [TestCase(0x300C)] // Intermediate save operation.
    [TestCase(0x301B)] // Last byte of the intermediate chain tuple.
    [TestCase(0x302C)] // Final chain's unwind RVA.
    public void ChainedEvidenceRequiresEveryUnwindRecordAndTupleToRemainUnrelocated(int target)
    {
        var image = NestedChainImage();
        Relocate(image, (uint)target);
        Assert.That(Classify(image, 0x1300, 0x1301).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [Test]
    public void RelocationRangeProofChecksInteriorOverlapsAndExactBoundaries()
    {
        var image = Image();
        Relocate(image, 0x3060, 0x3064, 0x306C); // Overlapping and adjacent spans merge.
        var index = X64UnwindProof.Parse(image)!;
        Assert.Multiple(() =>
        {
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3050, 0x10), Is.True);
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3050, 0x30), Is.False);
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3074, 1), Is.True);
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3073, 1), Is.False);
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3FFF, 2), Is.False);
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x1000, 0), Is.False);
            Assert.That(index.IsUnaffectedByBaseRelocation(ImageBase - 1, 1), Is.False);
            Assert.That(index.IsUnaffectedByBaseRelocation(ulong.MaxValue, 1), Is.False);
        });
    }

    [TestCase("none", false)]
    [TestCase("exact", true)]
    [TestCase("adjacent", true)]
    [TestCase("unrelated", false)]
    [TestCase("duplicate", false)]
    [TestCase("partial-before", false)]
    [TestCase("partial-after", false)]
    [TestCase("hidden-overlap", false)]
    [TestCase("overlap-before", false)]
    public void PointerRelocationPreservesWholeSlotsExactlyOnce(string scenario, bool expected)
    {
        var image = Image();
        uint[] targets = scenario switch
        {
            "none" => [],
            "exact" => [0x3060],
            "adjacent" => [0x3058, 0x3060, 0x3068],
            "unrelated" => [0x3040],
            "duplicate" => [0x3060, 0x3060],
            "partial-before" => [0x305C],
            "partial-after" => [0x3064],
            "hidden-overlap" => [0x3060, 0x3064],
            "overlap-before" => [0x305C, 0x3060],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (targets.Length != 0)
            Relocate(image, targets);
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.HasCanonicalPointerRelocation(ImageBase + 0x3060), Is.EqualTo(expected));
        if (targets.Length != 0 && scenario != "unrelated")
            Assert.That(index.IsUnaffectedByBaseRelocationRva(0x3060, 8), Is.False,
                "An intersecting relocation is safe only through the exact pointer-slot proof.");
    }

    [Test]
    public void PointerRelocationCannotAuthenticateAnOutOfImageOrOverflowingSlot()
    {
        var index = X64UnwindProof.Parse(Image())!;
        Assert.Multiple(() =>
        {
            Assert.That(index.HasCanonicalPointerRelocation(ImageBase - 1), Is.False);
            Assert.That(index.HasCanonicalPointerRelocation(ImageBase + 0x3FF9), Is.False);
            Assert.That(index.HasCanonicalPointerRelocation(ImageBase + 0x4000), Is.False);
            Assert.That(index.HasCanonicalPointerRelocation(ulong.MaxValue), Is.False);
        });
    }

    [TestCase(0x3008)] // Immediately after the first record; only its neighbor changes.
    [TestCase(0x3070)] // Unconsumed data beside the records.
    public void UnrelatedRelocationsDoNotDiscardAuthenticatedUnwindEvidence(int target)
    {
        var image = Image();
        Relocate(image, (uint)target);
        Assert.That(Classify(image, 0x1000, 0x1001).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
    }

    [TestCase(0x300C)] // Function-info pointer.
    [TestCase(0x3020)] // Function-info header.
    [TestCase(0x3028)] // IP-map RVA's final byte.
    [TestCase(0x3030)] // Compressed unwind map.
    [TestCase(0x3044)] // Handler-array RVA.
    [TestCase(0x3051)] // Compressed IP delta.
    [TestCase(0x3062)] // Catch funclet RVA.
    [TestCase(0x3066)] // Compressed continuation offset.
    public void LanguageSpecificMapsRequireEveryConsumedByteToRemainUnrelocated(int target)
    {
        var image = Eh4Image();
        Relocate(image, (uint)target);
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000);
        Assert.That(region, Is.Not.Null, "The unwind handler RVA remains authenticated.");
        Assert.That(X64Eh4MapProof.Parse(image, index, region!.Value), Is.Null);
    }

    [Test]
    public void AdjacentUnconsumedLanguageDataDoesNotInvalidateTheParsedMap()
    {
        var image = Eh4Image();
        Relocate(image, 0x3067);
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(X64Eh4MapProof.Parse(image, index,
            index.GetHandler(ImageBase + 0x1000)!.Value), Is.Not.Null);
    }

    [TestCase(0x3005)] // Tail overlap includes the handler RVA.
    [TestCase(0x3008)]
    [TestCase(0x300B)]
    public void RelocatedHandlerRvasCannotEstablishALanguageHandler(int target)
    {
        var image = Eh4Image();
        Relocate(image, (uint)target);
        Assert.That(X64UnwindProof.Parse(image)!.GetHandler(ImageBase + 0x1000), Is.Null);
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

    [Test]
    public void UnrelatedFunctionBoundaryDoesNotRequireItsUnwindSemantics()
    {
        var image = Image();
        image[0x900] = 2; // Unsupported unwind version in the next function.
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.ClassifySpan(ImageBase + 0x1000,
            ImageBase + 0x1001).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
        Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1000,
            ImageBase + 0x1001), Is.True);
        Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1001,
            ImageBase + 0x1002), Is.False);
        Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1000,
            ImageBase + 0x1101), Is.False);
        Assert.That(index.HasFunctionEntryAt(ImageBase + 0x1200,
            ImageBase + 0x1201), Is.False);
    }

    [Test]
    public void ExactHandlerFreeChainNamesItsPrimaryFunction()
    {
        var index = X64UnwindProof.Parse(ChainImage())!;
        var primary = index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001);
        var fragment = index.ClassifySpan(ImageBase + 0x1100, ImageBase + 0x1101);
        Assert.That(primary.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That(fragment.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That(fragment.RootStart, Is.EqualTo(primary.RootStart));
        Assert.That(fragment.Start, Is.EqualTo(ImageBase + 0x1100));
        Assert.That(index.GetHandler(ImageBase + 0x1100), Is.Null);
        Assert.That(index.MatchesUnwind(ImageBase + 0x1100, ImageBase + 0x1120, 0, 0,
            new byte[] { 0, 0x74, 0x16, 0 }), Is.False);
    }

    [Test]
    public void EpilogFragmentWithNoAdditionalUnwindCodesCanShareItsPrimary()
    {
        var image = ChainImage();
        image[0x90A] = 0;
        U32(image, 0x90C, 0x1000);
        U32(image, 0x910, 0x1010);
        U32(image, 0x914, 0x3000);
        var index = X64UnwindProof.Parse(image)!;
        Assert.That(index.ClassifySpan(ImageBase + 0x1100, ImageBase + 0x1101).RootStart,
            Is.EqualTo(ImageBase + 0x1000));
    }

    [Test]
    public void NestedHandlerFreeChainsReachTheirPrimary()
    {
        var image = NestedChainImage();
        var index = X64UnwindProof.Parse(image)!;
        var middle = index.ClassifySpan(ImageBase + 0x1100, ImageBase + 0x1101);
        var final = index.ClassifySpan(ImageBase + 0x1300, ImageBase + 0x1301);
        Assert.Multiple(() =>
        {
            Assert.That(middle.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
            Assert.That(final.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
            Assert.That(middle.RootStart, Is.EqualTo(ImageBase + 0x1000));
            Assert.That(final.RootStart, Is.EqualTo(ImageBase + 0x1000));
            Assert.That(final.Start, Is.EqualTo(ImageBase + 0x1300));
        });
    }

    [TestCase("wrong-middle-end")]
    [TestCase("wrong-middle-unwind")]
    [TestCase("cycle")]
    [TestCase("handler-middle")]
    [TestCase("frame-mismatch")]
    public void NestedChainsRejectBrokenLinks(string defect)
    {
        var image = NestedChainImage();
        switch (defect)
        {
            case "wrong-middle-end": U32(image, 0x928, 0x111F); break;
            case "wrong-middle-unwind": U32(image, 0x92C, 0x3010); break;
            case "cycle":
                U32(image, 0x910, 0x1300);
                U32(image, 0x914, 0x1320);
                U32(image, 0x918, 0x3020);
                break;
            case "handler-middle":
                image[0x908] = 1 | 1 << 3;
                U32(image, 0x910, 0x1400);
                break;
            case "frame-mismatch": image[0x923] = 5; break;
        }
        Assert.That(Classify(image, 0x1300, 0x1301).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [TestCase("wrong-root-start")]
    [TestCase("wrong-root-end")]
    [TestCase("wrong-root-unwind")]
    [TestCase("self-chain")]
    [TestCase("handler-root")]
    [TestCase("unknown-save")]
    [TestCase("truncated-chain")]
    [TestCase("frame-mismatch")]
    public void UnprovedChainsStayUnsupported(string defect)
    {
        var image = ChainImage();
        switch (defect)
        {
            case "wrong-root-start": U32(image, 0x910, 0x1001); break;
            case "wrong-root-end": U32(image, 0x914, 0x100F); break;
            case "wrong-root-unwind": U32(image, 0x918, 0x3040); break;
            case "self-chain": U32(image, 0x910, 0x1100); U32(image, 0x914, 0x1120); U32(image, 0x918, 0x3008); break;
            case "handler-root":
                U32(image, 0x808, 0x3040); U32(image, 0x918, 0x3040);
                image[0x940] = 1 | 3 << 3;
                U32(image, 0x944, 0x1300);
                image[0x948] = 0;
                break;
            case "unknown-save": image[0x90D] = 0x70; break;
            case "truncated-chain": U32(image, 0x1E8, 0x18); break;
            case "frame-mismatch":
                image[0x909] = 1; image[0x90A] = 1; image[0x90B] = 5;
                image[0x90C] = 1; image[0x90D] = 3; // valid SET_FPREG with a different frame register
                break;
        }
        Assert.That(Classify(image, 0x1100, 0x1101).Kind, Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
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

    [Test]
    public void BoundedFrameHandlerMapsRemainStructuralEvidence()
    {
        var image = Eh4Image();
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000);
        Assert.That(region, Is.Not.Null);
        var map = X64Eh4MapProof.Parse(image, index, region!.Value);
        Assert.That(map, Is.Not.Null);
        Assert.That(map!.UnwindActions, Is.EqualTo(new[]
        {
            new X64Eh4MapProof.UnwindAction(1, 0, null, null, -1),
            new X64Eh4MapProof.UnwindAction(2, 0, null, null, -1),
        }));
        Assert.That(map.TryBlocks, Has.Count.EqualTo(1));
        Assert.That(map.TryBlocks[0].Handlers, Has.Count.EqualTo(1));
        Assert.That(map.TryBlocks[0].Handlers[0].FuncletRva, Is.EqualTo(0x1300));
        Assert.That(map.TryBlocks[0].Handlers[0].ContinuationRvas, Is.EqualTo(new uint[] { 0x1002 }));
        Assert.That(map.IpStates, Is.EqualTo(new[] { new X64Eh4MapProof.IpState(0x1002, 0) }));
        Assert.That(X64Eh4MapProof.Parse(image, index, region.Value with { End = region.Value.Start }), Is.Null);
        Assert.That(index.ClassifySpan(ImageBase + 0x1000, ImageBase + 0x1001).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported));
    }

    [Test]
    public void UnwindOnlyCleanupMapsRemainSeparateFromManagedCatchAdmission()
    {
        var image = CleanupImage();
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000)!.Value;
        Assert.That(X64Eh4MapProof.Parse(image, index, region), Is.Null);
        var map = X64Eh4MapProof.ParseCleanup(image, index, region);
        Assert.That(map, Is.Not.Null);
        Assert.That(map!.UnwindActions, Is.EqualTo(new[]
        {
            new X64Eh4MapProof.UnwindAction(1, 1, 0x1300, 0x20, -1),
            new X64Eh4MapProof.UnwindAction(7, 0, null, null, -1)
        }));
        Assert.That(map.TryBlocks, Is.Empty);
        Assert.That(map.IpStates, Is.EqualTo(new[] { new X64Eh4MapProof.IpState(0x1002, 0) }));
        Assert.That(index.ClassifySpan(region.Start, region.Start + 1).Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.Unsupported),
            "A parsed cleanup map does not authorize ordinary managed lifting or authenticate its handler.");
    }

    [TestCase("exception-search")]
    [TestCase("catch-table")]
    [TestCase("catch-funclet")]
    [TestCase("separated")]
    [TestCase("noexcept")]
    [TestCase("missing-actions")]
    [TestCase("invalid-object-encoding")]
    [TestCase("bad-unwind-link")]
    [TestCase("nonexecutable-action")]
    [TestCase("out-of-range-ip")]
    public void UnsupportedOrMalformedCleanupMapsFailClosed(string defect)
    {
        var image = CleanupImage();
        switch (defect)
        {
            case "exception-search": image[0x900] = 1 | 3 << 3; break;
            case "catch-table":
                image[0x920] = 0x38;
                U32(image, 0x925, 0x3040);
                U32(image, 0x929, 0x3050);
                break;
            case "catch-funclet": image[0x920] = 0x29; break;
            case "separated": image[0x920] = 0x2A; break;
            case "noexcept": image[0x920] = 0x68; break;
            case "missing-actions": image[0x930] = 0; break;
            case "invalid-object-encoding": image[0x936] = 0x1F; break;
            case "bad-unwind-link": image[0x937] = 0x10; break;
            case "nonexecutable-action": U32(image, 0x932, 0x3040); break;
            case "out-of-range-ip": image[0x951] = 0x40; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000)!.Value;
        Assert.That(X64Eh4MapProof.ParseCleanup(image, index, region), Is.Null);
    }

    private static byte[] CleanupImage()
    {
        var image = Eh4Image();
        image[0x900] = 1 | 2 << 3;
        image[0x920] = 0x28;
        U32(image, 0x925, 0x3050);
        image[0x930] = 4;
        image[0x931] = 10;
        U32(image, 0x932, 0x1300);
        image[0x936] = 0x40;
        image[0x937] = 0x38;
        return image;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void FrameHandlerCompressedCountsUseTheirDeclaredWidth(int width)
    {
        var image = Eh4Image();
        image.AsSpan(0x930, 8).Clear();
        switch (width)
        {
            case 1: image[0x930] = 4; break;
            case 2: image[0x930] = 9; break;
            case 3: image[0x930] = 19; break;
            case 4: image[0x930] = 39; break;
            case 5: image[0x930] = 15; U32(image, 0x931, 2); break;
        }
        image[0x930 + width] = 8;
        image[0x931 + width] = 16;
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000)!.Value;
        Assert.That(X64Eh4MapProof.Parse(image, index, region)!.UnwindActions, Has.Count.EqualTo(2));
    }

    [TestCase("writable-maps")]
    [TestCase("bad-function-info")]
    [TestCase("unsupported-header")]
    [TestCase("oversized-count")]
    [TestCase("nonexecutable-funclet")]
    [TestCase("out-of-range-ip")]
    [TestCase("bad-unwind-link")]
    public void MalformedFrameHandlerMapsCannotEstablishStructure(string defect)
    {
        var image = Eh4Image();
        switch (defect)
        {
            case "writable-maps": U32(image, 0x1FC, 0xC0000040); break;
            case "bad-function-info": U32(image, 0x90C, 0x1300); break;
            case "unsupported-header": image[0x920] = 0x39; break;
            case "oversized-count":
                image[0x930] = 0x0F;
                U32(image, 0x931, 65537);
                break;
            case "nonexecutable-funclet": U32(image, 0x962, 0x3060); break;
            case "out-of-range-ip": image[0x951] = 0x40; break;
            case "bad-unwind-link": image[0x932] = 0; break;
        }
        var index = X64UnwindProof.Parse(image)!;
        var region = index.GetHandler(ImageBase + 0x1000);
        if (defect == "writable-maps")
            Assert.That(region, Is.Null);
        else
            Assert.That(X64Eh4MapProof.Parse(image, index, region!.Value), Is.Null);
    }

    private static byte[] Eh4Image()
    {
        var image = Image();
        image[0x900] = 1 | 3 << 3;
        U32(image, 0x908, 0x1300);
        U32(image, 0x90C, 0x3020); // handler data points to the frame-handler map
        image[0x920] = 0x38;
        U32(image, 0x921, 0x3030);
        U32(image, 0x925, 0x3040);
        U32(image, 0x929, 0x3050);
        image[0x930] = 4; image[0x931] = 8; image[0x932] = 16;
        image[0x940] = 2; image[0x941] = 0; image[0x942] = 0; image[0x943] = 2;
        U32(image, 0x944, 0x3060);
        image[0x950] = 2; image[0x951] = 4; image[0x952] = 2;
        image[0x960] = 2; image[0x961] = 0x10;
        U32(image, 0x962, 0x1300);
        image[0x966] = 4;
        return image;
    }

    private static byte[] ChainImage()
    {
        var image = Image();
        image[0x908] = 1 | 4 << 3;
        image[0x909] = 0;
        image[0x90A] = 2;
        image[0x90B] = 0;
        image[0x90C] = 0;
        image[0x90D] = 0x74; // SAVE_NONVOL RDI at fragment offset zero
        image[0x90E] = 0x16;
        image[0x90F] = 0;
        U32(image, 0x910, 0x1000);
        U32(image, 0x914, 0x1010);
        U32(image, 0x918, 0x3000);
        return image;
    }

    private static byte[] NestedChainImage()
    {
        var image = ChainImage();
        U32(image, 0x820, 0x3020);
        image[0x920] = 1 | 4 << 3;
        image[0x921] = 0;
        image[0x922] = 0;
        image[0x923] = 0;
        U32(image, 0x924, 0x1100);
        U32(image, 0x928, 0x1120);
        U32(image, 0x92C, 0x3008);
        return image;
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

    private static void Relocate(byte[] image, params uint[] targets)
    {
        // One synthetic block per target also exercises directory-order independence.
        U32(image, 0x130, 0x3080);
        U32(image, 0x134, checked((uint)targets.Length * 12));
        image.AsSpan(0x980).Clear();
        for (var index = 0; index < targets.Length; index++)
        {
            var at = 0x980 + index * 12;
            U32(image, at, targets[index] & ~0xFFFU);
            U32(image, at + 4, 12);
            U16(image, at + 8, (ushort)(0xA000 | targets[index] & 0xFFFU));
        }
    }

    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
