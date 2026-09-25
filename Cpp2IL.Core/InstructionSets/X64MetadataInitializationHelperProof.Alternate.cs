using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    private static bool TryIdentifyAlternateMethodDefArm(ApplicationAnalysisContext app,
        PE pe, X64UnwindProof.Index unwind, ulong target)
    {
        try
        {
            return TryFindCore(app, pe, unwind, target, out var core) &&
                   ProveAlternateCore(pe, unwind, core);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    // The same Unity runtime source can be laid out differently by the Release
    // toolchain. This route proves the token test, both used switch arms, the
    // cache commit, and the TypeInfo/MethodDef resolvers from native instructions.
    private static bool ProveAlternateCore(PE pe, X64UnwindProof.Index unwind, ulong core)
    {
        var firstSpan = unwind.ClassifySpan(core, core + 1);
        if (firstSpan.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            firstSpan.Start != core || firstSpan.RootStart != core ||
            firstSpan.End - core != 0x37 ||
            Read(pe, unwind, core, 17, 0x37, lockIndex: 7) is not { } first ||
            !AlternateCorePrefix(first, core + 0x37))
            return false;

        var next = firstSpan.End;
        if (Read(pe, unwind, next, 15, 0x3a) is not { } dispatch ||
            !SameRootInstructions(unwind, core, dispatch) ||
            !AlternateDispatch(dispatch, unwind.ImageBase, out var tableRva))
            return false;

        // The table indices are the usage tag minus one: TypeInfo is zero,
        // MethodDef is two, and MethodRef shares the MethodDef destination.
        var typeArmAddress = dispatch[^1].NextIP;
        if (!TablePointsTo(pe, unwind, tableRva, typeArmAddress))
            return false;
        if (!TryReadMethodDefSwitchArm(pe, unwind, tableRva, out var methodArmAddress))
            return false;
        if (typeArmAddress == methodArmAddress ||
            Read(pe, unwind, typeArmAddress, 5, 0x13) is not { } typeArm ||
            Read(pe, unwind, methodArmAddress, 3, 0x0d) is not { } methodArm)
            return false;
        if (!SameRoot(unwind, core, typeArmAddress, typeArm[^1].NextIP) ||
            !SameRoot(unwind, core, methodArmAddress, methodArm[^1].NextIP) ||
            !AlternateTypeArm(typeArm) || !AlternateMethodArm(methodArm))
            return false;

        var join = typeArm[4].NearBranchTarget;
        if (Read(pe, unwind, join, 10, 0x21) is not { } exit)
            return false;
        if (methodArm[2].NearBranchTarget != join ||
            !SameRootInstructions(unwind, core, exit) ||
            !AlternateCommit(exit) ||
            !Branch(dispatch[8], Mnemonic.Ja, exit[4].IP) ||
            !ProveAlternateTypeResolver(pe, unwind, typeArm[2].NearBranchTarget) ||
            !ProveAlternateEncodedMethodResolver(pe, unwind,
                methodArm[0].NearBranchTarget))
            return false;
        return true;
    }

    private static bool AlternateCorePrefix(IReadOnlyList<Instruction> code,
        ulong dispatch) =>
        Store(code[0], Register.RSP, 0x18, Register.RBX) &&
        Push(code[1], Register.R14) && Stack(code[2], Mnemonic.Sub, 0x40) &&
        ZeroRegister(code[3], Register.EBX) &&
        Move(code[4], Register.R14, Register.RCX) &&
        Move(code[5], Register.ECX, Register.EBX) &&
        ZeroExtend(code[6], Register.R9D, Register.DL) &&
        code[7].Mnemonic == Mnemonic.Xadd && code[7].HasLockPrefix &&
        Memory(code[7], 0, Register.R14, Register.None, 1, 0, 8) &&
        code[7].Op1Kind == OpKind.Register && code[7].Op1Register == Register.RCX &&
        ZeroExtend(code[8], Register.R8D, Register.CL) &&
        UnaryRegister(code[9], Mnemonic.Not, Register.R8L) &&
        TestImmediate(code[10], Register.R8L, 1) &&
        Branch(code[11], Mnemonic.Je, dispatch) &&
        Move(code[12], Register.RAX, Register.RCX) &&
        Load(code[13], Register.RBX, Register.RSP, 0x60) &&
        Stack(code[14], Mnemonic.Add, 0x40) &&
        Pop(code[15], Register.R14) && Return(code[16]);

    private static bool SameRootInstructions(X64UnwindProof.Index unwind,
        ulong root, IReadOnlyList<Instruction> code)
    {
        foreach (var instruction in code)
            if (!SameRoot(unwind, root, instruction.IP, instruction.NextIP))
                return false;
        return true;
    }

    private static bool AlternateDispatch(IReadOnlyList<Instruction> code,
        ulong imageBase, out uint tableRva)
    {
        tableRva = 0;
        return Store(code[0], Register.RSP, 0x58, Register.RDI) &&
            Move(code[1], Register.EAX, Register.ECX) &&
            Shift(code[2], Register.EAX, 29) &&
            Move(code[3], Register.EDI, Register.ECX) &&
            Shift(code[4], Register.EDI, 1) &&
            RegisterImmediate(code[5], Mnemonic.And, Register.EDI, 0x0fff_ffff) &&
            UnaryRegister(code[6], Mnemonic.Dec, Register.EAX) &&
            RegisterImmediate(code[7], Mnemonic.Cmp, Register.EAX, 6) &&
            code[8].Mnemonic == Mnemonic.Ja && code[8].Op0Kind == OpKind.NearBranch64 &&
            code[9].Mnemonic == Mnemonic.Lea && code[9].Op0Register == Register.R8 &&
            code[9].Op1Kind == OpKind.Memory && code[9].MemoryBase == Register.RIP &&
            code[9].IPRelativeMemoryAddress == imageBase &&
            code[10].Mnemonic == Mnemonic.Cdqe &&
            Store(code[11], Register.RSP, 0x50, Register.RSI) &&
            TableLoad(code[12], out tableRva) &&
            BinaryRegister(code[13], Mnemonic.Add, Register.RDX, Register.R8) &&
            code[14].Mnemonic == Mnemonic.Jmp && code[14].Op0Kind == OpKind.Register &&
            code[14].Op0Register == Register.RDX;
    }

    private static bool AlternateTypeArm(IReadOnlyList<Instruction> code) =>
        ZeroExtend(code[0], Register.EDX, Register.R9L) &&
        Move(code[1], Register.ECX, Register.EDI) &&
        DirectCall(code[2]) && Move(code[3], Register.RBX, Register.RAX) &&
        Jump(code[4]);

    private static bool AlternateMethodArm(IReadOnlyList<Instruction> code) =>
        DirectCall(code[0]) && Move(code[1], Register.RBX, Register.RAX) &&
        Jump(code[2]);

    private static bool AlternateCommit(IReadOnlyList<Instruction> code) =>
        SameRegisterTest(code[0], Register.RBX) &&
        Branch(code[1], Mnemonic.Je, code[3].IP) &&
        Store(code[2], Register.R14, 0, Register.RBX) &&
        Load(code[3], Register.RSI, Register.RSP, 0x50) &&
        Load(code[4], Register.RDI, Register.RSP, 0x58) &&
        Move(code[5], Register.RAX, Register.RBX) &&
        Load(code[6], Register.RBX, Register.RSP, 0x60) &&
        Stack(code[7], Mnemonic.Add, 0x40) &&
        Pop(code[8], Register.R14) && Return(code[9]);

    private static bool ProveAlternateTypeResolver(PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        var span = unwind.ClassifySpan(target, target + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != target || span.RootStart != target ||
            span.End - target != 0x8d ||
            Read(pe, unwind, target, 38, 0x8d) is not { } code ||
            !AlternateTypeResolverEntry(code) ||
            !AlternateTypeResolverCache(code, out var cache) ||
            !AlternateTypeResolverLookup(code, out var registration,
                out var cacheAgain) ||
            !AlternateTypeResolverReturn(code, out var cacheLast) ||
            cache != cacheAgain || cache != cacheLast ||
            cache == registration || !WritablePointer(unwind, cache) ||
            !WritablePointer(unwind, registration) ||
            !ProveClassFromTypeExport(pe, unwind, code[24].NearBranchTarget) ||
            !ProveClassInitSlow(pe, unwind, code[29].NearBranchTarget))
            return false;
        return true;
    }

    private static bool AlternateTypeResolverEntry(IReadOnlyList<Instruction> code) =>
        Store(code[0], Register.RSP, 8, Register.RBX) &&
        Push(code[1], Register.RDI) && Stack(code[2], Mnemonic.Sub, 0x20) &&
        SignExtend(code[3], Register.RBX, Register.ECX) &&
        ZeroExtend(code[4], Register.EDI, Register.DL) &&
        RegisterImmediate(code[5], Mnemonic.Cmp, Register.EBX, uint.MaxValue) &&
        Branch(code[6], Mnemonic.Jne, code[12].IP) &&
        ZeroRegister(code[7], Register.EAX) &&
        Load(code[8], Register.RBX, Register.RSP, 0x30) &&
        Stack(code[9], Mnemonic.Add, 0x20) && Pop(code[10], Register.RDI) &&
        Return(code[11]);

    private static bool AlternateTypeResolverCache(IReadOnlyList<Instruction> code,
        out ulong cache)
    {
        cache = 0;
        return RipLoad(code[12], Register.RAX, out cache) &&
            ScaledLea(code[13], Register.R8, Register.RBX, 8) &&
            code[14].Mnemonic == Mnemonic.Cmp &&
            Memory(code[14], 0, Register.RAX, Register.RBX, 8, 0, 8) &&
            code[14].GetImmediate(1) == 0 &&
            Branch(code[15], Mnemonic.Je, code[21].IP) &&
            LoadIndexed(code[16], Register.RAX, Register.R8, Register.RAX) &&
            Load(code[17], Register.RBX, Register.RSP, 0x30) &&
            Stack(code[18], Mnemonic.Add, 0x20) && Pop(code[19], Register.RDI) &&
            Return(code[20]);
    }

    private static bool AlternateTypeResolverLookup(IReadOnlyList<Instruction> code,
        out ulong registration, out ulong cacheAgain)
    {
        registration = cacheAgain = 0;
        return RipLoad(code[21], Register.RAX, out registration) &&
            Load(code[22], Register.RCX, Register.RAX, 0x38) &&
            LoadIndexed(code[23], Register.RCX, Register.R8, Register.RCX) &&
            DirectCall(code[24]) &&
            SameRegisterTest(code[25], Register.RAX) &&
            Branch(code[26], Mnemonic.Je, code[32].IP) &&
            ZeroExtend(code[27], Register.EDX, Register.DIL) &&
            Move(code[28], Register.RCX, Register.RAX) &&
            DirectCall(code[29]) &&
            RipLoad(code[30], Register.RCX, out cacheAgain) &&
            code[31].Mnemonic == Mnemonic.Mov &&
            Memory(code[31], 0, Register.RCX, Register.RBX, 8, 0, 8) &&
            code[31].Op1Register == Register.RAX;
    }

    private static bool AlternateTypeResolverReturn(IReadOnlyList<Instruction> code,
        out ulong cacheLast)
    {
        cacheLast = 0;
        return RipLoad(code[32], Register.RAX, out cacheLast) &&
            code[33].Mnemonic == Mnemonic.Mov && code[33].Op0Register == Register.RAX &&
            Memory(code[33], 1, Register.RAX, Register.RBX, 8, 0, 8) &&
            Load(code[34], Register.RBX, Register.RSP, 0x30) &&
            Stack(code[35], Mnemonic.Add, 0x20) && Pop(code[36], Register.RDI) &&
            Return(code[37]);
    }

    private static bool ProveAlternateEncodedMethodResolver(PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        var span = unwind.ClassifySpan(target, target + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != target || span.RootStart != target ||
            span.End - target != 0x58 ||
            Read(pe, unwind, target, 29, 0x58) is not { } code ||
            !Stack(code[0], Mnemonic.Sub, 0x28) ||
            !Move(code[1], Register.EAX, Register.ECX) ||
            !Shift(code[2], Register.ECX, 29) ||
            !Shift(code[3], Register.EAX, 1) ||
            !RegisterImmediate(code[4], Mnemonic.And, Register.EAX, 0x0fff_ffff) ||
            !SameRegisterTest(code[5], Register.ECX) ||
            !Branch(code[6], Mnemonic.Je, code[19].IP) ||
            !RegisterImmediate(code[7], Mnemonic.Cmp, Register.ECX, 3) ||
            !Branch(code[8], Mnemonic.Je, code[16].IP) ||
            !RegisterImmediate(code[9], Mnemonic.Cmp, Register.ECX, 6) ||
            !Branch(code[10], Mnemonic.Jne, code[26].IP) ||
            !Move(code[11], Register.ECX, Register.EAX) ||
            !DirectCall(code[12]) ||
            !Move(code[13], Register.RCX, Register.RAX) ||
            !Stack(code[14], Mnemonic.Add, 0x28) || !Jump(code[15]) ||
            !Move(code[16], Register.ECX, Register.EAX) ||
            !Stack(code[17], Mnemonic.Add, 0x28) || !Jump(code[18]) ||
            !SameRegisterTest(code[19], Register.EAX) ||
            !Branch(code[20], Mnemonic.Je, code[26].IP) ||
            !RegisterImmediate(code[21], Mnemonic.Cmp, Register.EAX, 1) ||
            !Branch(code[22], Mnemonic.Jne, code[26].IP) ||
            code[23].Mnemonic != Mnemonic.Lea || code[23].Op0Register != Register.RAX ||
            code[23].MemoryBase != Register.RIP ||
            !Stack(code[24], Mnemonic.Add, 0x28) || !Return(code[25]) ||
            !ZeroRegister(code[26], Register.EAX) ||
            !Stack(code[27], Mnemonic.Add, 0x28) || !Return(code[28]) ||
            !ProveAlternateMethodDefinitionResolver(pe, unwind,
                code[18].NearBranchTarget))
            return false;
        return true;
    }

    private static bool ProveAlternateMethodDefinitionResolver(PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        var first = unwind.ClassifySpan(target, target + 1);
        if (first.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            first.Start != target || first.RootStart != target ||
            Read(pe, unwind, target, 35, 0x8b) is not { } code)
            return false;
        if (!SameRootInstructions(unwind, target, code) ||
            unwind.ClassifySpan(code[^1].IP, code[^1].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree } last ||
            last.End - code[^1].NextIP > 16 ||
            !X64NativePaddingProof.HasInt3Padding(pe, code[^1].NextIP, last.End))
            return false;
        if (!AlternateMethodDefinitionShape(code, out var methodTable,
                out var metadataHeader, out var metadataBlob,
                out var typeInfo, out var setupMethods))
            return false;
        if (methodTable == metadataHeader || methodTable == metadataBlob ||
            metadataHeader == metadataBlob || typeInfo == setupMethods ||
            !WritablePointer(unwind, methodTable) ||
            !WritablePointer(unwind, metadataHeader) ||
            !WritablePointer(unwind, metadataBlob) ||
            !(ProveTypeInfoEntry(pe, unwind, typeInfo) ||
              ProveAlternateTypeInfoEntry(pe, unwind, typeInfo)) ||
            !ProveSetupMethodsEntry(pe, unwind, setupMethods))
            return false;
        return true;
    }

    private static bool ProveAlternateTypeInfoEntry(PE pe,
        X64UnwindProof.Index unwind, ulong target) =>
        Read(pe, unwind, target, 5, 18, allowChainedRegion: true) is { } code &&
        code[0].IP == target &&
        Store(code[0], Register.RSP, 8, Register.RBX) &&
        Store(code[1], Register.RSP, 0x18, Register.RSI) &&
        Push(code[2], Register.RDI) &&
        Stack(code[3], Mnemonic.Sub, 0x20) &&
        SignExtend(code[4], Register.RDI, Register.ECX);

    private static bool AlternateMethodDefinitionShape(IReadOnlyList<Instruction> code,
        out ulong methodTable, out ulong metadataHeader, out ulong metadataBlob,
        out ulong typeInfo, out ulong setupMethods)
    {
        methodTable = metadataHeader = metadataBlob = typeInfo = setupMethods = 0;
        if (!Push(code[0], Register.RDI) || !Stack(code[1], Mnemonic.Sub, 0x20) ||
            !SignExtend(code[2], Register.RDI, Register.ECX) ||
            !RipLoad(code[3], Register.RCX, out methodTable) ||
            code[4].Mnemonic != Mnemonic.Cmp ||
            !Memory(code[4], 0, Register.RCX, Register.RDI, 8, 0, 8) ||
            code[4].GetImmediate(1) != 0 ||
            !Branch(code[5], Mnemonic.Jne, code[31].IP) ||
            !RipLoad(code[6], Register.RAX, out metadataHeader) ||
            !Move(code[7], Register.RCX, Register.RDI) ||
            code[8].Mnemonic != Mnemonic.Shl || code[8].Op0Register != Register.RCX ||
            code[8].GetImmediate(1) != 5 ||
            !Store(code[9], Register.RSP, 0x30, Register.RBX) ||
            !SignExtendMemory(code[10], Register.RDX, Register.RAX, 0x30) ||
            !RipBinary(code[11], Mnemonic.Add, Register.RDX, out metadataBlob) ||
            code[12].Mnemonic != Mnemonic.Mov || code[12].Op0Register != Register.ECX ||
            !Memory(code[12], 1, Register.RCX, Register.RDX, 1, 4, 4) ||
            !DirectCall(code[13]) ||
            !Move(code[14], Register.RCX, Register.RAX) ||
            !Move(code[15], Register.RBX, Register.RAX) ||
            !DirectCall(code[16]) ||
            !Load(code[17], Register.RAX, Register.RBX, 0x68) ||
            !Move(code[18], Register.ECX, Register.EDI) ||
            code[19].Mnemonic != Mnemonic.Sub || code[19].Op0Register != Register.ECX ||
            !Memory(code[19], 1, Register.RAX, Register.None, 1, 0x24, 4) ||
            !Load(code[20], Register.RAX, Register.RBX, 0x98) ||
            !Load(code[21], Register.RBX, Register.RSP, 0x30) ||
            !SignExtend(code[22], Register.RCX, Register.ECX) ||
            code[23].Mnemonic != Mnemonic.Mov || code[23].Op0Register != Register.RCX ||
            !Memory(code[23], 1, Register.RAX, Register.RCX, 8, 0, 8) ||
            !RipLoad(code[24], Register.RAX, out var tableAgain) ||
            code[25].Mnemonic != Mnemonic.Mov ||
            !Memory(code[25], 0, Register.RAX, Register.RDI, 8, 0, 8) ||
            code[25].Op1Register != Register.RCX ||
            !RipLoad(code[26], Register.RAX, out var tableLast) ||
            code[27].Mnemonic != Mnemonic.Mov || code[27].Op0Register != Register.RAX ||
            !Memory(code[27], 1, Register.RAX, Register.RDI, 8, 0, 8) ||
            !Stack(code[28], Mnemonic.Add, 0x20) ||
            !Pop(code[29], Register.RDI) || !Return(code[30]) ||
            code[31].Mnemonic != Mnemonic.Mov || code[31].Op0Register != Register.RAX ||
            !Memory(code[31], 1, Register.RCX, Register.RDI, 8, 0, 8) ||
            !Stack(code[32], Mnemonic.Add, 0x20) ||
            !Pop(code[33], Register.RDI) || !Return(code[34]) ||
            tableAgain != methodTable || tableLast != methodTable)
            return false;
        typeInfo = code[13].NearBranchTarget;
        setupMethods = code[16].NearBranchTarget;
        return true;
    }
}
