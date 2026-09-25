using System;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    // The alternate Release layout has the same encoded usage table, but a
    // different literal cache hit/miss sequence and shared commit block.
    private static bool ProveAlternateStringLiteralArm(PE pe,
        X64UnwindProof.Index unwind, ulong core)
    {
        if (Read(pe, unwind, core + 0x37, 15, 0x3a) is not { } dispatch ||
            !TableLoad(dispatch[12], out var tableRva) ||
            !TablePointsTo(pe, unwind, tableRva, 4, out var literalAddress) ||
            literalAddress <= dispatch[^1].NextIP ||
            Read(pe, unwind, literalAddress, 27, 0x7f, lockIndex: 19) is not
                { } code ||
            code[^1].NextIP != literalAddress + 0x7f ||
            !SameRoot(unwind, core, literalAddress, code[^1].NextIP) ||
            Read(pe, unwind, dispatch[^1].NextIP, 5, 0x13) is not { } typeArm ||
            !Jump(typeArm[4]) ||
            code[5].NearBranchTarget != typeArm[4].NearBranchTarget ||
            Read(pe, unwind, typeArm[4].NearBranchTarget, 10, 0x21) is not
                { } commit || !AlternateCommit(commit))
            return false;

        var join = commit[0].IP;
        if (!RipLoad(code[0], Register.RBX, out var literalCache) ||
            !ScaledLea(code[1], Register.RCX, Register.RDI, 8) ||
            code[2].Mnemonic != Mnemonic.Cmp ||
            !Memory(code[2], 0, Register.RBX, Register.RDI, 8, 0, 8) ||
            code[2].GetImmediate(1) != 0 ||
            !Branch(code[3], Mnemonic.Je, code[6].IP) ||
            !LoadIndexed(code[4], Register.RBX, Register.RCX, Register.RBX) ||
            !Jump(code[5]) || code[5].NearBranchTarget != join ||
            !RipLoad(code[6], Register.R8, out var metadataGlobal) ||
            !RipLoad(code[7], Register.RAX, out var headerGlobal) ||
            !SignExtendMemory(code[8], Register.RDX, Register.RAX,
                Register.None, 1, 8) ||
            !SignExtendMemory(code[9], Register.RAX, Register.RAX,
                Register.None, 1, 16) ||
            !BinaryRegister(code[10], Mnemonic.Add, Register.RDX, Register.RCX) ||
            !BinaryRegister(code[11], Mnemonic.Add, Register.RAX, Register.R8) ||
            !SignExtendMemory(code[12], Register.RCX, Register.RDX,
                Register.R8, 1, 4) ||
            !LoadMemory(code[13], Register.EDX, Register.RDX, Register.R8, 1, 0) ||
            !BinaryRegister(code[14], Mnemonic.Add, Register.RCX, Register.RAX) ||
            !DirectCall(code[15]) ||
            !RipLoad(code[16], Register.RCX, out var cacheAgain) ||
            !Move(code[17], Register.RSI, Register.RAX) ||
            !ZeroRegister(code[18], Register.EAX) ||
            code[19].Mnemonic != Mnemonic.Cmpxchg ||
            !code[19].HasLockPrefix ||
            !Memory(code[19], 0, Register.RCX, Register.RDI, 8, 0, 8) ||
            code[19].Op1Kind != OpKind.Register ||
            code[19].Op1Register != Register.RSI ||
            !Move(code[20], Register.RBX, Register.RAX) ||
            !Branch(code[21], Mnemonic.Jne, join) ||
            !RipLoad(code[22], Register.RAX, out var cacheLast) ||
            code[23].Mnemonic != Mnemonic.Lea ||
            code[23].Op0Register != Register.RCX ||
            !Memory(code[23], 1, Register.RAX, Register.RDI, 8, 0, 0) ||
            !DirectCall(code[24]) ||
            !Move(code[25], Register.RBX, Register.RSI) ||
            !Jump(code[26]) || code[26].NearBranchTarget != join ||
            literalCache != cacheAgain || literalCache != cacheLast ||
            literalCache == metadataGlobal || literalCache == headerGlobal ||
            metadataGlobal == headerGlobal ||
            !WritablePointer(unwind, literalCache) ||
            !WritablePointer(unwind, metadataGlobal) ||
            !WritablePointer(unwind, headerGlobal) ||
            !ProveStringNewLenExport(pe, unwind, code[15].NearBranchTarget) ||
            !ProveWriteBarrierExport(pe, unwind, code[24].NearBranchTarget) ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind,
                code[24].NearBranchTarget))
            return false;
        return true;
    }
}
