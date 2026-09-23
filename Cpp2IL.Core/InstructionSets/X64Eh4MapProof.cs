using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Reads the bounded, non-separated MSVC frame-handler-4 map format as structural
/// evidence. Native cleanup actions and catch funclets are not managed IL clauses.
/// </summary>
internal static class X64Eh4MapProof
{
    internal sealed record Map(byte Header, IReadOnlyList<UnwindAction> UnwindActions,
        IReadOnlyList<TryBlock> TryBlocks, IReadOnlyList<IpState> IpStates);
    internal sealed record UnwindAction(uint NextOffset, byte Kind, uint? ActionRva, uint? ObjectOffset);
    internal sealed record TryBlock(uint LowState, uint HighState, uint CatchHighState,
        IReadOnlyList<Handler> Handlers);
    internal sealed record Handler(byte Header, uint? Adjectives, uint? TypeRva,
        uint? CatchObjectOffset, uint FuncletRva, IReadOnlyList<uint> ContinuationRvas);
    internal sealed record IpState(uint Rva, int State);

    private const uint MaxEntries = 4096;
    private sealed class Budget
    {
        private uint _remaining = 65536;
        internal bool Take(uint count)
        {
            if (count > MaxEntries || count > _remaining) return false;
            _remaining -= count;
            return true;
        }
    }

    internal static Map? Parse(ReadOnlySpan<byte> image, X64UnwindProof.Index index,
        X64UnwindProof.HandlerInfo region)
    {
        if ((region.Flags & 1) == 0 || region.Start < index.ImageBase ||
            region.End <= region.Start || region.End - index.ImageBase > uint.MaxValue)
            return null;
        var startRva = (uint)(region.Start - index.ImageBase);
        var data = new Cursor(image, index, region.HandlerDataAddress);
        if (!data.ReadUInt32(out var infoRva) || infoRva == 0 ||
            index.MapReadOnlyRva(infoRva, 1) < 0)
            return null;
        var info = new Cursor(image, index, index.ImageBase + infoRva);
        if (!info.ReadByte(out var header) || (header & 0x83) != 0)
            return null; // reserved, catch-funclet and separated formats need their own proof
        if ((header & 4) != 0 && !info.ReadCompressed(out _))
            return null;
        uint unwindRva = 0, tryRva = 0;
        if ((header & 8) != 0 && (!info.ReadUInt32(out unwindRva) || unwindRva == 0))
            return null;
        if ((header & 16) != 0 && (!info.ReadUInt32(out tryRva) || tryRva == 0))
            return null;
        if (!info.ReadUInt32(out var ipRva) || ipRva == 0)
            return null;
        var budget = new Budget();
        if (!ReadUnwind(image, index, unwindRva, budget, out var unwind) ||
            !ReadTry(image, index, tryRva, startRva, budget, out var blocks) ||
            !ReadIp(image, index, ipRva, startRva, region.End - index.ImageBase, budget, out var states))
            return null;
        foreach (var block in blocks)
            if (block.CatchHighState >= unwind.Count)
                return null;
        foreach (var state in states)
            if (state.State >= unwind.Count)
                return null;
        return new Map(header, unwind, blocks, states);
    }

    private static bool ReadUnwind(ReadOnlySpan<byte> image, X64UnwindProof.Index index,
        uint rva, Budget budget, out IReadOnlyList<UnwindAction> actions)
    {
        actions = Array.Empty<UnwindAction>();
        if (rva == 0)
            return true;
        var cursor = new Cursor(image, index, index.ImageBase + rva);
        if (!cursor.ReadCompressed(out var count) || !budget.Take(count))
            return false;
        var entries = new List<UnwindAction>((int)count);
        for (var i = 0U; i < count; i++)
        {
            if (!cursor.ReadCompressed(out var encoded))
                return false;
            var kind = (byte)(encoded & 3);
            uint? action = null, obj = null;
            if (kind != 0)
            {
                if (!cursor.ReadUInt32(out var actionRva) || !index.IsExecutableRva(actionRva))
                    return false;
                action = actionRva;
            }
            if (kind is 1 or 2)
            {
                if (!cursor.ReadCompressed(out var offset))
                    return false;
                obj = offset;
            }
            entries.Add(new UnwindAction(encoded >> 2, kind, action, obj));
        }
        actions = entries;
        return true;
    }

    private static bool ReadTry(ReadOnlySpan<byte> image, X64UnwindProof.Index index,
        uint rva, uint functionRva, Budget budget, out IReadOnlyList<TryBlock> blocks)
    {
        blocks = Array.Empty<TryBlock>();
        if (rva == 0)
            return true;
        var cursor = new Cursor(image, index, index.ImageBase + rva);
        if (!cursor.ReadCompressed(out var count) || !budget.Take(count))
            return false;
        var entries = new List<TryBlock>((int)count);
        for (var i = 0U; i < count; i++)
        {
            if (!cursor.ReadCompressed(out var low) || !cursor.ReadCompressed(out var high) ||
                !cursor.ReadCompressed(out var catchHigh) || !cursor.ReadUInt32(out var handlerRva) ||
                low > high || high > catchHigh || handlerRva == 0 ||
                !ReadHandlers(image, index, handlerRva, functionRva, budget, out var handlers))
                return false;
            entries.Add(new TryBlock(low, high, catchHigh, handlers));
        }
        blocks = entries;
        return true;
    }

    private static bool ReadHandlers(ReadOnlySpan<byte> image, X64UnwindProof.Index index,
        uint rva, uint functionRva, Budget budget, out IReadOnlyList<Handler> handlers)
    {
        handlers = Array.Empty<Handler>();
        var cursor = new Cursor(image, index, index.ImageBase + rva);
        if (!cursor.ReadCompressed(out var count) || !budget.Take(count))
            return false;
        var entries = new List<Handler>((int)count);
        for (var i = 0U; i < count; i++)
        {
            if (!cursor.ReadByte(out var header) || (header & 0xC0) != 0 ||
                (header & 0x30) == 0x30)
                return false;
            uint? adjectives = null, typeRva = null, obj = null;
            if ((header & 1) != 0)
            {
                if (!cursor.ReadCompressed(out var value)) return false;
                adjectives = value;
            }
            if ((header & 2) != 0)
            {
                // MSVC type descriptors may reside in writable .data. Their bytes are
                // not immutable type-identity evidence for managed catch matching.
                if (!cursor.ReadUInt32(out var value) || !index.IsReadableFileBackedRva(value))
                    return false;
                typeRva = value;
            }
            if ((header & 4) != 0)
            {
                if (!cursor.ReadCompressed(out var value)) return false;
                obj = value;
            }
            if (!cursor.ReadUInt32(out var funclet) || !index.IsExecutableRva(funclet))
                return false;
            var continuations = new List<uint>((header >> 4) & 3);
            for (var j = 0; j < ((header >> 4) & 3); j++)
            {
                if ((header & 8) != 0)
                {
                    if (!cursor.ReadUInt32(out var value) || !index.IsExecutableRva(value))
                        return false;
                    continuations.Add(value);
                }
                else
                {
                    if (!cursor.ReadCompressed(out var delta) || delta > uint.MaxValue - functionRva ||
                        !index.IsExecutableRva(functionRva + delta))
                        return false;
                    continuations.Add(functionRva + delta);
                }
            }
            entries.Add(new Handler(header, adjectives, typeRva, obj, funclet, continuations));
        }
        handlers = entries;
        return true;
    }

    private static bool ReadIp(ReadOnlySpan<byte> image, X64UnwindProof.Index index,
        uint rva, uint startRva, ulong endRva, Budget budget, out IReadOnlyList<IpState> states)
    {
        states = Array.Empty<IpState>();
        var cursor = new Cursor(image, index, index.ImageBase + rva);
        if (!cursor.ReadCompressed(out var count) || !budget.Take(count))
            return false;
        var entries = new List<IpState>((int)count);
        ulong offset = 0;
        for (var i = 0U; i < count; i++)
        {
            if (!cursor.ReadCompressed(out var delta) || !cursor.ReadCompressed(out var encodedState) ||
                encodedState > int.MaxValue || delta == 0)
                return false;
            offset += delta;
            if ((ulong)startRva + offset > endRva)
                return false;
            entries.Add(new IpState((uint)(startRva + offset), (int)encodedState - 1));
        }
        states = entries;
        return true;
    }

    private ref struct Cursor(ReadOnlySpan<byte> image, X64UnwindProof.Index index, ulong address)
    {
        private readonly ReadOnlySpan<byte> _image = image;
        private readonly X64UnwindProof.Index _index = index;
        private ulong _address = address;

        internal bool ReadByte(out byte value)
        {
            value = 0;
            var offset = _index.MapReadOnlyData(_address, 1);
            if (offset < 0 || offset >= _image.Length) return false;
            value = _image[offset];
            _address++;
            return true;
        }

        internal bool ReadUInt32(out uint value)
        {
            value = 0;
            var offset = _index.MapReadOnlyData(_address, 4);
            if (offset < 0 || offset > _image.Length - 4) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_image.Slice(offset, 4));
            _address += 4;
            return true;
        }

        internal bool ReadCompressed(out uint value)
        {
            value = 0;
            if (!ReadByte(out var first)) return false;
            if ((first & 1) == 0) { value = (uint)first >> 1; return true; }
            var length = (first & 3) == 1 ? 2 : (first & 7) == 3 ? 3 :
                (first & 15) == 7 ? 4 : first == 15 ? 5 : 0;
            if (length == 0) return false;
            if (length == 5) return ReadUInt32(out value);
            value = (uint)first >> length;
            for (var i = 1; i < length; i++)
            {
                if (!ReadByte(out var next)) return false;
                value |= (uint)next << (8 * i - length);
            }
            return true;
        }
    }
}
