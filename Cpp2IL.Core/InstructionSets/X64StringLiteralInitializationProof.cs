using System;
using System.Buffers.Binary;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Extends the exact-target runtime metadata proof to the StringLiteral usage
/// arm, including its cache hit, allocation, compare-exchange, and GC barrier paths.
/// </summary>
internal static partial class X64MetadataInitializationHelperProof
{
    internal static bool TryIdentifyStringLiteral(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        try
        {
            // Keep the TypeInfo proof as the common initializer identity check.
            // The literal arm also proves its cache hit, miss, and race paths.
            if (!TryIdentify(app, pe, unwind, target) ||
                Read(pe, unwind, target, 1, 5) is not { } thunk ||
                Read(pe, unwind, thunk[0].NearBranchTarget, 2, 7) is not { } wrapper)
                return false;
            return ProveStringLiteralArm(pe, unwind, wrapper[1].NearBranchTarget);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool ProveStringLiteralArm(PE pe, X64UnwindProof.Index unwind,
        ulong coreAddress)
    {
        // The common decoder decrements the encoded usage kind before indexing
        // this table. StringLiteral has kind 5, so its table entry is 4.
        var dispatchAddress = coreAddress + 0x5d;
        if (Read(pe, unwind, dispatchAddress, 8, 0x23) is not { } dispatch ||
            !TableLoad(dispatch[1], out var tableRva) ||
            !TablePointsTo(pe, unwind, tableRva, dispatch[4].IP) ||
            !TablePointsTo(pe, unwind, tableRva, 4, out var literalAddress) ||
            literalAddress <= dispatch[7].NextIP ||
            Read(pe, unwind, literalAddress, 26, 0x79, lockIndex: 18) is not { } code ||
            code[^1].NextIP != literalAddress + 0x79 ||
            !SameRoot(unwind, coreAddress, literalAddress, code[^1].NextIP) ||
            Read(pe, unwind, dispatch[7].NearBranchTarget, 4, 13,
                allowChainedRegion: true) is not { } commit)
            return false;

        var slotWrite = commit[2].IP;
        var join = commit[0].IP;
        if (!RipLoad(code[0], Register.RAX, out var literalCache) ||
            !ScaledLea(code[1], Register.RDI, Register.RCX, 8) ||
            !LoadIndexed(code[2], Register.RBX, Register.RDI, Register.RAX) ||
            !SameRegisterTest(code[3], Register.RBX) ||
            !Branch(code[4], Mnemonic.Jne, slotWrite) ||
            !RipLoad(code[5], Register.R8, out var metadataGlobal) ||
            !RipLoad(code[6], Register.RAX, out var headerGlobal) ||
            !SignExtendMemory(code[7], Register.RDX, Register.RAX, Register.None, 1, 8) ||
            !SignExtendMemory(code[8], Register.RAX, Register.RAX, Register.None, 1, 16) ||
            !BinaryRegister(code[9], Mnemonic.Add, Register.RDX, Register.RDI) ||
            !BinaryRegister(code[10], Mnemonic.Add, Register.RAX, Register.R8) ||
            !SignExtendMemory(code[11], Register.RCX, Register.RDX, Register.R8, 1, 4) ||
            !LoadMemory(code[12], Register.EDX, Register.RDX, Register.R8, 1, 0) ||
            !BinaryRegister(code[13], Mnemonic.Add, Register.RCX, Register.RAX) ||
            !DirectCall(code[14]) ||
            !RipLoad(code[15], Register.RCX, out var cacheAgain) ||
            !Move(code[16], Register.RBX, Register.RAX) ||
            !ZeroRegister(code[17], Register.EAX) ||
            code[18].Mnemonic != Mnemonic.Cmpxchg ||
            !Memory(code[18], 0, Register.RCX, Register.RDI, 1, 0, 8) ||
            code[18].Op1Kind != OpKind.Register || code[18].Op1Register != Register.RBX ||
            !Branch(code[19], Mnemonic.Jne, code[24].IP) ||
            !RipLoad(code[20], Register.RCX, out var cacheLast) ||
            !BinaryRegister(code[21], Mnemonic.Add, Register.RCX, Register.RDI) ||
            !DirectCall(code[22]) || !Jump(code[23]) ||
            code[23].NearBranchTarget != join ||
            !Move(code[24], Register.RBX, Register.RAX) ||
            !Jump(code[25]) || code[25].NearBranchTarget != slotWrite ||
            literalCache != cacheAgain || literalCache != cacheLast ||
            literalCache == metadataGlobal || literalCache == headerGlobal ||
            metadataGlobal == headerGlobal ||
            !WritablePointer(unwind, literalCache) ||
            !WritablePointer(unwind, metadataGlobal) ||
            !WritablePointer(unwind, headerGlobal) ||
            !ProveStringNewLenExport(pe, unwind, code[14].NearBranchTarget) ||
            !ProveWriteBarrierExport(pe, unwind, code[22].NearBranchTarget) ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind,
                code[22].NearBranchTarget))
            return false;
        return true;
    }

    private static bool ProveStringNewLenExport(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_string_new_len");
        return exported != 0 && Read(pe, unwind, exported, 1, 5) is { } code &&
               Jump(code[0]) && code[0].NearBranchTarget == target &&
               IsSimpleEntry(unwind, exported, code[0].NextIP) &&
               X64NativePaddingProof.HasInt3Padding(pe, code[0].NextIP, exported + 16);
    }

    private static bool ProveWriteBarrierExport(PE pe, X64UnwindProof.Index unwind,
        ulong target)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName(
            "il2cpp_gc_wbarrier_set_field");
        if (exported == 0 ||
            Read(pe, unwind, target, 1, 5) is not { } literalBarrier ||
            !Jump(literalBarrier[0]) ||
            !IsSimpleEntry(unwind, target, literalBarrier[0].NextIP) ||
            !X64NativePaddingProof.HasInt3Padding(pe, literalBarrier[0].NextIP,
                target + 16) ||
            Read(pe, unwind, exported, 3, 11) is not { } api ||
            !Move(api[0], Register.RCX, Register.RDX) ||
            !Move(api[1], Register.RDX, Register.R8) ||
            !Jump(api[2]) ||
            !IsSimpleEntry(unwind, exported, api[2].NextIP) ||
            !X64NativePaddingProof.HasInt3Padding(pe, api[2].NextIP,
                exported + 16) ||
            Read(pe, unwind, api[2].NearBranchTarget, 2, 8) is not { } setter ||
            !Store(setter[0], Register.RCX, 0, Register.RDX) ||
            !Jump(setter[1]) ||
            setter[1].NearBranchTarget != literalBarrier[0].NearBranchTarget ||
            !IsSimpleEntry(unwind, api[2].NearBranchTarget, setter[1].NextIP) ||
            !X64NativePaddingProof.HasInt3Padding(pe, setter[1].NextIP,
                api[2].NearBranchTarget + 16))
            return false;
        return true;
    }

    private static bool TablePointsTo(PE pe, X64UnwindProof.Index unwind, uint tableRva,
        int entryIndex, out ulong armAddress)
    {
        armAddress = 0;
        if (entryIndex is < 0 or > 6 || tableRva > uint.MaxValue - 28 ||
            !unwind.IsExecutableRva(tableRva) ||
            unwind.IsWritableFileBackedRva(tableRva))
            return false;
        var tableAddress = unwind.ImageBase + tableRva;
        var first = pe.MapVirtualAddressToRaw(tableAddress, false);
        var last = pe.MapVirtualAddressToRaw(tableAddress + 27, false);
        var raw = pe.GetRawBinaryContent();
        if (first < 0 || last - first != 27 || last >= raw.Length)
            return false;
        var data = raw.Slice((int)first, 28);
        var entry = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(entryIndex * 4, 4));
        if (entry < 0 || !unwind.IsExecutableRva((uint)entry))
            return false;
        armAddress = unwind.ImageBase + (uint)entry;
        return true;
    }

    private static bool SignExtendMemory(Instruction code, Register destination,
        Register basis, Register index, int scale, ulong offset) =>
        code.Mnemonic == Mnemonic.Movsxd &&
        code.Op0Kind == OpKind.Register && code.Op0Register == destination &&
        Memory(code, 1, basis, index, scale, offset, 4);

    private static bool LoadMemory(Instruction code, Register destination,
        Register basis, Register index, int scale, ulong offset) =>
        code.Mnemonic == Mnemonic.Mov && code.Op0Kind == OpKind.Register &&
        code.Op0Register == destination &&
        Memory(code, 1, basis, index, scale, offset, RegisterWidth(destination));
}
