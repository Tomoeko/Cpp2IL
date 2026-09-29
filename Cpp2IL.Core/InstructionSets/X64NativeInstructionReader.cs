using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Reads a bounded, file-backed x64 helper prefix without crossing unsupported unwind data.</summary>
internal static class X64NativeInstructionReader
{
    // Metadata-derived spans can include another function or omit terminal trap
    // padding. Authenticate the independent root .pdata boundary and correlate
    // the cached prefix before giving it to a native provenance proof.
    internal static Instruction[]? ReadRootBody(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } index)
            return null;
        try
        {
            var span = index.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
            if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != method.UnderlyingPointer ||
                span.RootStart != span.Start || span.End <= span.Start || span.End - span.Start > 4096 ||
                method.AppContext.MethodsByAddress.Keys.Any(address => address > span.Start && address < span.End))
                return null;
            var length = checked((int)(span.End - span.Start));
            var offset = pe.MapVirtualAddressToRaw(span.Start, false);
            var image = pe.GetRawBinaryContent();
            if (offset < 0 || offset > image.Length - length)
                return null;
            var bytes = image.Slice(checked((int)offset), length);
            var sharedLength = Math.Min(length, method.RawBytes.Length);
            if (sharedLength == 0 || !method.RawBytes.AsSpan()[..sharedLength].SequenceEqual(bytes[..sharedLength]) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, index, bytes, span.Start) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, index, span.Start, (uint)length))
                return null;
            var body = X86Utils.Disassemble(bytes, span.Start, false).ToArray();
            return body.Length > 0 && body[^1].NextIP == span.End ? body : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

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
