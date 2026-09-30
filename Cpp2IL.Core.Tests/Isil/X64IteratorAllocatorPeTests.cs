using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorAllocatorPeTests
{
    private const ulong ImageBase = 0x180000000;

    [Test]
    public void FileBackedExportWrapperAndTransparentStubProveTheAllocator()
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, false, true);
        using var pe = new PE(stream);
        Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, X64UnwindProof.ForBinary(pe)!,
            ImageBase + 0x1100), Is.True);
    }

    [TestCase(0x1005)] // Exported CALL displacement.
    [TestCase(0x101F)] // Exported wrapper's last padding byte.
    [TestCase(0x10F9)] // DIR64 starts before the stub but overlaps its first byte.
    [TestCase(0x1101)] // Codegen JMP displacement.
    [TestCase(0x110F)] // Codegen stub's last padding byte.
    [TestCase(0x2100)] // Selected export address-table entry.
    public void LoaderChangesInvalidateOtherwiseIdenticalFileEvidence(int rva)
    {
        var bytes = Image();
        Relocate(bytes, (uint)rva);
        using var stream = new MemoryStream(bytes, 0, bytes.Length, false, true);
        using var pe = new PE(stream);
        var unwind = X64UnwindProof.ForBinary(pe)!;
        Assert.Multiple(() =>
        {
            Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_object_new"),
                Is.EqualTo(ImageBase + 0x1000), "The unchanged file export is the positive control.");
            Assert.That(unwind.GetHandler(ImageBase + 0x1000), Is.Not.Null);
            Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, unwind, ImageBase + 0x1100), Is.False);
        });
    }

    [Test]
    public void UnrelatedRelocationDoesNotInvalidateTheAllocator()
    {
        var bytes = Image();
        Relocate(bytes, 0x2300);
        using var stream = new MemoryStream(bytes, 0, bytes.Length, false, true);
        using var pe = new PE(stream);
        Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, X64UnwindProof.ForBinary(pe)!,
            ImageBase + 0x1100), Is.True);
    }

    [Test]
    public void CachedExportCannotAuthenticateAChangedCurrentAddressTable()
    {
        var bytes = Image();
        using var stream = new MemoryStream(bytes, 0, bytes.Length, true, true);
        using var pe = new PE(stream);
        var unwind = X64UnwindProof.ForBinary(pe)!;
        Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_object_new"),
            Is.EqualTo(ImageBase + 0x1000));
        Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, unwind, ImageBase + 0x1100), Is.True);
        U32(bytes, 0x700, 0x1200);
        Assert.Multiple(() =>
        {
            Assert.That(pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_object_new"),
                Is.EqualTo(ImageBase + 0x1000), "The old PE export API retains the stale address.");
            Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, unwind, ImageBase + 0x1100), Is.False);
        });
    }

    [TestCase(0x2300)] // File-backed data is not callable code.
    [TestCase(0x3100)] // Outside the image.
    [TestCase(0x1000)] // The exception wrapper cannot be its own internal allocator.
    [TestCase(0x100B)] // An interior catch-return instruction is still the wrapper.
    [TestCase(0x101F)] // The wrapper's padding is not an internal callable allocator.
    [TestCase(0x1100)] // The transparent transfer cannot recursively target itself.
    [TestCase(0x1105)] // The transparent transfer's padding is not an allocator.
    public void MatchingTransfersStillRequireAnInternalExecutableTarget(int targetRva)
    {
        var bytes = Image();
        U32(bytes, 0x205, unchecked((uint)(targetRva - 0x1009)));
        U32(bytes, 0x301, unchecked((uint)(targetRva - 0x1105)));
        using var stream = new MemoryStream(bytes, 0, bytes.Length, false, true);
        using var pe = new PE(stream);
        Assert.That(X64IteratorAllocatorProof.IsAllocator(pe, X64UnwindProof.ForBinary(pe)!,
            ImageBase + 0x1100), Is.False);
    }

    private static void Relocate(byte[] bytes, uint rva)
    {
        U32(bytes, 0xB00, rva & ~0xFFFU);
        U16(bytes, 0xB08, (ushort)(0xA000U | (rva & 0xFFFU)));
    }

    private static byte[] Image()
    {
        // Redistributable synthetic PE, including an exception-bearing wrapper,
        // a frameless codegen transfer, export tables and loader relocation data.
        var bytes = new byte[0xC00];
        U16(bytes, 0, 0x5A4D); U32(bytes, 0x3C, 0x80);
        U32(bytes, 0x80, 0x4550); U16(bytes, 0x84, 0x8664);
        U16(bytes, 0x86, 2); U16(bytes, 0x94, 0xF0); U16(bytes, 0x98, 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0xB0), ImageBase);
        U32(bytes, 0xD0, 0x3000); U32(bytes, 0x104, 16);
        U32(bytes, 0x108, 0x2000); U32(bytes, 0x10C, 0x180);
        U32(bytes, 0x120, 0x2400); U32(bytes, 0x124, 12);
        U32(bytes, 0x130, 0x2500); U32(bytes, 0x134, 12);
        Section(0x188, ".text", 0x1000, 0x200, 0x400, 0x60000020);
        Section(0x1B0, ".rdata", 0x2000, 0x600, 0x600, 0x40000040);
        bytes.AsSpan(0x200, 0x400).Fill(0xCC);
        new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0xF7, 0x01, 0, 0,
            0xEB, 0x02, 0x31, 0xC0, 0x48, 0x83, 0xC4, 0x28, 0xC3 }.CopyTo(bytes, 0x200);
        new byte[] { 0xE9, 0xFB, 0, 0, 0 }.CopyTo(bytes, 0x300);
        bytes[0x400] = 0xC3;
        U32(bytes, 0xA00, 0x1000); U32(bytes, 0xA04, 0x1020); U32(bytes, 0xA08, 0x2420);
        new byte[] { 9, 4, 1, 0, 4, 0x42, 0, 0 }.CopyTo(bytes, 0xA20);
        U32(bytes, 0xA28, 0x1200);
        U32(bytes, 0xB00, 0x2000); U32(bytes, 0xB04, 12);
        U32(bytes, 0x60C, 0x2050); U32(bytes, 0x610, 1);
        U32(bytes, 0x614, 1); U32(bytes, 0x618, 1);
        U32(bytes, 0x61C, 0x2100); U32(bytes, 0x620, 0x2030); U32(bytes, 0x624, 0x2038);
        U32(bytes, 0x700, 0x1000); U32(bytes, 0x630, 0x2070); U16(bytes, 0x638, 0);
        String(0x650, "AllocatorProof.dll"); String(0x670, "il2cpp_object_new");
        return bytes;

        void String(int at, string value) => Encoding.ASCII.GetBytes(value).CopyTo(bytes, at);
        void Section(int at, string name, uint rva, uint raw, uint size, uint flags)
        {
            String(at, name);
            U32(bytes, at + 8, size); U32(bytes, at + 12, rva); U32(bytes, at + 16, size);
            U32(bytes, at + 20, raw); U32(bytes, at + 36, flags);
        }
    }

    private static void U16(byte[] bytes, int at, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);

    private static void U32(byte[] bytes, int at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
