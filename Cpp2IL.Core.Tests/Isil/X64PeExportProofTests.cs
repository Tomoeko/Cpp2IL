using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64PeExportProofTests
{
    private const ulong ImageBase = 0x180000000;

    [Test]
    public void OrdinaryExportUsesAnUnbiasedOrdinalAndAnExecutableTarget()
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe)!;
        Assert.Multiple(() =>
        {
            Assert.That(X64PeExportProof.Find(pe, index, "Anchor"), Is.EqualTo(ImageBase + 0x1100));
            Assert.That(X64PeExportProof.Find(pe, index, "Second"), Is.EqualTo(ImageBase + 0x1110));
            Assert.That(X64PeExportProof.Find(pe, index, "Missing"), Is.Zero);
            Assert.That(X64PeExportProof.Find(pe, index, "anchor"), Is.Zero);
        });
    }

    [TestCase("function-entry")]
    [TestCase("function-table")]
    [TestCase("ordinal-entry")]
    [TestCase("ordinal-table")]
    public void FreshResolutionDoesNotReuseCachedFunctionOrOrdinalArrays(string mutation)
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe)!;
        Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("Anchor"), Is.EqualTo(ImageBase + 0x1100));
        switch (mutation)
        {
            case "function-entry": U32(bytes, 0x500, 0x1110); break;
            case "function-table": U32(bytes, 0x41C, 0x20B0); break;
            case "ordinal-entry": U16(bytes, 0x438, 1); break;
            case "ordinal-table": U32(bytes, 0x424, 0x20A0); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Multiple(() =>
        {
            Assert.That(X64UnwindProof.ForBinary(pe), Is.SameAs(index),
                "Export contents are read by their own proof, outside cached structural input.");
            Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("Anchor"), Is.EqualTo(ImageBase + 0x1100),
                "This positive stale-cache control distinguishes a fresh anchor from the old PE API.");
            Assert.That(X64PeExportProof.Find(pe, index, "Anchor"), Is.EqualTo(ImageBase + 0x1110));
        });
    }

    [TestCase("name-pointer")]
    [TestCase("name-table")]
    public void FreshNamePointersCanInvalidateAnOtherwiseCachedAnchor(string mutation)
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe)!;
        Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("Anchor"), Is.EqualTo(ImageBase + 0x1100));
        U32(bytes, mutation == "name-pointer" ? 0x430 : 0x420,
            mutation == "name-pointer" ? 0x2090U : 0x20C0U);
        Assert.Multiple(() =>
        {
            Assert.That(X64UnwindProof.ForBinary(pe), Is.SameAs(index));
            Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("Anchor"), Is.EqualTo(ImageBase + 0x1100));
            Assert.That(X64PeExportProof.Find(pe, index, "Anchor"), Is.Zero);
        });
    }

    [TestCase("duplicate-name")]
    [TestCase("unsorted-name")]
    [TestCase("ordinal-range")]
    [TestCase("function-hole")]
    [TestCase("forwarder")]
    [TestCase("data-target")]
    [TestCase("unmapped-target")]
    [TestCase("unmapped-function-table")]
    [TestCase("unmapped-name-table")]
    [TestCase("unmapped-ordinal-table")]
    [TestCase("unterminated-name")]
    [TestCase("non-ascii-name")]
    [TestCase("name-limit")]
    [TestCase("function-limit")]
    [TestCase("directory-size")]
    [TestCase("directory-flags")]
    [TestCase("writable-data")]
    [TestCase("executable-data")]
    public void MalformedAmbiguousForwardedAndMutableEvidenceCannotAnchorANativeHelper(string mutation)
    {
        var bytes = Image();
        switch (mutation)
        {
            case "duplicate-name": U32(bytes, 0x434, 0x2070); break;
            case "unsorted-name": U32(bytes, 0x430, 0x2080); U32(bytes, 0x434, 0x2070); break;
            case "ordinal-range": U16(bytes, 0x438, 2); break;
            case "function-hole": U32(bytes, 0x500, 0); break;
            case "forwarder": U32(bytes, 0x500, 0x2090); break;
            case "data-target": U32(bytes, 0x500, 0x2500); break;
            case "unmapped-target": U32(bytes, 0x500, 0x4000); break;
            case "unmapped-function-table": U32(bytes, 0x41C, 0x25FC); break;
            case "unmapped-name-table": U32(bytes, 0x420, 0x25FC); break;
            case "unmapped-ordinal-table": U32(bytes, 0x424, 0x25FE); break;
            case "unterminated-name":
                bytes.AsSpan(0x900, 0x100).Fill((byte)'A'); U32(bytes, 0x430, 0x2500); break;
            case "non-ascii-name": bytes[0x470] = 0x80; break;
            case "name-limit": U32(bytes, 0x418, 65_537); break;
            case "function-limit": U32(bytes, 0x414, 1_048_577); break;
            case "directory-size": U32(bytes, 0x10C, 0x601); break;
            case "directory-flags": U32(bytes, 0x400, 1); break;
            case "writable-data": U32(bytes, 0x1D4, 0xC0000040); break;
            case "executable-data": U32(bytes, 0x1D4, 0x60000040); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe);
        Assert.That(index, Is.Not.Null, "The synthetic structural PE remains parseable.");
        Assert.That(X64PeExportProof.Find(pe, index!, "Anchor"), Is.Zero);
    }

    [TestCase(0x2000, 40)] // Directory, including function/name/ordinal array RVAs.
    [TestCase(0x2030, 8)] // Both name pointers establish sorted unique selection.
    [TestCase(0x2038, 4)] // Both unbiased ordinal entries are validated.
    [TestCase(0x2070, 7)] // Requested name, including its terminating zero.
    [TestCase(0x2080, 7)] // Other name also establishes uniqueness/order.
    [TestCase(0x2100, 4)] // Selected function RVA.
    public void LoadedOverlapInvalidatesUnchangedFileExportEvidence(int rva, int length)
    {
        foreach (var displacement in new[] { -7, 0, length - 1 })
        {
            var bytes = Image();
            Relocate(bytes, checked((uint)(rva + displacement)));
            using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            using var pe = new PE(stream);
            var index = X64UnwindProof.ForBinary(pe)!;
            Assert.Multiple(() =>
            {
                Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("Anchor"), Is.EqualTo(ImageBase + 0x1100),
                    "File export bytes are unchanged; only the loader relocation entries differ.");
                Assert.That(index.IsUnaffectedByBaseRelocationRva((uint)rva, (uint)length), Is.False);
                Assert.That(X64PeExportProof.Find(pe, index, "Anchor"), Is.Zero);
            });
        }
    }

    [Test]
    public void RelocationOutsideConsumedFunctionEntryDoesNotChangeTheSelectedExport()
    {
        var bytes = Image();
        Relocate(bytes, 0x2104);
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        using var pe = new PE(stream);
        Assert.That(X64PeExportProof.Find(pe, X64UnwindProof.ForBinary(pe)!, "Anchor"),
            Is.EqualTo(ImageBase + 0x1100));
    }

    [Test]
    public void StructuralHeaderChangesAndLoadedHeaderOverlapRemainUnproved()
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe)!;
        U32(bytes, 0x108, 0x2010);
        Assert.That(index.HasUnchangedInput(pe.GetRawBinaryContent()), Is.False);
        Assert.That(X64PeExportProof.Find(pe, index, "Anchor"), Is.Zero);

        var relocated = Image();
        Relocate(relocated, 0x108);
        using var changedStream = new MemoryStream(relocated, 0, relocated.Length, writable: false, publiclyVisible: true);
        using var changed = new PE(changedStream);
        var altered = X64UnwindProof.ForBinary(changed)!;
        Assert.That(X64PeExportProof.Find(changed, altered, "Anchor"), Is.Zero,
            "A byte-identical file header does not establish its loaded export directory.");
    }

    [Test]
    public void ExactPlayerExportAnchorRejectsItsStaleCachedAddress()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_PARAMETER_CLASS_TEST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_PARAMETER_CLASS_TEST_FIXTURE_INPUT to the neutral exact player input.");
        var bytes = File.ReadAllBytes(Path.Combine(directory!, "GameAssembly.dll"));
        using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true);
        using var pe = new PE(stream);
        var index = X64UnwindProof.ForBinary(pe)!;
        const string name = "il2cpp_class_from_type";
        var cached = pe.GetVirtualAddressOfExportedFunctionByName(name);
        Assert.That(cached, Is.Not.Zero);
        Assert.That(X64PeExportProof.Find(pe, index, name), Is.EqualTo(cached));
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x3C, 4)));
        var directoryRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 24 + 112, 4));
        var raw = index.MapReadOnlyRva(directoryRva, 40);
        var functionsRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(raw + 28, 4));
        var functionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(raw + 20, 4));
        var functions = index.MapReadOnlyRva(functionsRva, checked(functionCount * 4));
        var nameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(raw + 24, 4));
        var namesRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(raw + 32, 4));
        var ordinalsRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(raw + 36, 4));
        var names = index.MapReadOnlyRva(namesRva, checked(nameCount * 4));
        var ordinals = index.MapReadOnlyRva(ordinalsRva, checked(nameCount * 2));
        var selected = -1;
        for (var slot = 0; slot < nameCount; slot++)
        {
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(names + slot * 4, 4));
            var nameRaw = index.MapReadOnlyRva(nameRva, 1);
            var end = Array.IndexOf(bytes, (byte)0, nameRaw);
            if (Encoding.ASCII.GetString(bytes.AsSpan(nameRaw, end - nameRaw)) != name)
                continue;
            var ordinal = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ordinals + slot * 2, 2));
            selected = functions + ordinal * 4;
            break;
        }
        Assert.That(selected, Is.GreaterThanOrEqualTo(0));
        U32(bytes, selected, 0);
        Assert.Multiple(() =>
        {
            Assert.That(X64UnwindProof.ForBinary(pe), Is.SameAs(index));
            Assert.That(pe.GetVirtualAddressOfExportedFunctionByName(name), Is.EqualTo(cached));
            Assert.That(X64PeExportProof.Find(pe, index, name), Is.Zero,
                "Cached runtime export identity must not survive a changed current address table.");
        });
    }

    private static void Relocate(byte[] image, uint rva)
    {
        U32(image, 0xB00, rva & ~0xFFFU);
        U16(image, 0xB08, (ushort)(0xA000U | (rva & 0xFFFU)));
    }

    private static void U16(byte[] bytes, int at, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);

    private static void U32(byte[] bytes, int at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);

    private static byte[] Image()
    {
        // Synthetic PE32+ sections, export arrays, names and one unwind record.
        // No player bytes, linked runtime addresses or identifying fingerprints.
        var image = new byte[0x1000];
        U16(image, 0, 0x5A4D); U32(image, 0x3C, 0x80);
        U32(image, 0x80, 0x4550); U16(image, 0x84, 0x8664); U16(image, 0x86, 3); U16(image, 0x94, 0xF0);
        U16(image, 0x98, 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xB0), ImageBase);
        U32(image, 0xD0, 0x4000); U32(image, 0x104, 16);
        U32(image, 0x108, 0x2000); U32(image, 0x10C, 0x180);
        U32(image, 0x120, 0x2400); U32(image, 0x124, 12);
        U32(image, 0x130, 0x3100); U32(image, 0x134, 12);
        Section(0x188, 0x1000, 0x200, 0x200, 0x60000020);
        Section(0x1B0, 0x2000, 0x400, 0x600, 0x40000040);
        Section(0x1D8, 0x3000, 0xA00, 0x600, 0x40000040);
        image[0x300] = image[0x310] = 0xC3;
        U32(image, 0x800, 0x1000); U32(image, 0x804, 0x1010); U32(image, 0x808, 0x3000);
        image[0xA00] = 1;
        U32(image, 0xB00, 0x2000); U32(image, 0xB04, 12);
        U32(image, 0x40C, 0x2050); U32(image, 0x410, 1);
        U32(image, 0x414, 2); U32(image, 0x418, 2);
        U32(image, 0x41C, 0x2100); U32(image, 0x420, 0x2030); U32(image, 0x424, 0x2038);
        U32(image, 0x500, 0x1100); U32(image, 0x504, 0x1110);
        U32(image, 0x430, 0x2070); U32(image, 0x434, 0x2080);
        U16(image, 0x438, 0); U16(image, 0x43A, 1);
        String(0x450, "Proof.dll"); String(0x470, "Anchor"); String(0x480, "Second"); String(0x490, "Absent");
        U16(image, 0x4A0, 1); U16(image, 0x4A2, 0);
        U32(image, 0x4B0, 0x1110); U32(image, 0x4B4, 0x1100);
        U32(image, 0x4C0, 0x2090); U32(image, 0x4C4, 0x2080);
        return image;

        void Section(int at, uint rva, uint raw, uint size, uint flags)
        {
            U32(image, at + 8, size); U32(image, at + 12, rva); U32(image, at + 16, size);
            U32(image, at + 20, raw); U32(image, at + 36, flags);
        }
        void String(int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(image.AsSpan(at));
    }
}
