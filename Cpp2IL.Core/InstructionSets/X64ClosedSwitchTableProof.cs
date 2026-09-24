using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the code/data boundary and all ordinary native successors of one
/// guarded x64 jump table. This is a CFG fact only: it does not assign managed
/// types, prove exception behavior, or emit IL for the case bodies.
/// </summary>
internal static class X64ClosedSwitchTableProof
{
    internal sealed record Evidence(ulong Entry, ulong Dispatch, ulong Table,
        ulong DefaultTarget, IReadOnlyList<ulong> CaseTargets,
        IReadOnlyList<Instruction> Code);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var evidence = TryProve(pe.GetRawBinaryContent(), method.UnderlyingPointer,
            unwind.ImageBase, app.MethodsByAddress.Keys, unwind.ClassifySpan);
        if (evidence == null)
            return null;

        // PE section flags alone do not prevent the Windows loader from
        // rebasing bytes through .reloc. The existing x64 relocation reader
        // validates the directory and rejects any fixup in code or table.
        var end = evidence.Table + (ulong)evidence.CaseTargets.Count * 4;
        return X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, evidence.Entry,
            checked((uint)(end - evidence.Entry))) ? evidence : null;
    }

    // This injected CFG seam does not authenticate loader relocations. Find
    // must perform that independent PE proof before returning evidence.
    // Supplying the image separately permits mutations without changing the
    // application's cached model.
    internal static Evidence? TryProve(ReadOnlySpan<byte> image, ulong entry,
        ulong expectedImageBase, IEnumerable<ulong> methodRoots,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify)
    {
        if (methodRoots == null || classify == null ||
            !TryReadSection(image, entry, expectedImageBase, out var section))
            return null;

        var prefix = new Instruction[7];
        var cursor = entry;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!TryDecode(image, section, cursor, out prefix[i]) ||
                !PlainInstruction(prefix[i]))
                return null;
            cursor = prefix[i].NextIP;
        }

        if (!TryDispatch(prefix, section.ImageBase, out var caseCount,
                out var table, out var defaultTarget) ||
            table <= cursor || table - entry > 64 * 1024 ||
            !section.Map(table, checked((uint)caseCount * 4), out var tableRaw))
            return null;

        var tableEnd = table + (ulong)caseCount * 4;
        if (tableEnd < table || defaultTarget < cursor || defaultTarget >= table)
            return null;
        foreach (var root in methodRoots)
            if (root != entry && root >= entry && root < tableEnd)
                return null;

        var targets = new ulong[caseCount];
        for (var i = 0; i < targets.Length; i++)
        {
            var rva = BinaryPrimitives.ReadUInt32LittleEndian(
                image.Slice(tableRaw + i * 4, 4));
            targets[i] = section.ImageBase + rva;
            if (targets[i] < cursor || targets[i] >= table)
                return null;
        }

        // A guard at the entry dominates this dispatch only if no later edge
        // re-enters its prefix. Decode reachable successors from individual
        // addresses; linear disassembly would reinterpret the table as code.
        var occupied = new bool[checked((int)(table - entry))];
        var decoded = new Dictionary<ulong, Instruction>();
        var pending = new Stack<ulong>();
        pending.Push(entry);
        var entryRegion = classify(entry, prefix[0].NextIP);
        if (entryRegion.Kind == X64UnwindProof.SpanKind.Unsupported ||
            entryRegion.Kind == X64UnwindProof.SpanKind.HandlerFree &&
            (entryRegion.Start != entry || entryRegion.RootStart != entry ||
             entryRegion.End < table))
            return null;

        var instructionInfo = new InstructionInfoFactory();
        while (pending.Count != 0)
        {
            var address = pending.Pop();
            if (decoded.ContainsKey(address))
                continue;
            if (address < entry || address >= table ||
                !TryDecode(image, section, address, out var instruction) ||
                !PlainInstruction(instruction) || instruction.NextIP > table ||
                !SameRegion(classify(address, instruction.NextIP), entryRegion) ||
                !SafeFrameEffect(instruction, entryRegion.Kind, instructionInfo))
                return null;

            var start = checked((int)(address - entry));
            for (var offset = start; offset < start + instruction.Length; offset++)
            {
                if (occupied[offset])
                    return null;
                occupied[offset] = true;
            }
            decoded.Add(address, instruction);

            if (address < cursor)
            {
                var prefixIndex = Array.FindIndex(prefix, part => part.IP == address);
                if (prefixIndex < 0 || !instruction.Equals(prefix[prefixIndex]))
                    return null;
            }

            switch (instruction.FlowControl)
            {
                case FlowControl.Next:
                    if (!Push(instruction.NextIP, fallthrough: true)) return null;
                    break;
                case FlowControl.ConditionalBranch:
                    if (address != prefix[1].IP && instruction.NearBranchTarget < cursor ||
                        !Push(instruction.NearBranchTarget) ||
                        !Push(instruction.NextIP, fallthrough: true))
                        return null;
                    break;
                case FlowControl.UnconditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        !Push(instruction.NearBranchTarget))
                        return null;
                    break;
                case FlowControl.IndirectBranch:
                    if (address != prefix[6].IP ||
                        targets.Any(target => !Push(target)))
                        return null;
                    break;
                case FlowControl.Return:
                    if (!PlainReturn(instruction)) return null;
                    break;
                default:
                    return null;
            }
        }

        // Any unvisited bytes before the table must be inert alignment, not
        // another instruction stream or an unrecognized data island.
        for (var offset = 0; offset < occupied.Length; offset++)
            if (!occupied[offset] &&
                (!section.Map(entry + (ulong)offset, 1, out var raw) ||
                 image[raw] is not (0x90 or 0xCC)))
                return null;

        return new Evidence(entry, prefix[6].IP, table, defaultTarget,
            targets, decoded.Values.OrderBy(instruction => instruction.IP).ToArray());

        bool Push(ulong target, bool fallthrough = false)
        {
            if (target < entry || target >= table ||
                target < cursor && (!fallthrough ||
                    !prefix.Any(part => part.IP == target)))
                return false;
            pending.Push(target);
            return true;
        }
    }

    private static bool TryDispatch(IReadOnlyList<Instruction> code, ulong imageBase,
        out int caseCount, out ulong table, out ulong defaultTarget)
    {
        caseCount = 0;
        table = defaultTarget = 0;
        var compare = code[0];
        var guard = code[1];
        var extend = code[2];
        var loadBase = code[3];
        var loadTarget = code[4];
        var addBase = code[5];
        var jump = code[6];
        if (compare.Mnemonic != Mnemonic.Cmp || compare.Op0Kind != OpKind.Register ||
            compare.Op0Register.GetSize() != 4 ||
            compare.Op1Kind is not (OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate32) ||
            compare.GetImmediate(1) is < 1 or > 255 ||
            guard.Mnemonic != Mnemonic.Ja || guard.FlowControl != FlowControl.ConditionalBranch ||
            guard.Op0Kind != OpKind.NearBranch64 ||
            extend.Code != Code.Movsxd_r64_rm32 ||
            extend.Op0Kind != OpKind.Register || extend.Op1Kind != OpKind.Register ||
            extend.Op1Register != compare.Op0Register ||
            loadBase.Mnemonic != Mnemonic.Lea ||
            loadBase.Op0Kind != OpKind.Register || loadBase.Op0Register.GetSize() != 8 ||
            loadBase.Op1Kind != OpKind.Memory || !loadBase.IsIPRelativeMemoryOperand ||
            loadBase.IPRelativeMemoryAddress != imageBase ||
            loadTarget.Code != Code.Mov_r32_rm32 ||
            loadTarget.Op0Kind != OpKind.Register || loadTarget.Op1Kind != OpKind.Memory ||
            loadTarget.MemoryBase != loadBase.Op0Register ||
            loadTarget.MemoryIndex != extend.Op0Register ||
            loadTarget.MemoryIndexScale != 4 ||
            loadTarget.MemoryDisplacement64 > uint.MaxValue ||
            loadTarget.MemorySize.GetSize() != 4 ||
            addBase.Mnemonic != Mnemonic.Add ||
            addBase.Op0Kind != OpKind.Register || addBase.Op1Kind != OpKind.Register ||
            addBase.Op0Register != loadTarget.Op0Register.GetFullRegister() ||
            addBase.Op1Register != loadBase.Op0Register ||
            jump.FlowControl != FlowControl.IndirectBranch ||
            jump.Op0Kind != OpKind.Register || jump.Op0Register != addBase.Op0Register ||
            imageBase > ulong.MaxValue - loadTarget.MemoryDisplacement64)
            return false;

        caseCount = checked((int)compare.GetImmediate(1) + 1);
        table = imageBase + loadTarget.MemoryDisplacement64;
        defaultTarget = guard.NearBranchTarget;
        return true;
    }

    private static bool SameRegion(X64UnwindProof.SpanClassification current,
        X64UnwindProof.SpanClassification entry)
    {
        if (current.Kind != entry.Kind)
            return false;
        return current.Kind == X64UnwindProof.SpanKind.NoEntry ||
               current.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               current.Start == entry.Start && current.End == entry.End &&
               current.RootStart == entry.RootStart;
    }

    private static bool SafeFrameEffect(Instruction instruction,
        X64UnwindProof.SpanKind region, InstructionInfoFactory instructionInfo)
    {
        if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            return false;
        if (region == X64UnwindProof.SpanKind.HandlerFree || PlainReturn(instruction))
            return true;
        foreach (var used in instructionInfo.GetInfo(instruction).GetUsedRegisters())
        {
            if (used.Access is not (OpAccess.Write or OpAccess.CondWrite or
                OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                continue;
            var register = used.Register.GetFullRegister();
            if (register is Register.RSP or Register.RBP or Register.RBX or Register.RSI or
                Register.RDI or Register.R12 or Register.R13 or Register.R14 or Register.R15 ||
                register is >= Register.XMM6 and <= Register.XMM15 ||
                register is >= Register.YMM6 and <= Register.YMM15 ||
                register is >= Register.ZMM6 and <= Register.ZMM15)
                return false;
        }
        return true;
    }

    private static bool PlainReturn(Instruction instruction) =>
        instruction.Code == Code.Retnq && instruction.OpCount == 0 ||
        instruction.Code == Code.Retnq_imm16 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Immediate16 && instruction.Immediate16 == 0;

    private static bool PlainInstruction(Instruction instruction) =>
        !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
        instruction.Length > 0 && instruction.NextIP > instruction.IP &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == Register.None;

    private static bool TryDecode(ReadOnlySpan<byte> image, Section section,
        ulong address, out Instruction instruction)
    {
        instruction = default;
        if (!section.Map(address, 1, out var offset))
            return false;
        var available = Math.Min(15, section.RawEnd - offset);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(
            image.Slice(offset, available).ToArray()), address);
        instruction = decoder.Decode();
        return instruction.Length > 0 && section.Map(address,
            (uint)instruction.Length, out _);
    }

    private readonly record struct Section(ulong ImageBase, ulong Start, ulong End,
        int RawStart, int RawEnd)
    {
        internal bool Map(ulong address, uint length, out int offset)
        {
            offset = -1;
            if (length == 0 || address < Start || address >= End ||
                (ulong)length > End - address || address - Start > int.MaxValue)
                return false;
            var relative = checked((int)(address - Start));
            if (relative > RawEnd - RawStart - length)
                return false;
            offset = RawStart + relative;
            return true;
        }
    }

    // PE's public virtual-to-raw mapper does not expose write permissions or
    // distinguish a file-backed section from its zero-filled virtual tail.
    // Parse these standard PE32+ section fields from the original file image.
    private static bool TryReadSection(ReadOnlySpan<byte> image, ulong entry,
        ulong expectedImageBase, out Section selected)
    {
        selected = default;
        if (image.Length < 0x100 || U16(image, 0) != 0x5A4D)
            return false;
        var pe = U32(image, 0x3C);
        if (pe > image.Length - 24 || U32(image, (int)pe) != 0x4550 ||
            U16(image, (int)pe + 4) != 0x8664)
            return false;
        var count = U16(image, (int)pe + 6);
        var optionalSize = U16(image, (int)pe + 20);
        var optional = (int)pe + 24;
        if (count is 0 or > 96 || optionalSize < 64 ||
            optional > image.Length - optionalSize ||
            U16(image, optional) != 0x20B ||
            U64(image, optional + 24) != expectedImageBase ||
            expectedImageBase == 0 || entry < expectedImageBase)
            return false;
        var imageSize = U32(image, optional + 56);
        var header = optional + optionalSize;
        if (header > image.Length - count * 40 ||
            entry - expectedImageBase >= imageSize)
            return false;

        var previous = new List<(ulong Start, ulong End)>();
        for (var i = 0; i < count; i++)
        {
            var at = header + i * 40;
            var rva = U32(image, at + 12);
            var size = U32(image, at + 8);
            var rawSize = U32(image, at + 16);
            var rawStart = U32(image, at + 20);
            var flags = U32(image, at + 36);
            if ((ulong)rva + size > imageSize ||
                (ulong)rawStart + rawSize > (ulong)image.Length ||
                previous.Any(prior => rva < prior.End && prior.Start < (ulong)rva + size))
                return false;
            previous.Add((rva, (ulong)rva + size));
            if (entry - expectedImageBase < rva ||
                entry - expectedImageBase >= (ulong)rva + size)
                continue;
            // Readable executable and non-writable, including the embedded table.
            if ((flags & 0xE0000000) != 0x60000000 || rawSize == 0 ||
                expectedImageBase > ulong.MaxValue - rva - size)
                return false;
            selected = new Section(expectedImageBase, expectedImageBase + rva,
                expectedImageBase + rva + size, checked((int)rawStart),
                checked((int)(rawStart + rawSize)));
        }
        return selected.Start != 0 && selected.Map(entry, 1, out _);
    }

    private static ushort U16(ReadOnlySpan<byte> image, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> image, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> image, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(offset, 8));
}
