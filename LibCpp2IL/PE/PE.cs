using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using LibCpp2IL.Logging;

namespace LibCpp2IL.PE;

public sealed class PE : Il2CppBinary
{
    //Initialized in constructor
    private readonly byte[] raw;

    //PE-Specific Stuff
    private readonly SectionHeader[] peSectionHeaders;
    private readonly ulong peImageBase;
    private readonly OptionalHeader64? peOptionalHeader64;
    private readonly OptionalHeader? peOptionalHeader32;

    //Disable null check because this stuff is initialized post-constructor
#pragma warning disable 8618
    private uint[]? peExportedFunctionPointers;
    private uint[] peExportedFunctionNamePtrs;
    private ushort[] peExportedFunctionOrdinals;

    //Il2cpp binary fields:

    //Top-level structs

    //Pointers

    public PE(MemoryStream input) : base(input)
    {
        raw = input.GetBuffer();
        LibLogger.Verbose("\tReading PE File Header...");
        var start = DateTime.Now;

        if (ReadUInt16() != 0x5A4D) //Magic number
            throw new FormatException("ERROR: Magic number mismatch.");
        Position = 0x3C; //Signature position position (lol)
        Position = ReadUInt32(); //Signature position
        if (ReadUInt32() != 0x00004550) //Signature
            throw new FormatException("ERROR: Invalid PE file signature");

        var fileHeader = ReadReadable<FileHeader>();
        if (fileHeader.Machine == 0x014c) //Intel 386
        {
            is32Bit = true;
            InstructionSetId = DefaultInstructionSets.X86_32;
            peOptionalHeader32 = ReadReadable<OptionalHeader>();
            peImageBase = peOptionalHeader32.ImageBase;
        }
        else if (fileHeader.Machine == 0x8664) //AMD64
        {
            InstructionSetId = DefaultInstructionSets.X86_64;
            peOptionalHeader64 = ReadReadable<OptionalHeader64>();
            peImageBase = peOptionalHeader64.ImageBase;
        }
        else
        {
            throw new NotSupportedException("ERROR: Unsupported machine.");
        }

        peSectionHeaders = ReadReadableArrayAtRawAddr<SectionHeader>(-1, fileHeader.NumberOfSections);

        LibLogger.VerboseNewline($"OK ({(DateTime.Now - start).TotalMilliseconds} ms)");
        LibLogger.VerboseNewline($"\t\tImage Base at 0x{peImageBase:X}");
        LibLogger.VerboseNewline($"\t\tDLL is {(is32Bit ? "32" : "64")}-bit");
    }
#pragma warning restore 8618

    public override long MapVirtualAddressToRaw(ulong uiAddr, bool throwOnError = true)
    {
        if (uiAddr < peImageBase)
            if (throwOnError)
                throw new OverflowException($"Provided address, 0x{uiAddr:X}, was less than image base, 0x{peImageBase:X}");
            else
                return VirtToRawInvalidNoMatch;

        var addr = (uint)(uiAddr - peImageBase);

        if (addr == (uint)int.MaxValue + 1)
        {
            if (throwOnError)
                throw new OverflowException($"Provided address, 0x{uiAddr:X}, was less than image base, 0x{peImageBase:X}");

            return VirtToRawInvalidNoMatch;
        }

        var last = peSectionHeaders[peSectionHeaders.Length - 1];
        if (addr > last.VirtualAddress + last.VirtualSize)
        {
            if (throwOnError)
                throw new ArgumentOutOfRangeException(nameof(uiAddr), $"Provided address maps to image offset 0x{addr:X} which is outside the range of the file (last section ends at 0x{last.VirtualAddress + last.VirtualSize:X})");

            return VirtToRawInvalidOutOfBounds;
        }

        var section = peSectionHeaders.FirstOrDefault(x => addr >= x.VirtualAddress && addr < x.VirtualAddress + x.VirtualSize);

        if (section == null)
        {
            if (throwOnError)
                throw new ArgumentOutOfRangeException(nameof(uiAddr), $"Provided address maps to image offset 0x{addr:X} which is outside the range of every section in the file");

            return VirtToRawInvalidOutOfBounds;
        }

        return addr - (section.VirtualAddress - section.PointerToRawData);
    }

    public override ulong MapRawAddressToVirtual(uint offset, bool throwOnError = true)
    {
        var section = peSectionHeaders.FirstOrDefault(x => offset >= x.PointerToRawData && offset < x.PointerToRawData + x.SizeOfRawData);
        if (section == null)
            if (throwOnError)
                throw new InvalidOperationException($"Provided offset, 0x{offset:X}, does not fall within any section of the PE file.");
            else
                return 0;

        return peImageBase + section.VirtualAddress + offset - section.PointerToRawData;
    }

    [MemberNotNull(nameof(peExportedFunctionPointers))]
    private void LoadPeExportTable()
    {
        uint addrExportTable;
        if (is32Bit)
        {
            if (peOptionalHeader32?.DataDirectory == null || peOptionalHeader32.DataDirectory.Length == 0)
                throw new InvalidDataException("Could not load 32-bit optional header or data directory, or data directory was empty!");

            //We assume, per microsoft guidelines, that the first datadirectory is the export table.
            addrExportTable = peOptionalHeader32.DataDirectory.First().VirtualAddress;
        }
        else
        {
            if (peOptionalHeader64?.DataDirectory == null || peOptionalHeader64.DataDirectory.Length == 0)
                throw new InvalidDataException("Could not load 64-bit optional header or data directory, or data directory was empty!");

            //We assume, per microsoft guidelines, that the first datadirectory is the export table.
            addrExportTable = peOptionalHeader64.DataDirectory.First().VirtualAddress;
        }

        try
        {
            //Non-virtual addresses for these
            var directoryEntryExports = ReadReadableAtVirtualAddress<PeDirectoryEntryExport>(addrExportTable + peImageBase);

            peExportedFunctionPointers = ReadClassArrayAtVirtualAddress<uint>(directoryEntryExports.RawAddressOfExportTable + peImageBase, directoryEntryExports.NumberOfExports);
            peExportedFunctionNamePtrs = ReadClassArrayAtVirtualAddress<uint>(directoryEntryExports.RawAddressOfExportNameTable + peImageBase, directoryEntryExports.NumberOfExportNames);
            peExportedFunctionOrdinals = ReadClassArrayAtVirtualAddress<ushort>(directoryEntryExports.RawAddressOfExportOrdinalTable + peImageBase, directoryEntryExports.NumberOfExportNames); //This uses the name count per MSoft spec
        }
        catch (EndOfStreamException)
        {
            LibLogger.WarnNewline($"PE does not appear to contain a valid export table! It would be apparently located at virt address 0x{addrExportTable + peImageBase:X}, raw 0x{MapVirtualAddressToRaw(addrExportTable + peImageBase):X}, but that's beyond the end of the binary. No exported functions will be accessible.");
            peExportedFunctionPointers = [];
            peExportedFunctionNamePtrs = [];
            peExportedFunctionOrdinals = [];
        }
    }

    public override ulong GetVirtualAddressOfExportedFunctionByName(string toFind)
    {
        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        var index = Array.FindIndex(peExportedFunctionNamePtrs, stringAddress =>
        {
            var rawStringAddress = MapVirtualAddressToRaw(stringAddress + peImageBase);
            var exportName = ReadStringToNull(rawStringAddress);
            return exportName == toFind;
        });

        if (index < 0)
            return 0;

        var ordinal = peExportedFunctionOrdinals[index];
        var functionPointer = peExportedFunctionPointers[ordinal];

        return functionPointer + peImageBase;
    }

    /// <summary>
    /// Returns the file-image address of one named PE32+ import-address-table slot.
    /// An absent, ordinal-only, bound, or malformed import returns zero.
    /// </summary>
    public ulong GetVirtualAddressOfImportedFunctionByName(string library, string function)
    {
        if (is32Bit || peOptionalHeader64?.DataDirectory is not { Length: > 1 } directories ||
            string.IsNullOrEmpty(library) || string.IsNullOrEmpty(function))
            return 0;
        var import = directories[1];
        if (import.VirtualAddress == 0 || import.Size < 20)
            return 0;

        for (uint descriptor = 0; descriptor < Math.Min(import.Size / 20, 4096U); descriptor++)
        {
            var rva = (ulong)import.VirtualAddress + descriptor * 20;
            if (rva > uint.MaxValue || !TryMapFileBackedRva((uint)rva, 20, out var rawDescriptor))
                return 0;
            var entry = raw.AsSpan(rawDescriptor, 20);
            var lookup = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var timeStamp = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4));
            var forwarderChain = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
            var name = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12));
            var addressTable = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16));
            if (lookup == 0 && timeStamp == 0 && forwarderChain == 0 && name == 0 && addressTable == 0)
                break;
            if (name == 0 || addressTable == 0 ||
                !string.Equals(ReadImportName(name), library, StringComparison.OrdinalIgnoreCase))
                continue;

            var lookupTable = lookup == 0 ? addressTable : lookup;
            for (uint ordinal = 0; ordinal < 65536; ordinal++)
            {
                var thunkRva = (ulong)lookupTable + ordinal * 8;
                var slotRva = (ulong)addressTable + ordinal * 8;
                if (thunkRva > uint.MaxValue || slotRva > uint.MaxValue ||
                    !TryMapFileBackedRva((uint)thunkRva, 8, out var rawThunk) ||
                    !TryMapFileBackedRva((uint)slotRva, 8, out _))
                    return 0;
                var thunk = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(rawThunk, 8));
                if (thunk == 0)
                    break;
                if ((thunk & (1UL << 63)) != 0 || thunk > uint.MaxValue - 2U ||
                    !string.Equals(ReadImportName((uint)thunk + 2U), function, StringComparison.Ordinal))
                    continue;
                return peImageBase <= ulong.MaxValue - slotRva ? peImageBase + slotRva : 0;
            }
        }
        return 0;
    }

    private string? ReadImportName(uint rva)
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((ulong)rva + (uint)i > uint.MaxValue ||
                !TryMapFileBackedRva(rva + (uint)i, 1, out var offset))
                return null;
            if (raw[offset] == 0)
                return i == 0 ? null : System.Text.Encoding.ASCII.GetString(bytes, 0, i);
            if (raw[offset] < 32 || raw[offset] > 126)
                return null;
            bytes[i] = raw[offset];
        }
        return null;
    }

    private bool TryMapFileBackedRva(uint rva, int length, out int offset)
    {
        offset = 0;
        foreach (var section in peSectionHeaders)
        {
            if (rva < section.VirtualAddress)
                continue;
            var within = (ulong)rva - section.VirtualAddress;
            if (within + (uint)length > section.VirtualSize ||
                within + (uint)length > section.SizeOfRawData)
                continue;
            var rawOffset = (ulong)section.PointerToRawData + within;
            if (rawOffset + (uint)length > (ulong)raw.Length)
                return false;
            offset = (int)rawOffset;
            return true;
        }
        return false;
    }

    public override bool IsExportedFunction(ulong addr)
    {
        if (addr <= peImageBase)
            return false;

        var rva = GetRva(addr);
        if (rva > uint.MaxValue)
            return false;

        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        return Array.IndexOf(peExportedFunctionPointers, (uint)rva) >= 0;
    }

    public override bool TryGetExportedFunctionName(ulong addr, [NotNullWhen(true)] out string? name)
    {
        if (addr <= peImageBase)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }

        var rva = GetRva(addr);
        if (rva > uint.MaxValue)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }

        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        var index = Array.IndexOf(peExportedFunctionPointers, (uint)rva);
        if (index < 0)
        {
            return base.TryGetExportedFunctionName(addr, out name);
        }
        else
        {
            var rawStringAddress = MapVirtualAddressToRaw(peExportedFunctionNamePtrs[index] + peImageBase);
            name = ReadStringToNull(rawStringAddress);
            return true;
        }
    }

    public override IEnumerable<KeyValuePair<string, ulong>> GetExportedFunctions()
    {
        if (peExportedFunctionPointers == null)
            LoadPeExportTable();

        for (var i = 0; i < peExportedFunctionPointers.Length; i++)
        {
            var functionPointer = peExportedFunctionPointers[i];
            var namePointer = peExportedFunctionNamePtrs[i];

            var name = ReadStringToNull(MapVirtualAddressToRaw(namePointer + peImageBase));
            yield return new(name, functionPointer + peImageBase);
        }
    }

    public override ulong GetRva(ulong pointer)
    {
        return pointer - peImageBase;
    }

    public override ReadOnlySpan<byte> GetEntirePrimaryExecutableSection()
    {
        var primarySection = peSectionHeaders.FirstOrDefault(s => s.Name == ".text");

        if (primarySection == null)
            return ReadOnlySpan<byte>.Empty;

        return GetRawBinaryContent().Slice((int)primarySection.PointerToRawData, (int)primarySection.SizeOfRawData);
    }

    public override ulong GetVirtualAddressOfPrimaryExecutableSection() => peSectionHeaders.FirstOrDefault(s => s.Name == ".text")?.VirtualAddress + peImageBase ?? 0;

    public override byte GetByteAtRawAddress(ulong addr) => raw[addr];

    public override long RawLength => raw.Length;

    public override ReadOnlySpan<byte> GetRawBinaryContent() => raw;

    public Stream ToStream() => new MemoryStream(raw, false);
}
