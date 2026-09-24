using System;
using System.Buffers.Binary;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates an initially zero, writable PE byte used as a metadata once flag.
/// A file-backed zero byte is accepted only when no base relocation can change it.
/// </summary>
internal static class X64PeOnceFlagProof
{
    internal static bool IsInitiallyZero(PE pe, X64UnwindProof.Index unwind, ulong address)
    {
        try
        {
            if (address < unwind.ImageBase || address - unwind.ImageBase > uint.MaxValue)
                return false;

            var zeroFill = X64MetadataStaticGetterProof.ZeroInitializedWritableData(
                unwind, address, 1);
            if (!zeroFill)
            {
                if (!X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind,
                        address, 1))
                    return false;
                var rawOffset = pe.MapVirtualAddressToRaw(address, false);
                if (rawOffset < 0 || rawOffset >= pe.GetRawBinaryContent().Length ||
                    pe.GetRawBinaryContent()[(int)rawOffset] != 0)
                    return false;
            }

            return IsUnrelocatedRange(pe, unwind, address, 1);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Proves that the loader cannot change any byte in a file-backed metadata
    /// usage slot through an x64 base relocation.
    /// </summary>
    internal static bool IsUnrelocatedRange(PE pe, X64UnwindProof.Index unwind,
        ulong address, uint length)
    {
        try
        {
            if (length == 0 || address < unwind.ImageBase ||
                address - unwind.ImageBase > uint.MaxValue - (ulong)length + 1)
                return false;

            var image = pe.GetRawBinaryContent();
            if (ReadUInt16(image, 0) != 0x5A4D)
                return false;
            var header = checked((int)ReadUInt32(image, 0x3C));
            if (ReadUInt32(image, header) != 0x4550 ||
                ReadUInt16(image, header + 4) != 0x8664)
                return false;
            var optionalSize = ReadUInt16(image, header + 20);
            var optional = checked(header + 24);
            const int baseRelocationDirectory = 112 + 5 * 8;
            if (optionalSize < baseRelocationDirectory + 8 ||
                ReadUInt16(image, optional) != 0x20B ||
                ReadUInt64(image, optional + 24) != unwind.ImageBase ||
                ReadUInt32(image, optional + 108) < 6)
                return false;

            var relocationRva = ReadUInt32(image, optional + baseRelocationDirectory);
            var relocationSize = ReadUInt32(image, optional + baseRelocationDirectory + 4);
            if (relocationRva == 0 || relocationSize < 8 ||
                relocationSize > int.MaxValue)
                return false;
            var relocationRaw = unwind.MapReadOnlyRva(relocationRva,
                relocationSize);
            if (relocationRaw < 0 || relocationRaw > image.Length - relocationSize)
                return false;
            return HasNoRelocationInRange(image.Slice(relocationRaw,
                (int)relocationSize), checked((uint)(address - unwind.ImageBase)),
                length);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    internal static bool HasNoRelocationOnByte(ReadOnlySpan<byte> blocks,
        uint targetRva) => HasNoRelocationInRange(blocks, targetRva, 1);

    internal static bool HasNoRelocationInRange(ReadOnlySpan<byte> blocks,
        uint targetRva, uint length)
    {
        try
        {
            if (length == 0 || (ulong)targetRva + length > (ulong)uint.MaxValue + 1)
                return false;
            for (var at = 0; at < blocks.Length;)
            {
                if (blocks.Length - at < 8)
                    return false;
                var page = ReadUInt32(blocks, at);
                var size = ReadUInt32(blocks, at + 4);
                if (page % 0x1000 != 0 || size < 8 || size % 2 != 0 ||
                    size > blocks.Length - at)
                    return false;
                for (var item = at + 8; item < at + size; item += 2)
                {
                    var entry = ReadUInt16(blocks, item);
                    var kind = entry >> 12;
                    if (kind == 0)
                        continue;
                    if (kind != 10)
                        return false;
                    var relocatedRva = checked(page + (uint)(entry & 0x0FFF));
                    if ((ulong)relocatedRva < (ulong)targetRva + length &&
                        (ulong)targetRva < (ulong)relocatedRva + 8)
                        return false;
                }
                at += checked((int)size);
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
}
