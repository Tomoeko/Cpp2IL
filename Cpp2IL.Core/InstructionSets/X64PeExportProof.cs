using System;
using System.Buffers.Binary;
using System.Text;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Resolves one ordinary named native export from the current PE32+ bytes.
/// Cached PE export arrays are not evidence for a runtime helper's identity.
/// </summary>
internal static class X64PeExportProof
{
    // PE export names use null-terminated ASCII, and their paired ordinals are
    // unbiased 16-bit indexes into the function RVA array.
    // https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
    internal static ulong Find(PE pe, X64UnwindProof.Index unwind, string name)
    {
        try
        {
            var image = pe.GetRawBinaryContent();
            if (pe.PointerSizeBytes != 8 || string.IsNullOrEmpty(name) || name.Length > 1024 ||
                !unwind.HasUnchangedInput(image))
                return 0;
            foreach (var character in name)
                if (character is '\0' or > '\x7f')
                    return 0;

            var header = checked((int)U32(image, 0x3C));
            var optional = checked(header + 24);
            var optionalSize = U16(image, header + 20);
            var sectionCount = U16(image, header + 6);
            if (U16(image, 0) != 0x5A4D || U32(image, header) != 0x4550 ||
                U16(image, header + 4) != 0x8664 || optionalSize < 120 || sectionCount is 0 or > 96 ||
                U16(image, optional) != 0x20B || U32(image, optional + 108) == 0 ||
                BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(optional + 24, 8)) != unwind.ImageBase ||
                !unwind.IsUnaffectedByBaseRelocationRva(0,
                    checked((uint)(optional + optionalSize + sectionCount * 40))))
                return 0;
            var directoryRva = U32(image, optional + 112);
            var directorySize = U32(image, optional + 116);
            var directoryStart = unwind.MapReadOnlyRva(directoryRva, directorySize);
            if (directoryRva == 0 || directorySize < 40 ||
                (ulong)directoryRva + directorySize > uint.MaxValue ||
                directoryStart < 0 || directoryStart > image.Length - (long)directorySize ||
                ReadOnlyRange(image, unwind, directoryRva, 40) is not { } directory ||
                U32(image, directory) != 0)
                return 0;

            var functionCount = U32(image, directory + 20);
            var nameCount = U32(image, directory + 24);
            if (functionCount is 0 or > 1_048_576 || nameCount is 0 or > 65_536)
                return 0;
            var functionsRva = U32(image, directory + 28);
            var namesRva = U32(image, directory + 32);
            var ordinalsRva = U32(image, directory + 36);
            // Bound the complete arrays in immutable data. Only the selected
            // function entry is consumed; every name pointer is searched below.
            var functions = unwind.MapReadOnlyRva(functionsRva, checked(functionCount * 4));
            if (functions < 0 || functions > image.Length - (long)functionCount * 4 ||
                ReadOnlyRange(image, unwind, namesRva, checked(nameCount * 4)) is not { } names ||
                ReadOnlyRange(image, unwind, ordinalsRva, checked(nameCount * 2)) is not { } ordinals)
                return 0;

            string? previous = null;
            uint? selected = null;
            var remainingNameBytes = 4_194_304;
            for (var index = 0; index < nameCount; index++)
            {
                var nameRva = U32(image, checked(names + index * 4));
                if (!ReadName(image, unwind, nameRva, ref remainingNameBytes, out var candidate) ||
                    previous != null && string.CompareOrdinal(previous, candidate) >= 0)
                    return 0;
                previous = candidate;
                var ordinal = U16(image, checked(ordinals + index * 2));
                if (ordinal >= functionCount)
                    return 0;
                if (candidate == name)
                    selected = ordinal;
            }
            if (selected is not { } slot ||
                !unwind.IsUnaffectedByBaseRelocationRva(checked(functionsRva + slot * 4), 4))
                return 0;
            var target = U32(image, checked(functions + (int)slot * 4));
            if (target == 0 ||
                target >= directoryRva && (ulong)target < (ulong)directoryRva + directorySize ||
                !unwind.IsExecutableRva(target))
                return 0;
            // A forwarder is never treated as an in-image helper. The caller
            // must separately authenticate the returned target's native body.
            return checked(unwind.ImageBase + target);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return 0;
        }
    }

    private static bool ReadName(ReadOnlySpan<byte> image, X64UnwindProof.Index unwind,
        uint rva, ref int remaining, out string name)
    {
        name = "";
        if (rva == 0 || rva > uint.MaxValue - 1025)
            return false;
        var raw = unwind.MapReadOnlyRva(rva, 1);
        if (raw < 0 || raw >= image.Length)
            return false;
        var length = image.Slice(raw, Math.Min(1025, image.Length - raw)).IndexOf((byte)0);
        if (length <= 0 || remaining < length + 1 ||
            ReadOnlyRange(image, unwind, rva, (uint)length + 1) != raw)
            return false;
        remaining -= length + 1;
        var value = image.Slice(raw, length);
        foreach (var character in value)
            if (character > 0x7F)
                return false;
        name = Encoding.ASCII.GetString(value.ToArray());
        return true;
    }

    private static int? ReadOnlyRange(ReadOnlySpan<byte> image, X64UnwindProof.Index unwind,
        uint rva, uint length)
    {
        if (rva == 0 || length == 0 || !unwind.IsUnaffectedByBaseRelocationRva(rva, length))
            return null;
        var raw = unwind.MapReadOnlyRva(rva, length);
        return raw >= 0 && raw <= image.Length - (long)length ? raw : null;
    }

    private static ushort U16(ReadOnlySpan<byte> image, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> image, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, 4));
}
