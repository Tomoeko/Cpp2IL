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
    internal static bool HasInteriorManagedEntry(ApplicationAnalysisContext app, ulong start, ulong end) =>
        HasInteriorManagedEntry(app.MethodsByAddress, start, end);

    internal static bool HasInteriorManagedEntry(
        IReadOnlyDictionary<ulong, List<MethodAnalysisContext>> entries, ulong start, ulong end)
    {
        // Native proofs supply small independent bounds. Probe those byte addresses
        // directly, keeping mutations visible without enumerating every managed entry.
        if (start == 0 || end <= start || end - start > 4096)
            return true;
        for (var address = start + 1; address < end; address++)
            if (entries.ContainsKey(address))
                return true;
        return false;
    }

    /// <summary>Authenticates a frameless leaf while preserving any existing cached bytes.</summary>
    internal static Instruction[]? ReadFramelessLeaf(MethodAnalysisContext method, int count, int maxBytes)
    {
        var body = ReadFramelessBody(method, count, maxBytes);
        return body != null && body[^1].Code == Iced.Intel.Code.Retnq && body[^1].OpCount == 0 &&
            body.Take(body.Length - 1).All(instruction => instruction.FlowControl == FlowControl.Next) ? body : null;
    }

    // Authenticating bytes and an independent NoEntry span does not prove control
    // flow or leaf-frame semantics. A caller accepting branches must separately
    // qualify every reachable edge through X86CallerExceptionRegionProof.
    internal static Instruction[]? ReadFramelessBody(MethodAnalysisContext method, int count, int maxBytes)
    {
        if (method.AppContext.Binary is not PE pe || method.UnderlyingPointer == 0 ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } index)
            return null;
        try
        {
            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            if (Read(pe, index, method.UnderlyingPointer, count, maxBytes) is not { } read)
                return null;
            var body = read.ToArray();
            var start = method.UnderlyingPointer;
            var end = body[^1].NextIP;
            if (index.ClassifySpan(start, end) is not
                    { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var provedStart, End: var provedEnd } ||
                provedStart != start || provedEnd != end ||
                HasInteriorManagedEntry(method.AppContext, start, end))
                return null;
            var length = checked((int)(end - start));
            var offset = pe.MapVirtualAddressToRaw(start, false);
            var image = pe.GetRawBinaryContent();
            if (offset < 0 || offset > image.Length - length || method.RawBytes.Length < length)
                return null;
            var bytes = image.Slice(checked((int)offset), length);
            return bytes.SequenceEqual(method.RawBytes.AsSpan().Slice(0, length)) &&
                   X64AncestorConstructorThunkProof.FileBackedExecutable(pe, index, bytes, start)
                ? body : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

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
                HasInteriorManagedEntry(method.AppContext, span.Start, span.End))
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
        return index.IsUnaffectedByBaseRelocation(address,
            checked((uint)(result[^1].NextIP - address))) ? result : null;
    }
}
