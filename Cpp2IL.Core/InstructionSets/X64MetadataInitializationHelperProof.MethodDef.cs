using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    /// <summary>
    /// Establishes the MethodDef route in addition to the TypeInfo route checked by
    /// TryIdentify. The installed runtime maps this route to
    /// GlobalMetadata::GetMethodInfoFromMethodDefinitionIndex. A caller must still
    /// bind its own encoded MethodDef slot to the exact managed method.
    /// </summary>
    internal static bool TryIdentifyMethodDefArm(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        if (!TryIdentify(app, pe, unwind, target))
            return false;

        try
        {
            var thunk = Read(pe, unwind, target, 1, 5);
            if (thunk == null || !Jump(thunk[0]))
                return false;
            var wrapper = Read(pe, unwind, thunk[0].NearBranchTarget, 2, 7);
            if (wrapper == null || !MoveOneToDl(wrapper[0]) || !Jump(wrapper[1]))
                return false;

            var core = wrapper[1].NearBranchTarget;
            var first = unwind.ClassifySpan(core, core + 1);
            if (first.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                first.Start != core || first.RootStart != core ||
                Read(pe, unwind, first.End, 8, 0x23) is not { } dispatch ||
                !TableLoad(dispatch[1], out var tableRva) ||
                !TryReadMethodDefSwitchArm(pe, unwind, tableRva, out var armAddress))
                return false;
            if (Read(pe, unwind, armAddress, 4, 16, allowChainedRegion: true) is not { } arm ||
                !SameRoot(unwind, core, armAddress, arm[^1].NextIP) ||
                !ProveMethodDefArmShape(arm, dispatch[7].NearBranchTarget,
                    out var encodedResolver))
                return false;
            if (!ProveEncodedMethodResolver(pe, unwind, encodedResolver,
                    out var definitionResolver))
                return false;
            if (!ProveMethodDefinitionResolver(pe, unwind, definitionResolver))
                return false;

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadMethodDefSwitchArm(PE pe, X64UnwindProof.Index unwind,
        uint tableRva, out ulong arm)
    {
        arm = 0;
        if (tableRva > uint.MaxValue - 28 || !unwind.IsExecutableRva(tableRva) ||
            unwind.IsWritableFileBackedRva(tableRva) ||
            unwind.ImageBase > ulong.MaxValue - tableRva - 27)
            return false;
        var tableAddress = unwind.ImageBase + tableRva;
        var first = pe.MapVirtualAddressToRaw(tableAddress, false);
        var last = pe.MapVirtualAddressToRaw(tableAddress + 27, false);
        var raw = pe.GetRawBinaryContent();
        if (first < 0 || last - first != 27 || last >= raw.Length)
            return false;

        // The native decoder subtracts one from the usage tag: TypeInfo is
        // table[0], MethodDef is table[2], and MethodRef shares table[5].
        var table = raw.Slice((int)first, 28);
        var methodDefRva = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(8, 4));
        var methodRefRva = BinaryPrimitives.ReadInt32LittleEndian(table.Slice(20, 4));
        if (methodDefRva < 0 || methodDefRva != methodRefRva ||
            unwind.ImageBase > ulong.MaxValue - (uint)methodDefRva ||
            !unwind.IsExecutableRva((uint)methodDefRva))
            return false;
        arm = unwind.ImageBase + (uint)methodDefRva;
        return true;
    }

    internal static bool ProveMethodDefArmShape(IReadOnlyList<Instruction> arm,
        ulong join, out ulong encodedResolver)
    {
        encodedResolver = 0;
        if (arm.Count != 4 || !Move(arm[0], Register.ECX, Register.R9D) ||
            !DirectCall(arm[1]) || !Move(arm[2], Register.RBX, Register.RAX) ||
            !Jump(arm[3]) || arm[3].NearBranchTarget != join)
            return false;
        encodedResolver = arm[1].NearBranchTarget;
        return true;
    }

    private static bool ProveEncodedMethodResolver(PE pe, X64UnwindProof.Index unwind,
        ulong address, out ulong definitionResolver)
    {
        definitionResolver = 0;
        var prefix = Read(pe, unwind, address, 9, 0x19);
        if (prefix == null || !Stack(prefix[0], Mnemonic.Sub, 0x28) ||
            !Move(prefix[1], Register.EAX, Register.ECX) ||
            !Shift(prefix[2], Register.ECX, 29) ||
            !Shift(prefix[3], Register.EAX, 1) ||
            !RegisterImmediate(prefix[4], Mnemonic.And, Register.EAX, 0x0fff_ffff) ||
            !SameRegisterTest(prefix[5], Register.ECX) ||
            prefix[6].Mnemonic != Mnemonic.Je || prefix[6].Op0Kind != OpKind.NearBranch64 ||
            !RegisterImmediate(prefix[7], Mnemonic.Cmp, Register.ECX, 3) ||
            prefix[8].Mnemonic != Mnemonic.Je || prefix[8].Op0Kind != OpKind.NearBranch64)
            return false;

        var methodDef = Read(pe, unwind, prefix[8].NearBranchTarget, 3, 11);
        if (methodDef == null || !Move(methodDef[0], Register.ECX, Register.EAX) ||
            !Stack(methodDef[1], Mnemonic.Add, 0x28) || !Jump(methodDef[2]))
            return false;

        var region = unwind.ClassifySpan(address, prefix[^1].NextIP);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != address || region.RootStart != address ||
            methodDef[0].IP <= prefix[^1].IP ||
            !SameRoot(unwind, address, methodDef[0].IP, methodDef[^1].NextIP))
            return false;

        definitionResolver = methodDef[2].NearBranchTarget;
        return true;
    }

    private static bool ProveMethodDefinitionResolver(PE pe, X64UnwindProof.Index unwind,
        ulong address)
    {
        var code = Read(pe, unwind, address, 33, 0x90);
        if (code == null || code[^1].NextIP != address + 0x90 ||
            unwind.ClassifySpan(address, code[0].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree, Start: var start,
                    RootStart: var root } || start != address || root != address ||
            !code.All(instruction => SameRoot(unwind, address,
                instruction.IP, instruction.NextIP)))
            return false;

        var lastRegion = unwind.ClassifySpan(code[^1].IP, code[^1].NextIP);
        if (lastRegion.End < code[^1].NextIP ||
            lastRegion.End - code[^1].NextIP > 16 ||
            !X64NativePaddingProof.HasInt3Padding(pe, code[^1].NextIP, lastRegion.End) ||
            !ProveMethodDefinitionShape(code, out var methodTable, out var metadataHeader,
                out var metadataBlob, out var typeInfo, out var setupMethods) ||
            !WritablePointer(unwind, methodTable) ||
            !WritablePointer(unwind, metadataHeader) ||
            !WritablePointer(unwind, metadataBlob) ||
            methodTable == metadataHeader || methodTable == metadataBlob ||
            metadataHeader == metadataBlob || typeInfo == setupMethods ||
            !ProveTypeInfoEntry(pe, unwind, typeInfo) ||
            !ProveSetupMethodsEntry(pe, unwind, setupMethods))
            return false;
        return true;
    }

    internal static bool ProveMethodDefinitionShape(IReadOnlyList<Instruction> code,
        out ulong methodTable, out ulong metadataHeader, out ulong metadataBlob,
        out ulong typeInfo, out ulong setupMethods)
    {
        methodTable = metadataHeader = metadataBlob = typeInfo = setupMethods = 0;
        if (code.Count != 33 ||
            !MemoryRegister(code[0], Mnemonic.Mov, 0, Register.RSP, Register.None, 1,
                0x10, 8, Register.RSI) ||
            !Push(code[1], Register.RDI) || !Stack(code[2], Mnemonic.Sub, 0x20) ||
            !RipLoad(code[3], Register.RAX, out methodTable) ||
            !SignExtend(code[4], Register.RSI, Register.ECX) ||
            !Move(code[5], Register.RCX, Register.RSI) ||
            !ScaledLea(code[6], Register.RDI, Register.RSI, 8) ||
            !MemoryImmediateZero(code[7], Mnemonic.Cmp, Register.RDI, Register.RAX,
                1, 0, 8) ||
            !Branch(code[8], Mnemonic.Jne, code[28].IP) ||
            !RipLoad(code[9], Register.RAX, out metadataHeader) ||
            !RegisterImmediate(code[10], Mnemonic.Shl, Register.RCX, 5) ||
            !Store(code[11], Register.RSP, 0x30, Register.RBX) ||
            !SignExtendMemory(code[12], Register.RDX, Register.RAX, 0x30) ||
            !RipBinary(code[13], Mnemonic.Add, Register.RDX, out metadataBlob) ||
            !MemoryRegister(code[14], Mnemonic.Mov, 1, Register.RCX, Register.RDX,
                1, 4, 4, Register.ECX) ||
            !DirectCall(code[15]) || !Move(code[16], Register.RCX, Register.RAX) ||
            !Move(code[17], Register.RBX, Register.RAX) || !DirectCall(code[18]) ||
            !Load(code[19], Register.RCX, Register.RBX, 0x68) ||
            !MemoryRegister(code[20], Mnemonic.Sub, 1, Register.RCX, Register.None,
                1, 0x24, 4, Register.ESI) ||
            !Load(code[21], Register.RCX, Register.RBX, 0x98) ||
            !Load(code[22], Register.RBX, Register.RSP, 0x30) ||
            !SignExtend(code[23], Register.RDX, Register.ESI) ||
            !MemoryRegister(code[24], Mnemonic.Mov, 1, Register.RCX, Register.RDX,
                8, 0, 8, Register.R8) ||
            !RipLoad(code[25], Register.RCX, out var tableAgain) ||
            !MemoryRegister(code[26], Mnemonic.Mov, 0, Register.RDI, Register.RCX,
                1, 0, 8, Register.R8) ||
            !RipLoad(code[27], Register.RAX, out var tableLast) ||
            !MemoryRegister(code[28], Mnemonic.Mov, 1, Register.RDI, Register.RAX,
                1, 0, 8, Register.RAX) ||
            !MemoryRegister(code[29], Mnemonic.Mov, 1, Register.RSP, Register.None,
                1, 0x38, 8, Register.RSI) ||
            !Stack(code[30], Mnemonic.Add, 0x20) || !Pop(code[31], Register.RDI) ||
            !Return(code[32]) ||
            tableAgain != methodTable || tableLast != methodTable)
            return false;
        typeInfo = code[15].NearBranchTarget;
        setupMethods = code[18].NearBranchTarget;
        return true;
    }

    private static bool ProveTypeInfoEntry(PE pe, X64UnwindProof.Index unwind,
        ulong address) =>
        Read(pe, unwind, address, 5, 20, allowChainedRegion: true) is { } code &&
        MemoryRegister(code[0], Mnemonic.Mov, 0, Register.RSP, Register.None,
            1, 8, 8, Register.RBX) &&
        MemoryRegister(code[1], Mnemonic.Mov, 0, Register.RSP, Register.None,
            1, 0x18, 8, Register.RBP) &&
        MemoryRegister(code[2], Mnemonic.Mov, 0, Register.RSP, Register.None,
            1, 0x20, 8, Register.RSI) &&
        Push(code[3], Register.RDI) && Stack(code[4], Mnemonic.Sub, 0x20);

    private static bool ProveSetupMethodsEntry(PE pe, X64UnwindProof.Index unwind,
        ulong address) =>
        Read(pe, unwind, address, 5, 18, allowChainedRegion: true) is { } code &&
        MemoryRegister(code[0], Mnemonic.Mov, 0, Register.RSP, Register.None,
            1, 0x10, 8, Register.RBX) &&
        MemoryRegister(code[1], Mnemonic.Mov, 0, Register.RSP, Register.None,
            1, 0x18, 8, Register.RSI) &&
        Push(code[2], Register.RDI) && Stack(code[3], Mnemonic.Sub, 0x20) &&
        Move(code[4], Register.RDI, Register.RCX);

    private static bool MemoryRegister(Instruction code, Mnemonic mnemonic,
        int memoryOperand, Register basis, Register index, int scale, ulong offset,
        int width, Register register) =>
        code.Mnemonic == mnemonic &&
        Memory(code, memoryOperand, basis, index, scale, offset, width) &&
        code.GetOpKind(1 - memoryOperand) == OpKind.Register &&
        code.GetOpRegister(1 - memoryOperand) == register;

    private static bool MemoryImmediateZero(Instruction code, Mnemonic mnemonic,
        Register basis, Register index, int scale, ulong offset, int width) =>
        code.Mnemonic == mnemonic &&
        Memory(code, 0, basis, index, scale, offset, width) &&
        code.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        code.GetImmediate(1) == 0;

    private static bool SignExtendMemory(Instruction code, Register destination,
        Register basis, ulong offset) =>
        code.Mnemonic == Mnemonic.Movsxd && code.Op0Kind == OpKind.Register &&
        code.Op0Register == destination &&
        Memory(code, 1, basis, Register.None, 1, offset, 4);

    private static bool RipBinary(Instruction code, Mnemonic mnemonic,
        Register destination, out ulong address)
    {
        address = code.IPRelativeMemoryAddress;
        return code.Mnemonic == mnemonic && code.Op0Kind == OpKind.Register &&
            code.Op0Register == destination && code.Op1Kind == OpKind.Memory &&
            code.MemoryBase == Register.RIP && code.MemoryIndex == Register.None &&
            code.MemorySize.GetSize() == 8;
    }
}
