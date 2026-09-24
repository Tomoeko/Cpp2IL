using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Classifies x64 native exception regions before recovering ordinary managed control flow.
/// Native exception handlers are not visible in a function's normal instruction stream.
/// Format: https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64
/// </summary>
internal static class X64UnwindProof
{
    internal enum SpanKind { Unsupported, NoEntry, HandlerFree }
    internal readonly record struct SpanClassification(SpanKind Kind, ulong Start, ulong End, ulong RootStart = 0);
    private sealed record CacheEntry(Index? Value);
    // PE owns the read-only input bytes. Context/metadata overrides do not change this input.
    private static readonly ConditionalWeakTable<PE, CacheEntry> Cache = new();

    internal static Index? ForApplication(ApplicationAnalysisContext app) => app.Binary is PE { PointerSizeBytes: 8 } pe
        ? Cache.GetValue(pe, binary => new(Parse(binary.GetRawBinaryContent()))).Value : null;

    internal static Index? Parse(ReadOnlySpan<byte> image)
    {
        try { return new Reader(image).Parse(); }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or IndexOutOfRangeException)
        { return null; }
    }

    internal readonly record struct Section(uint Rva, uint VirtualSize, uint Raw, uint RawSize, uint Characteristics);
    internal sealed record Unwind(byte PrologSize, byte FrameRegister, byte[] Codes,
        byte Flags = 0, uint HandlerRva = 0, uint HandlerDataRva = 0,
        uint ChainStart = 0, uint ChainEnd = 0, uint ChainUnwindRva = 0);
    internal readonly record struct Function(uint Start, uint End, Unwind? Info,
        uint UnwindRva = 0, uint RootStart = 0);
    // Structural PE evidence only. The target and its language-specific data still need
    // independent semantic validation before any managed exception region can be emitted.
    internal readonly record struct HandlerInfo(ulong Start, ulong End, byte Flags,
        ulong HandlerAddress, ulong HandlerDataAddress);
    internal readonly record struct Xmm128Save(ulong End, int Register, uint StackOffset);

    internal sealed class Index
    {
        private readonly ulong _imageBase;
        private readonly uint _imageSize;
        private readonly Section[] _sections;
        private readonly Function[] _functions;

        internal Index(ulong imageBase, uint imageSize, Section[] sections, Function[] functions)
        { _imageBase = imageBase; _imageSize = imageSize; _sections = sections; _functions = functions; }

        internal ulong ImageBase => _imageBase;

        internal int MapReadOnlyRva(uint rva, uint length) => rva < _imageSize &&
            _imageBase <= ulong.MaxValue - rva ? MapReadOnlyData(_imageBase + rva, length) : -1;

        internal bool IsExecutableRva(uint rva) => rva < _imageSize && Map(_sections, rva, 1, true) >= 0;

        internal bool IsReadableFileBackedRva(uint rva)
        {
            if (rva >= _imageSize || Map(_sections, rva, 1, false) < 0)
                return false;
            foreach (var section in _sections)
                if (rva >= section.Rva && (ulong)rva < (ulong)section.Rva + section.VirtualSize)
                    return (section.Characteristics & 0x40000000) != 0;
            return false;
        }

        internal bool IsWritableFileBackedRva(uint rva)
        {
            if (rva >= _imageSize || Map(_sections, rva, 1, false) < 0)
                return false;
            foreach (var section in _sections)
                if (rva >= section.Rva && (ulong)rva < (ulong)section.Rva + section.VirtualSize)
                    // A runtime metadata slot must be readable and writable, with no
                    // executable mapping that could alias proved code.
                    return (section.Characteristics & 0xE0000000) == 0xC0000000;
            return false;
        }

        internal bool IsWritableZeroInitializedRva(uint rva)
        {
            if (rva >= _imageSize)
                return false;
            foreach (var section in _sections)
                if (rva >= section.Rva && (ulong)rva < (ulong)section.Rva + section.VirtualSize)
                    // PE maps the virtual tail after SizeOfRawData as zeros. Unlike
                    // TypeInfo slots, compiler-generated once flags may live here.
                    return (section.Characteristics & 0xE0000000) == 0xC0000000 &&
                           (ulong)rva - section.Rva >= section.RawSize;
            return false;
        }

        // A PE section's virtual tail after its file-backed bytes is zero-initialized.
        // Checking two writable endpoints alone misses gaps and intervening sections.
        // Require the whole range in one readable, writable, non-executable section.
        internal bool IsWritableVirtualRangeInOneSection(uint rva, uint length)
        {
            if (length == 0 || rva >= _imageSize || (ulong)rva + length > _imageSize)
                return false;
            foreach (var section in _sections)
                if (rva >= section.Rva &&
                    (ulong)rva + length <= (ulong)section.Rva + section.VirtualSize)
                    return (section.Characteristics & 0xE0000000) == 0xC0000000;
            return false;
        }

        internal SpanClassification ClassifySpan(ulong start, ulong end)
        {
            if (!TryRange(start, end, out var rva, out var endRva))
                return new(SpanKind.Unsupported, 0, 0);
            var index = Find(rva);
            if (index < _functions.Length && _functions[index].Start < endRva)
            {
                var function = _functions[index];
                return function.Start <= rva && endRva <= function.End &&
                       function.Info is { Flags: 0 or 4 }
                    ? new(SpanKind.HandlerFree, _imageBase + function.Start, _imageBase + function.End,
                        _imageBase + function.RootStart)
                    : new(SpanKind.Unsupported, _imageBase + function.Start, _imageBase + function.End);
            }
            return new(SpanKind.NoEntry, start, end);
        }

        internal HandlerInfo? GetHandler(ulong entry)
        {
            if (entry == ulong.MaxValue || !TryRange(entry, entry + 1, out var rva, out _))
                return null;
            var index = Find(rva);
            if (index >= _functions.Length || _functions[index] is not
                { Start: var start, End: var end, Info: { Flags: var flags, HandlerRva: var handler,
                    HandlerDataRva: var data } } || start != rva || flags is not (1 or 2 or 3))
                return null;
            if (MapReadOnlyData(_imageBase + data, 1) < 0)
                return null;
            return new(_imageBase + start, _imageBase + end, flags,
                _imageBase + handler, _imageBase + data);
        }

        // Immutable literal evidence must stay inside one readable, file-backed data section.
        internal int MapReadOnlyData(ulong address, uint length)
        {
            if (length == 0 || address < _imageBase || address - _imageBase >= _imageSize ||
                length > _imageSize - (address - _imageBase))
                return -1;
            var rva = checked((uint)(address - _imageBase));
            foreach (var section in _sections)
                if (rva >= section.Rva && (ulong)rva + length <= (ulong)section.Rva + section.VirtualSize)
                {
                    // IMAGE_SCN_MEM_READ is required; WRITE and EXECUTE are outside this scope.
                    if ((section.Characteristics & 0xE0000000) != 0x40000000)
                        return -1;
                    return Map(_sections, rva, length, false);
                }
            return -1;
        }

        // A semantic helper matcher can additionally bind the decoded native prolog to
        // its exact unwind operations. The reader does not know any helper-specific shape.
        internal bool MatchesUnwind(ulong start, ulong end, byte prologSize, byte frameRegister, ReadOnlySpan<byte> codes)
        {
            var span = ClassifySpan(start, end);
            if (span.Kind != SpanKind.HandlerFree || span.Start != start)
                return false;
            var function = _functions[Find(checked((uint)(start - _imageBase)))];
            return function.Info!.Flags == 0 && function.Info.PrologSize == prologSize && function.Info.FrameRegister == frameRegister &&
                   function.Info.Codes.AsSpan().SequenceEqual(codes);
        }

        // Corroborate each candidate ABI save with the native prologue's exact
        // register, instruction end, frame size and frame-relative 16-byte slot.
        // Chained regions and frame-pointer-relative saves are outside this proof.
        internal bool MatchesXmm128Saves(ulong entry, IReadOnlyList<Xmm128Save> saves,
            uint frameSize)
        {
            if (frameSize == 0 || saves.Count is < 1 or > 10 || saves.Any(save =>
                    save.Register is < 6 or > 15 || save.StackOffset < 0x20 ||
                    save.StackOffset % 16 != 0 || save.End <= entry ||
                    save.End - entry > byte.MaxValue) ||
                ClassifySpan(entry, saves.Max(save => save.End)) is not
                    { Kind: SpanKind.HandlerFree } span ||
                span.Start != entry || span.RootStart != entry)
                return false;

            var function = _functions[Find(checked((uint)(entry - _imageBase)))];
            if (function.Info is not { Flags: 0, FrameRegister: 0 } unwind ||
                saves.Any(save => save.End - entry > unwind.PrologSize))
                return false;

            var matched = new HashSet<int>();
            var codes = unwind.Codes;
            ulong unwindFrameSize = 0;
            for (var at = 0; at < codes.Length;)
            {
                var operation = codes[at + 1] & 15;
                var info = codes[at + 1] >> 4;
                unwindFrameSize += operation switch
                {
                    0 => 8UL,
                    1 when info == 0 => (ulong)(codes[at + 2] | codes[at + 3] << 8) * 8,
                    1 when info == 1 => (uint)(codes[at + 2] | codes[at + 3] << 8 |
                        codes[at + 4] << 16 | codes[at + 5] << 24),
                    2 => (ulong)(info + 1) * 8,
                    _ => 0,
                };
                if (operation is 8 or 9)
                {
                    // The reader has already validated the code slots. A second XMM
                    // save needs its own independent stack/exit proof.
                    if (operation != 8 || !matched.Add(info) ||
                        !saves.Any(save => save.Register == info &&
                            codes[at] == save.End - entry &&
                            (uint)(codes[at + 2] | codes[at + 3] << 8) * 16 == save.StackOffset))
                        return false;
                }
                at += operation switch
                {
                    1 when info == 0 => 4,
                    1 when info == 1 => 6,
                    4 or 8 => 4,
                    5 or 9 => 6,
                    _ => 2,
                };
            }
            return matched.Count == saves.Count && unwindFrameSize == frameSize;
        }

        // First record whose end lies beyond the requested PC. All records are sorted and
        // disjoint, so this also distinguishes a gap from an interior or cross-function span.
        private int Find(uint rva)
        {
            var low = 0;
            var high = _functions.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_functions[middle].End <= rva) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private bool TryRange(ulong start, ulong end, out uint rva, out uint endRva)
        {
            rva = endRva = 0;
            if (start < _imageBase || end <= start || end - _imageBase > _imageSize)
                return false;
            rva = (uint)(start - _imageBase);
            endRva = (uint)(end - _imageBase);
            return Map(_sections, rva, endRva - rva, true) >= 0;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> image)
    {
        private readonly ReadOnlySpan<byte> _image = image;
        private Section[] _sections = [];
        private uint _imageSize;

        public Index? Parse()
        {
            if (U16(0) != 0x5A4D)
                return null;
            var pe = checked((int)U32(0x3C));
            if (U32(pe) != 0x4550 || U16(pe + 4) != 0x8664)
                return null;
            var sectionCount = U16(pe + 6);
            var optionalSize = U16(pe + 20);
            var optional = checked(pe + 24);
            if (sectionCount is 0 or > 96 || optionalSize < 144 || U16(optional) != 0x20B || U32(optional + 108) < 4)
                return null;
            var imageBase = U64(optional + 24);
            _imageSize = U32(optional + 56);
            var tableRva = U32(optional + 136);
            var tableSize = U32(optional + 140);
            if (imageBase == 0 || _imageSize == 0 || imageBase > ulong.MaxValue - _imageSize ||
                tableRva == 0 || tableRva % 4 != 0 || tableSize == 0 || tableSize % 12 != 0)
                return null;
            _sections = new Section[sectionCount];
            var sectionStart = checked(optional + optionalSize);
            for (var index = 0; index < sectionCount; index++)
            {
                var at = checked(sectionStart + index * 40);
                var section = new Section(U32(at + 12), U32(at + 8), U32(at + 20), U32(at + 16), U32(at + 36));
                if ((ulong)section.Rva + section.VirtualSize > _imageSize ||
                    (ulong)section.Raw + section.RawSize > (ulong)_image.Length)
                    return null;
                for (var prior = 0; prior < index; prior++)
                {
                    var other = _sections[prior];
                    if (section.VirtualSize != 0 && other.VirtualSize != 0 &&
                        section.Rva < (ulong)other.Rva + other.VirtualSize && other.Rva < (ulong)section.Rva + section.VirtualSize)
                        return null;
                }
                _sections[index] = section;
            }
            var table = Map(_sections, tableRva, tableSize, false);
            if (table < 0)
                return null;
            var records = new Function[checked((int)(tableSize / 12))];
            var unwind = new Dictionary<uint, Unwind?>();
            uint previousEnd = 0;
            for (var index = 0; index < records.Length; index++)
            {
                var at = checked(table + index * 12);
                var start = U32(at);
                var end = U32(at + 4);
                var unwindRva = U32(at + 8);
                if (start >= end || end > _imageSize || start < previousEnd)
                    return null;
                previousEnd = end;
                if (!unwind.TryGetValue(unwindRva, out var info))
                    unwind[unwindRva] = info = ReadUnwind(unwindRva);
                if (info?.PrologSize > end - start)
                    info = null;
                records[index] = new(start, end, info, unwindRva, start);
            }
            // A chain is evidence for the primary function only when its tuple names an
            // exact handler-free .pdata record. Do not follow arbitrary or nested chains.
            var byStart = new Dictionary<uint, Function>();
            foreach (var record in records)
                byStart.Add(record.Start, record);
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                if (record.Info is not { Flags: 4 } chain)
                    continue;
                if (!byStart.TryGetValue(chain.ChainStart, out var root) ||
                    root.Start == record.Start || root.End != chain.ChainEnd ||
                    root.UnwindRva != chain.ChainUnwindRva ||
                    root.Info is not { Flags: 0 } primary ||
                    primary.FrameRegister != chain.FrameRegister)
                    records[index] = record with { Info = null };
                else
                    records[index] = record with { RootStart = root.Start };
            }
            return new Index(imageBase, _imageSize, _sections, records);
        }

        private Unwind? ReadUnwind(uint rva)
        {
            if (rva == 0 || rva % 4 != 0)
                return null;
            var at = Map(_sections, rva, 4, false);
            if (at < 0 || (_image[at] & 7) != 1)
                return null;
            var flags = (byte)(_image[at] >> 3);
            if (flags > 4)
                return null;
            var prolog = _image[at + 1];
            var count = _image[at + 2];
            var frame = _image[at + 3];
            if (Map(_sections, rva, (uint)(4 + (count + 1) / 2 * 4), false) != at)
                return null;
            var codes = _image.Slice(at + 4, count * 2).ToArray();
            if (!ValidCodes(codes, prolog, frame, flags == 4))
                return null;
            if (flags == 0)
                return new(prolog, frame, codes);
            var handlerField = checked(rva + (uint)(4 + (count + 1) / 2 * 4));
            if (flags == 4)
            {
                var chainField = Map(_sections, handlerField, 12, false);
                if (chainField < 0)
                    return null;
                var chainStart = U32(chainField);
                var chainEnd = U32(chainField + 4);
                var chainUnwindRva = U32(chainField + 8);
                if (chainStart >= chainEnd || chainEnd > _imageSize || chainUnwindRva == 0)
                    return null;
                return new(prolog, frame, codes, flags, ChainStart: chainStart,
                    ChainEnd: chainEnd, ChainUnwindRva: chainUnwindRva);
            }
            var fieldOffset = Map(_sections, handlerField, 5, false);
            if (fieldOffset < 0)
                return null;
            var handlerRva = U32(fieldOffset);
            if (handlerRva == 0 || Map(_sections, handlerRva, 1, true) < 0)
                return null;
            var dataRva = checked(handlerField + 4);
            return new(prolog, frame, codes, flags, handlerRva, dataRva);
        }

        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_image.Slice(offset, 2));
        private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_image.Slice(offset, 4));
        private ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_image.Slice(offset, 8));
    }

    // Validate the supported version1 encoding without guessing unknown unwind ops.
    private static bool ValidCodes(byte[] codes, byte prolog, byte frame, bool chain)
    {
        var previous = (int)prolog;
        var sawFrame = false;
        for (var at = 0; at < codes.Length;)
        {
            var offset = codes[at];
            var operation = codes[at + 1] & 15;
            var info = codes[at + 1] >> 4;
            // Shrink-wrapped chained fragments can record a save at their first byte.
            if ((offset == 0 && (!chain || operation is not (4 or 5 or 8 or 9))) ||
                offset > previous || offset > prolog)
                return false;
            previous = offset;
            var slots = operation switch
            {
                0 when Nonvolatile(info) => 1, // PUSH_NONVOL
                1 when info == 0 => 2, // ALLOC_LARGE scaled ushort
                1 when info == 1 => 3, // ALLOC_LARGE uint
                2 => 1, // ALLOC_SMALL
                3 when info == 0 && !sawFrame && Nonvolatile(frame & 15) => 1,
                4 when Nonvolatile(info) => 2, // SAVE_NONVOL
                5 when Nonvolatile(info) => 3,
                8 when info >= 6 => 2, // SAVE_XMM128
                9 when info >= 6 => 3,
                _ => 0,
            };
            if (slots == 0 || at + slots * 2 > codes.Length)
                return false;
            if (operation == 3) sawFrame = true;
            at += slots * 2;
        }
        return (frame == 0 && !sawFrame) || ((frame & 15) != 0 && sawFrame);
    }

    private static bool Nonvolatile(int register) => register is 3 or 5 or 6 or 7 or >= 12 and <= 15;

    private static int Map(Section[] sections, uint rva, uint length, bool executable)
    {
        if (length == 0)
            return -1;
        foreach (var section in sections)
        {
            if (rva < section.Rva || (ulong)rva + length > (ulong)section.Rva + section.VirtualSize)
                continue;
            var relative = rva - section.Rva;
            if ((ulong)relative + length > section.RawSize || executable && (section.Characteristics & 0x20000000) == 0)
                return -1;
            return checked((int)((ulong)section.Raw + relative));
        }
        return -1;
    }
}
