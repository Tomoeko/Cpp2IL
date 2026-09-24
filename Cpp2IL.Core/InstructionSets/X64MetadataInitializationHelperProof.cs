using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates the exact-target native TypeInfo metadata initializer reached by
/// generated method guards. The key-function lookup supplies only a candidate;
/// the thunk, throw-enabled wrapper, token decoder, TypeInfo arm, and resolver
/// are independently checked against the player bytes.
/// </summary>
internal static partial class X64MetadataInitializationHelperProof
{
    internal static bool TryIdentify(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        try
        {
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !ReferenceEquals(app.Binary, pe) ||
                !ReferenceEquals(X64UnwindProof.ForApplication(app), unwind) ||
                target == 0 ||
                app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata != target ||
                Read(pe, unwind, target, 1, 5) is not { } thunk ||
                !Jump(thunk[0]) || !IsSimpleEntry(unwind, target, thunk[0].NextIP) ||
                !X64NativePaddingProof.HasInt3Padding(pe, thunk[0].NextIP, target + 16))
                return false;

            var wrapperAddress = thunk[0].NearBranchTarget;
            if (Read(pe, unwind, wrapperAddress, 2, 7) is not { } wrapper ||
                !MoveOneToDl(wrapper[0]) || !Jump(wrapper[1]) ||
                !IsSimpleEntry(unwind, wrapperAddress, wrapper[1].NextIP) ||
                !X64NativePaddingProof.HasInt3Padding(pe, wrapper[1].NextIP,
                    wrapperAddress + 16))
                return false;

            return ProveCore(pe, unwind, wrapper[1].NearBranchTarget);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool ProveCore(PE pe, X64UnwindProof.Index unwind, ulong address)
    {
        var firstSpan = unwind.ClassifySpan(address, address + 1);
        if (firstSpan.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            firstSpan.Start != address || firstSpan.RootStart != address ||
            firstSpan.End - address != 0x5d ||
            Read(pe, unwind, address, 27, 0x5d, 7) is not { } first ||
            first[^1].NextIP != firstSpan.End ||
            !CorePrefix(first, unwind.ImageBase))
            return false;

        var next = first[^1].NextIP;
        if (Read(pe, unwind, next, 8, 0x23) is not { } dispatch ||
            dispatch[^1].NextIP != next + 0x23 ||
            !SameRoot(unwind, address, next, dispatch[^1].NextIP) ||
            !Store(dispatch[0], Register.RSP, 0x58, Register.RDI) ||
            !TableLoad(dispatch[1], out var tableRva) ||
            !BinaryRegister(dispatch[2], Mnemonic.Add, Register.RDX, Register.R8) ||
            dispatch[3].Mnemonic != Mnemonic.Jmp ||
            dispatch[3].Op0Kind != OpKind.Register || dispatch[3].Op0Register != Register.RDX ||
            !ZeroExtend(dispatch[4], Register.EDX, Register.R10L) ||
            !DirectCall(dispatch[5]) || !Move(dispatch[6], Register.RBX, Register.RAX) ||
            !Jump(dispatch[7]))
            return false;

        var typeArm = dispatch[4].IP;
        if (!TablePointsTo(pe, unwind, tableRva, typeArm))
            return false;

        var join = dispatch[7].NearBranchTarget;
        if (Read(pe, unwind, join, 4, 13, allowChainedRegion: true) is not { } commit ||
            !ProveNestedCommitUnwind(pe, unwind, address, next, join,
                commit[^1].NextIP) ||
            !SameRegisterTest(commit[0], Register.RBX) ||
            !Branch(commit[1], Mnemonic.Je, commit[3].IP) ||
            !Store(commit[2], Register.R14, 0, Register.RBX) ||
            !Load(commit[3], Register.RDI, Register.RSP, 0x58))
            return false;

        var tail = commit[^1].NextIP;
        if (Read(pe, unwind, tail, 5, 15) is not { } exit ||
            !SameRoot(unwind, address, tail, exit[^1].NextIP) ||
            first[24].NearBranchTarget != tail ||
            !Move(exit[0], Register.RAX, Register.RBX) ||
            !Load(exit[1], Register.RBX, Register.RSP, 0x60) ||
            !Stack(exit[2], Mnemonic.Add, 0x40) ||
            !Pop(exit[3], Register.R14) || !Return(exit[4]) ||
            !ProveTypeResolver(pe, unwind, dispatch[5].NearBranchTarget))
            return false;

        return true;
    }

    private static bool CorePrefix(IReadOnlyList<Instruction> code, ulong imageBase) =>
        Store(code[0], Register.RSP, 0x18, Register.RBX) &&
        Push(code[1], Register.R14) && Stack(code[2], Mnemonic.Sub, 0x40) &&
        ZeroRegister(code[3], Register.EBX) &&
        ZeroExtend(code[4], Register.R10D, Register.DL) &&
        Move(code[5], Register.R9D, Register.EBX) &&
        Move(code[6], Register.R14, Register.RCX) &&
        code[7].Mnemonic == Mnemonic.Xadd && code[7].HasLockPrefix &&
        Memory(code[7], 0, Register.RCX, Register.None, 1, 0, 8) &&
        code[7].Op1Kind == OpKind.Register && code[7].Op1Register == Register.R9 &&
        ZeroExtend(code[8], Register.R8D, Register.R9L) &&
        UnaryRegister(code[9], Mnemonic.Not, Register.R8L) &&
        TestImmediate(code[10], Register.R8L, 1) &&
        Branch(code[11], Mnemonic.Je, code[17].IP) &&
        Move(code[12], Register.RAX, Register.R9) &&
        Load(code[13], Register.RBX, Register.RSP, 0x60) &&
        Stack(code[14], Mnemonic.Add, 0x40) &&
        Pop(code[15], Register.R14) && Return(code[16]) &&
        Move(code[17], Register.ECX, Register.R9D) &&
        Move(code[18], Register.EAX, Register.R9D) &&
        Shift(code[19], Register.ECX, 1) &&
        RegisterImmediate(code[20], Mnemonic.And, Register.ECX, 0x0fff_ffff) &&
        Shift(code[21], Register.EAX, 29) &&
        UnaryRegister(code[22], Mnemonic.Dec, Register.EAX) &&
        RegisterImmediate(code[23], Mnemonic.Cmp, Register.EAX, 6) &&
        code[24].Mnemonic == Mnemonic.Ja && code[24].Op0Kind == OpKind.NearBranch64 &&
        code[25].Mnemonic == Mnemonic.Lea && code[25].Op0Register == Register.R8 &&
        code[25].Op1Kind == OpKind.Memory && code[25].MemoryBase == Register.RIP &&
        code[25].IPRelativeMemoryAddress == imageBase &&
        code[26].Mnemonic == Mnemonic.Cdqe;

    private static bool ProveTypeResolver(PE pe, X64UnwindProof.Index unwind, ulong address)
    {
        var span = unwind.ClassifySpan(address, address + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != address ||
            span.RootStart != address || span.End - address != 0x7c ||
            Read(pe, unwind, address, 33, 0x7c) is not { } code ||
            code[^1].NextIP != span.End || !ResolverEntry(code) ||
            !ResolverCache(code, out var cacheGlobal) ||
            !ResolverLookup(code, out var registrationGlobal,
                out var cacheAgain) ||
            !ResolverReturn(code, out var cacheLast) ||
            cacheGlobal != cacheAgain || cacheGlobal != cacheLast ||
            cacheGlobal == registrationGlobal ||
            !WritablePointer(unwind, cacheGlobal) ||
            !WritablePointer(unwind, registrationGlobal) ||
            !ProveClassFromTypeExport(pe, unwind, code[19].NearBranchTarget) ||
            !ProveClassInitSlow(pe, unwind, code[24].NearBranchTarget))
            return false;
        return true;
    }

    private static bool ResolverEntry(IReadOnlyList<Instruction> code) =>
        Push(code[0], Register.RDI) && Stack(code[1], Mnemonic.Sub, 0x20) &&
        ZeroExtend(code[2], Register.EDI, Register.DL) &&
        RegisterImmediate(code[3], Mnemonic.Cmp, Register.ECX, uint.MaxValue) &&
        Branch(code[4], Mnemonic.Jne, code[9].IP) &&
        ZeroRegister(code[5], Register.EAX) &&
        Stack(code[6], Mnemonic.Add, 0x20) && Pop(code[7], Register.RDI) &&
        Return(code[8]) &&
        SignExtend(code[9], Register.RAX, Register.ECX) &&
        Store(code[10], Register.RSP, 0x30, Register.RBX) &&
        ScaledLea(code[11], Register.RBX, Register.RAX, 8);

    private static bool ResolverCache(IReadOnlyList<Instruction> code,
        out ulong cacheGlobal)
    {
        cacheGlobal = 0;
        return RipLoad(code[12], Register.RAX, out cacheGlobal) &&
            LoadIndexed(code[13], Register.RAX, Register.RBX, Register.RAX) &&
            SameRegisterTest(code[14], Register.RAX) &&
            Branch(code[15], Mnemonic.Jne, code[29].IP);
    }

    private static bool ResolverLookup(IReadOnlyList<Instruction> code,
        out ulong registrationGlobal, out ulong cacheAgain)
    {
        registrationGlobal = 0;
        cacheAgain = 0;
        return RipLoad(code[16], Register.RAX, out registrationGlobal) &&
            Load(code[17], Register.RCX, Register.RAX, 0x38) &&
            LoadIndexed(code[18], Register.RCX, Register.RBX, Register.RCX) &&
            DirectCall(code[19]) &&
            SameRegisterTest(code[20], Register.RAX) &&
            Branch(code[21], Mnemonic.Je, code[27].IP) &&
            ZeroExtend(code[22], Register.EDX, Register.DIL) &&
            Move(code[23], Register.RCX, Register.RAX) &&
            DirectCall(code[24]) &&
            RipLoad(code[25], Register.RCX, out cacheAgain) &&
            StoreIndexed(code[26], Register.RBX, Register.RCX, Register.RAX);
    }

    private static bool ResolverReturn(IReadOnlyList<Instruction> code,
        out ulong cacheLast)
    {
        cacheLast = 0;
        return RipLoad(code[27], Register.RAX, out cacheLast) &&
            LoadIndexed(code[28], Register.RAX, Register.RBX, Register.RAX) &&
            Load(code[29], Register.RBX, Register.RSP, 0x30) &&
            Stack(code[30], Mnemonic.Add, 0x20) && Pop(code[31], Register.RDI) &&
            Return(code[32]);
    }

    private static bool ProveClassFromTypeExport(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_from_type");
        var alias = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_from_il2cpp_type");
        return exported != 0 && alias == exported &&
               Read(pe, unwind, exported, 2, 7) is { } code &&
               MoveOneToDl(code[0]) && Jump(code[1]) && code[1].NearBranchTarget == target &&
               IsSimpleEntry(unwind, exported, code[1].NextIP) &&
               X64NativePaddingProof.HasInt3Padding(pe, code[1].NextIP, exported + 16);
    }

    private static bool ProveClassInitSlow(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var span = unwind.ClassifySpan(target, target + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != target ||
            span.RootStart != target || span.End - target != 0x31 ||
            Read(pe, unwind, target, 16, 0x31) is not { } code ||
            code[^1].NextIP != span.End ||
            !Push(code[0], Register.RBX) || !Stack(code[1], Mnemonic.Sub, 0x20) ||
            !Move(code[2], Register.RBX, Register.RCX) ||
            !SameRegisterTest(code[3], Register.DL) ||
            !Branch(code[4], Mnemonic.Je, code[8].IP) ||
            !Stack(code[5], Mnemonic.Add, 0x20) || !Pop(code[6], Register.RBX) ||
            !Jump(code[7]) || !DirectCall(code[8]) ||
            !ZeroRegister(code[9], Register.EAX) ||
            !MemoryRegisterCompare(code[10], Register.RBX, 0xd8, Register.EAX) ||
            !ConditionalMove(code[11], Register.RBX, Register.RAX) ||
            !Move(code[12], Register.RAX, Register.RBX) ||
            !Stack(code[13], Mnemonic.Add, 0x20) ||
            !Pop(code[14], Register.RBX) || !Return(code[15]))
            return false;

        // The throw-enabled arm calls the same Class::Init target as the
        // null-return arm, then either returns the class or raises the saved
        // initialization exception through independently anchored exports.
        var throwing = code[7].NearBranchTarget;
        var directSpan = unwind.ClassifySpan(throwing, throwing + 1);
        if (directSpan.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            directSpan.Start != throwing || directSpan.RootStart != throwing ||
            directSpan.End - throwing != 0x31 ||
            Read(pe, unwind, throwing, 15, 0x30) is not { } direct ||
            direct[^1].NextIP != throwing + 0x30 ||
            !X64NativePaddingProof.HasInt3Padding(pe, direct[^1].NextIP,
                directSpan.End) ||
            !Push(direct[0], Register.RBX) || !Stack(direct[1], Mnemonic.Sub, 0x20) ||
            !Move(direct[2], Register.RBX, Register.RCX) ||
            !DirectCall(direct[3]) ||
            direct[3].NearBranchTarget != code[8].NearBranchTarget ||
            !Load(direct[4], Register.ECX, Register.RBX, 0xd8) ||
            !SameRegisterTest(direct[5], Register.ECX) ||
            !Branch(direct[6], Mnemonic.Jne, direct[11].IP) ||
            !Move(direct[7], Register.RAX, Register.RBX) ||
            !Stack(direct[8], Mnemonic.Add, 0x20) ||
            !Pop(direct[9], Register.RBX) || !Return(direct[10]) ||
            !DirectCall(direct[11]) ||
            !Move(direct[12], Register.RCX, Register.RAX) ||
            !ZeroRegister(direct[13], Register.EDX) ||
            !DirectCall(direct[14]) ||
            !ProveGetTargetExport(pe, unwind, direct[11].NearBranchTarget) ||
            !ProveRaiseExport(pe, unwind, direct[14].NearBranchTarget) ||
            !ProveClassInitFromObjectNewExport(pe, unwind,
                direct[3].NearBranchTarget))
            return false;
        return true;
    }

    private static bool ProveClassInitFromObjectNewExport(PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        // Class::Init has its own exception handler for initialization cleanup.
        // Require that actual handler-bearing function entry, not an interior
        // address or an unchecked code pointer.
        if (target < unwind.ImageBase ||
            target - unwind.ImageBase > uint.MaxValue ||
            !unwind.IsExecutableRva((uint)(target - unwind.ImageBase)) ||
            unwind.GetHandler(target) is not { Start: var classInitStart,
                End: var classInitEnd } ||
            classInitStart != target || classInitEnd <= target)
            return false;
        var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_object_new");
        if (exported == 0 ||
            Read(pe, unwind, exported, 6, 18, allowChainedRegion: true) is not { } api ||
            !Stack(api[0], Mnemonic.Sub, 0x28) || !DirectCall(api[1]) ||
            !Jump(api[2]) || api[2].NearBranchTarget != api[4].IP ||
            !ZeroRegister(api[3], Register.EAX) ||
            !Stack(api[4], Mnemonic.Add, 0x28) || !Return(api[5]) ||
            unwind.GetHandler(exported) is not
                { Start: var exportStart, End: var exportEnd } ||
            exportStart != exported || exportEnd < api[^1].NextIP ||
            !X64NativePaddingProof.HasInt3Padding(pe, api[^1].NextIP,
                exportEnd))
            return false;

        var objectNew = api[1].NearBranchTarget;
        var span = unwind.ClassifySpan(objectNew, objectNew + 1);
        return span.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               span.Start == objectNew && span.RootStart == objectNew &&
               Read(pe, unwind, objectNew, 5, 18) is { } entry &&
               entry[^1].NextIP <= span.End &&
               SameRoot(unwind, objectNew, objectNew, entry[^1].NextIP) &&
               Store(entry[0], Register.RSP, 0x10, Register.RBX) &&
               Push(entry[1], Register.RDI) &&
               Stack(entry[2], Mnemonic.Sub, 0x20) &&
               Move(entry[3], Register.RBX, Register.RCX) &&
               DirectCall(entry[4]) && entry[4].NearBranchTarget == target;
    }

    private static bool ProveGetTargetExport(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_gchandle_get_target");
        return exported != 0 &&
               Read(pe, unwind, exported, 1, 5) is { } code &&
               Jump(code[0]) && code[0].NearBranchTarget == target &&
               IsSimpleEntry(unwind, exported, code[0].NextIP) &&
               X64NativePaddingProof.HasInt3Padding(pe, code[0].NextIP, exported + 16);
    }

    private static bool ProveRaiseExport(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_raise_exception");
        if (exported == 0 ||
            Read(pe, unwind, exported, 3, 11) is not { } code ||
            !Stack(code[0], Mnemonic.Sub, 0x28) ||
            !ZeroRegister(code[1], Register.EDX) ||
            !DirectCall(code[2]) || code[2].NearBranchTarget != target ||
            !X64NativePaddingProof.HasInt3Padding(pe, code[2].NextIP,
                exported + 16))
            return false;
        var span = unwind.ClassifySpan(exported, code[2].NextIP);
        return span.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               span.Start == exported && span.RootStart == exported &&
               span.End == exported + 12;
    }

    private static IReadOnlyList<Instruction>? Read(PE pe, X64UnwindProof.Index unwind,
        ulong address, int count, int bytes, int lockIndex = -1,
        bool allowChainedRegion = false)
    {
        if (count <= 0 || bytes <= 0 || address < unwind.ImageBase ||
            address > ulong.MaxValue - (ulong)bytes ||
            address - unwind.ImageBase > uint.MaxValue ||
            !unwind.IsExecutableRva((uint)(address - unwind.ImageBase)))
            return null;
        var start = pe.MapVirtualAddressToRaw(address, false);
        var end = pe.MapVirtualAddressToRaw(address + (ulong)bytes - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (start < 0 || end - start != bytes - 1 || end >= raw.Length)
            return null;
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(raw.Slice((int)start, bytes).ToArray()), address);
        var result = new Instruction[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = decoder.Decode();
            if (result[index].IsInvalid || result[index].CodeSize != CodeSize.Code64 ||
                result[index].IP != (index == 0 ? address : result[index - 1].NextIP) ||
                result[index].NextIP > address + (ulong)bytes ||
                result[index].HasLockPrefix != (index == lockIndex) ||
                result[index].HasRepPrefix || result[index].HasRepnePrefix ||
                result[index].SegmentPrefix != Register.None ||
                !allowChainedRegion &&
                unwind.ClassifySpan(result[index].IP, result[index].NextIP).Kind ==
                X64UnwindProof.SpanKind.Unsupported)
                return null;
        }
        return result;
    }

    private static bool TablePointsTo(PE pe, X64UnwindProof.Index unwind, uint tableRva,
        ulong typeArm)
    {
        if (tableRva > uint.MaxValue - 28 || !unwind.IsExecutableRva(tableRva) ||
            unwind.IsWritableFileBackedRva(tableRva))
            return false;
        var tableAddress = unwind.ImageBase + tableRva;
        var first = pe.MapVirtualAddressToRaw(tableAddress, false);
        var last = pe.MapVirtualAddressToRaw(tableAddress + 27, false);
        var raw = pe.GetRawBinaryContent();
        if (first < 0 || last - first != 27 || last >= raw.Length)
            return false;
        var data = raw.Slice((int)first, 28);
        var entry = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
        return entry >= 0 && unwind.ImageBase + (uint)entry == typeArm;
    }

    private readonly record struct FunctionRecord(uint Start, uint End, uint UnwindRva);

    // This exact MSVC switch places the TypeInfo commit in a chain-to-chain
    // UNW_FLAG_CHAININFO region. The shared unwind index deliberately rejects
    // nested chains, so validate the two links here before reading that span.
    private static bool ProveNestedCommitUnwind(PE pe, X64UnwindProof.Index unwind,
        ulong rootAddress, ulong directAddress, ulong commitAddress, ulong commitEnd)
    {
        if (rootAddress < unwind.ImageBase || directAddress < unwind.ImageBase ||
            commitAddress < unwind.ImageBase || commitEnd <= commitAddress ||
            commitEnd - unwind.ImageBase > uint.MaxValue ||
            !TryExceptionTable(pe, unwind, out var tableOffset, out var count))
            return false;
        var rootRva = (uint)(rootAddress - unwind.ImageBase);
        var directRva = (uint)(directAddress - unwind.ImageBase);
        var commitRva = (uint)(commitAddress - unwind.ImageBase);
        var raw = pe.GetRawBinaryContent();
        if (!FindFunction(raw, tableOffset, count, rootRva, out var root) ||
            root.Start != rootRva ||
            !FindFunction(raw, tableOffset, count, directRva, out var direct) ||
            direct.Start != directRva ||
            !FindFunction(raw, tableOffset, count, commitRva, out var commit) ||
            commit.Start >= commitRva || commit.End != commitEnd - unwind.ImageBase ||
            unwind.ClassifySpan(rootAddress, rootAddress + 1) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree, RootStart: var rootStart } ||
            rootStart != rootAddress ||
            !SameRoot(unwind, rootAddress, directAddress, directAddress + 1) ||
            !TryChain(pe, unwind, direct.UnwindRva, out var directParent,
                requireNoUnwindCodes: false) || directParent != root ||
            !TryChain(pe, unwind, commit.UnwindRva, out var commitParent,
                requireNoUnwindCodes: true) || commitParent != direct)
            return false;
        return true;
    }

    private static bool TryExceptionTable(PE pe, X64UnwindProof.Index unwind,
        out int offset, out int count)
    {
        offset = count = 0;
        var raw = pe.GetRawBinaryContent();
        if (raw.Length < 0x40)
            return false;
        var header = ReadU32(raw, 0x3c);
        if (header > raw.Length - 24 - 144 ||
            ReadU32(raw, (int)header) != 0x4550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice((int)header + 24, 2)) != 0x20b ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice((int)header + 20, 2)) < 144)
            return false;
        // PE32+ optional header's fourth data directory is the exception table.
        var directory = (int)header + 24 + 112 + 3 * 8;
        var rva = ReadU32(raw, directory);
        var size = ReadU32(raw, directory + 4);
        if (rva == 0 || size < 12 || size > 4_000_000 || size % 12 != 0 ||
            rva > uint.MaxValue - size ||
            !unwind.IsReadableFileBackedRva(rva))
            return false;
        var start = pe.MapVirtualAddressToRaw(unwind.ImageBase + rva, false);
        var end = pe.MapVirtualAddressToRaw(unwind.ImageBase + rva + size - 1, false);
        if (start < 0 || end - start != size - 1 || end >= raw.Length)
            return false;
        offset = (int)start;
        count = (int)(size / 12);
        return true;
    }

    private static bool FindFunction(ReadOnlySpan<byte> raw, int tableOffset,
        int count, uint rva, out FunctionRecord result)
    {
        result = default;
        var low = 0;
        var high = count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var at = tableOffset + middle * 12;
            if (ReadU32(raw, at + 4) <= rva) low = middle + 1;
            else high = middle;
        }
        if (low == count)
            return false;
        var index = tableOffset + low * 12;
        result = new(ReadU32(raw, index), ReadU32(raw, index + 4),
            ReadU32(raw, index + 8));
        return result.Start <= rva && rva < result.End;
    }

    private static bool TryChain(PE pe, X64UnwindProof.Index unwind,
        uint unwindRva, out FunctionRecord parent, bool requireNoUnwindCodes)
    {
        parent = default;
        var address = unwind.ImageBase + unwindRva;
        var at = pe.MapVirtualAddressToRaw(address, false);
        var raw = pe.GetRawBinaryContent();
        if (at < 0 || at > raw.Length - 16 || (raw[(int)at] & 7) != 1 ||
            raw[(int)at] >> 3 != 4 ||
            !unwind.IsReadableFileBackedRva(unwindRva))
            return false;
        var codeCount = raw[(int)at + 2];
        if (requireNoUnwindCodes && (raw[(int)at + 1] != 0 ||
                                     codeCount != 0 || raw[(int)at + 3] != 0))
            return false;
        var chainOffset = 4 + (codeCount + 1) / 2 * 4;
        if (unwindRva > uint.MaxValue - (uint)chainOffset - 11)
            return false;
        var last = pe.MapVirtualAddressToRaw(address + (uint)chainOffset + 11, false);
        if (last != at + chainOffset + 11 || last >= raw.Length ||
            !unwind.IsReadableFileBackedRva(unwindRva + (uint)chainOffset + 11))
            return false;
        var chain = (int)at + chainOffset;
        parent = new(ReadU32(raw, chain), ReadU32(raw, chain + 4),
            ReadU32(raw, chain + 8));
        return parent.Start < parent.End && parent.UnwindRva != 0;
    }

    private static uint ReadU32(ReadOnlySpan<byte> raw, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(offset, 4));

    private static bool SameRoot(X64UnwindProof.Index unwind, ulong root,
        ulong start, ulong end) => unwind.ClassifySpan(start, end) is
        { Kind: X64UnwindProof.SpanKind.HandlerFree, RootStart: var actual } && actual == root;

    private static bool IsSimpleEntry(X64UnwindProof.Index unwind, ulong start,
        ulong end) => unwind.ClassifySpan(start, end).Kind == X64UnwindProof.SpanKind.NoEntry;

    private static bool WritablePointer(X64UnwindProof.Index unwind, ulong address) =>
        address >= unwind.ImageBase && address - unwind.ImageBase <= uint.MaxValue - 7 &&
        unwind.IsWritableVirtualRangeInOneSection((uint)(address - unwind.ImageBase), 8);

    private static bool TableLoad(Instruction code, out uint rva)
    {
        rva = 0;
        if (code.Mnemonic != Mnemonic.Mov || code.Op0Register != Register.EDX ||
            code.Op1Kind != OpKind.Memory || code.MemoryBase != Register.R8 ||
            code.MemoryIndex != Register.RAX || code.MemoryIndexScale != 4 ||
            code.MemoryDisplacement64 > uint.MaxValue || code.MemorySize.GetSize() != 4)
            return false;
        rva = (uint)code.MemoryDisplacement64;
        return true;
    }

    private static bool MoveOneToDl(Instruction code) =>
        code.Mnemonic == Mnemonic.Mov && code.Op0Register == Register.DL &&
        code.Op1Kind == OpKind.Immediate8 && code.Immediate8 == 1;

    private static bool Memory(Instruction code, int operand, Register basis,
        Register index, int scale, ulong offset, int width) =>
        code.GetOpKind(operand) == OpKind.Memory && code.MemoryBase == basis &&
        code.MemoryIndex == index && code.MemoryIndexScale == scale &&
        code.MemoryDisplacement64 == offset &&
        (width == 0 || code.MemorySize.GetSize() == width);

    private static bool Store(Instruction code, Register basis, ulong offset,
        Register source) => code.Mnemonic == Mnemonic.Mov &&
        Memory(code, 0, basis, Register.None, 1, offset, RegisterWidth(source)) &&
        code.Op1Kind == OpKind.Register && code.Op1Register == source;

    private static bool Load(Instruction code, Register destination, Register basis,
        ulong offset) => code.Mnemonic == Mnemonic.Mov &&
        code.Op0Kind == OpKind.Register && code.Op0Register == destination &&
        Memory(code, 1, basis, Register.None, 1, offset, RegisterWidth(destination));

    private static bool LoadIndexed(Instruction code, Register destination,
        Register basis, Register index) => code.Mnemonic == Mnemonic.Mov &&
        code.Op0Register == destination &&
        Memory(code, 1, basis, index, 1, 0, RegisterWidth(destination));

    private static bool StoreIndexed(Instruction code, Register basis, Register index,
        Register value) => code.Mnemonic == Mnemonic.Mov &&
        Memory(code, 0, basis, index, 1, 0, RegisterWidth(value)) &&
        code.Op1Register == value;

    private static bool RipLoad(Instruction code, Register destination, out ulong address)
    {
        address = code.IPRelativeMemoryAddress;
        return code.Mnemonic == Mnemonic.Mov && code.Op0Register == destination &&
               code.Op1Kind == OpKind.Memory && code.MemoryBase == Register.RIP &&
               code.MemoryIndex == Register.None && code.MemorySize.GetSize() == 8;
    }

    private static bool Move(Instruction code, Register destination, Register source) =>
        code.Mnemonic == Mnemonic.Mov && code.Op0Kind == OpKind.Register &&
        code.Op0Register == destination && code.Op1Kind == OpKind.Register &&
        code.Op1Register == source;

    private static bool ZeroExtend(Instruction code, Register destination,
        Register source) => code.Mnemonic == Mnemonic.Movzx &&
        code.Op0Register == destination && code.Op1Register == source;

    private static bool SignExtend(Instruction code, Register destination,
        Register source) => code.Mnemonic == Mnemonic.Movsxd &&
        code.Op0Register == destination && code.Op1Register == source;

    private static bool ZeroRegister(Instruction code, Register register) =>
        code.Mnemonic == Mnemonic.Xor && code.Op0Register == register &&
        code.Op1Register == register;

    private static bool SameRegisterTest(Instruction code, Register register) =>
        code.Mnemonic == Mnemonic.Test && code.Op0Register == register &&
        code.Op1Register == register;

    private static bool TestImmediate(Instruction code, Register register,
        ulong value) => code.Mnemonic == Mnemonic.Test &&
        code.Op0Register == register && code.GetImmediate(1) == value;

    private static bool UnaryRegister(Instruction code, Mnemonic mnemonic,
        Register register) => code.Mnemonic == mnemonic &&
        code.Op0Kind == OpKind.Register && code.Op0Register == register && code.OpCount == 1;

    private static bool RegisterImmediate(Instruction code, Mnemonic mnemonic,
        Register register, ulong value) => code.Mnemonic == mnemonic &&
        code.Op0Kind == OpKind.Register && code.Op0Register == register &&
        (uint)code.GetImmediate(1) == (uint)value;

    private static bool Shift(Instruction code, Register register, ulong amount) =>
        code.Mnemonic == Mnemonic.Shr && code.Op0Register == register &&
        (code.OpCount == 1 && amount == 1 ||
         code.OpCount == 2 && code.GetImmediate(1) == amount);

    private static bool Stack(Instruction code, Mnemonic mnemonic, ulong amount) =>
        RegisterImmediate(code, mnemonic, Register.RSP, amount);

    private static bool Push(Instruction code, Register register) =>
        code.Mnemonic == Mnemonic.Push && code.Op0Register == register;

    private static bool Pop(Instruction code, Register register) =>
        code.Mnemonic == Mnemonic.Pop && code.Op0Register == register;

    private static bool Return(Instruction code) =>
        code.Code == Code.Retnq && code.OpCount == 0;

    private static bool DirectCall(Instruction code) =>
        code.Code == Code.Call_rel32_64 && code.NearBranchTarget != 0;

    private static bool Jump(Instruction code) =>
        code.Mnemonic == Mnemonic.Jmp && code.Op0Kind == OpKind.NearBranch64 &&
        code.NearBranchTarget != 0;

    private static bool Branch(Instruction code, Mnemonic mnemonic, ulong target) =>
        code.Mnemonic == mnemonic && code.Op0Kind == OpKind.NearBranch64 &&
        code.NearBranchTarget == target;

    private static bool BinaryRegister(Instruction code, Mnemonic mnemonic,
        Register destination, Register source) => code.Mnemonic == mnemonic &&
        code.Op0Register == destination && code.Op1Register == source;

    private static bool ScaledLea(Instruction code, Register destination,
        Register index, int scale) => code.Mnemonic == Mnemonic.Lea &&
        code.Op0Register == destination &&
        Memory(code, 1, Register.None, index, scale, 0, 0);

    private static bool MemoryRegisterCompare(Instruction code, Register basis,
        ulong offset, Register source) => code.Mnemonic == Mnemonic.Cmp &&
        Memory(code, 0, basis, Register.None, 1, offset, RegisterWidth(source)) &&
        code.Op1Register == source;

    private static int RegisterWidth(Register register) => register switch
    {
        Register.AL or Register.BL or Register.CL or Register.DL or Register.DIL or
            Register.R8L or Register.R9L or Register.R10L => 1,
        Register.EAX or Register.EBX or Register.ECX or Register.EDX or
            Register.EDI or Register.R8D or Register.R9D or Register.R10D => 4,
        Register.RAX or Register.RBX or Register.RCX or Register.RDX or
            Register.RDI or Register.RSP or Register.R8 or Register.R9 or
            Register.R10 or Register.R14 => 8,
        _ => 0,
    };

    private static bool ConditionalMove(Instruction code, Register destination,
        Register source) => code.Mnemonic == Mnemonic.Cmovne &&
        code.Op0Register == destination && code.Op1Register == source;
}
