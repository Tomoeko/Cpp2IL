using System;
using System.Collections.Generic;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Reads a bounded, file-backed x64 helper prefix without crossing unsupported unwind data.</summary>
internal static class X64NativeInstructionReader
{
    internal static IReadOnlyList<Instruction>? Read(PE pe, X64UnwindProof.Index index,
        ulong address, int count, int maxBytes)
    {
        if (count <= 0 || maxBytes <= 0 || address < index.ImageBase ||
            address >= ulong.MaxValue - (ulong)maxBytes ||
            address - index.ImageBase > uint.MaxValue ||
            !index.IsExecutableRva((uint)(address - index.ImageBase)))
            return null;
        var start = pe.MapVirtualAddressToRaw(address, false);
        var end = pe.MapVirtualAddressToRaw(address + (ulong)maxBytes - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (start < 0 || end < start || end - start != maxBytes - 1 || end >= raw.Length)
            return null;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(raw.Slice((int)start, maxBytes).ToArray()), address);
        var result = new Instruction[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = decoder.Decode();
            if (result[i].IsInvalid || result[i].CodeSize != CodeSize.Code64 ||
                result[i].NextIP > address + (ulong)maxBytes || result[i].HasLockPrefix ||
                result[i].HasRepPrefix || result[i].HasRepnePrefix ||
                result[i].SegmentPrefix != Register.None ||
                index.ClassifySpan(address, result[i].NextIP).Kind == X64UnwindProof.SpanKind.Unsupported)
                return null;
        }
        return result;
    }
}
