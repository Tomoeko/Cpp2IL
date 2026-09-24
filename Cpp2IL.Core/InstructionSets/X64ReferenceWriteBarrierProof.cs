using System;
using System.Collections.Generic;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds an internal tail target to the installed il2cpp_gc_wbarrier_set_field
/// export and checks that the target only marks the GC card table. Neither a
/// symbol name alone nor an arbitrary thunk is enough to discard a native call.
/// </summary>
internal static class X64ReferenceWriteBarrierProof
{
    internal static bool TryIdentify(PE pe, X64UnwindProof.Index unwind, ulong target)
    {
        try
        {
            var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_gc_wbarrier_set_field");
            if (exported == 0 ||
                X64NativeInstructionReader.Read(pe, unwind, exported, 3, 16) is not { } wrapper ||
                !RegisterMove(wrapper[0], Register.RCX, Register.RDX) ||
                !RegisterMove(wrapper[1], Register.RDX, Register.R8) ||
                !Jump(wrapper[2]))
                return false;

            var setField = wrapper[2].NearBranchTarget;
            if (X64NativeInstructionReader.Read(pe, unwind, setField, 2, 16) is not { } writer ||
                writer[0].Code != Code.Mov_rm64_r64 ||
                !Memory(writer[0], 0, Register.RCX, Register.None, 1, 0, 8) ||
                writer[0].Op1Kind != OpKind.Register || writer[0].Op1Register != Register.RDX ||
                !Jump(writer[1]))
                return false;

            // The caller's tail transfer may pass through short linker thunks.
            // Each accepted thunk has exactly one direct JMP and no other effect.
            var cardMarker = writer[1].NearBranchTarget;
            var seen = new HashSet<ulong> { exported, setField, cardMarker };
            for (var depth = 0; depth < 3 && target != cardMarker; depth++)
            {
                if (!seen.Add(target) ||
                    X64NativeInstructionReader.Read(pe, unwind, target, 1, 5) is not { } thunk ||
                    !Jump(thunk[0]))
                    return false;
                target = thunk[0].NearBranchTarget;
            }
            return target == cardMarker && ProveCardMarker(pe, unwind, cardMarker);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool ProveCardMarker(PE pe, X64UnwindProof.Index unwind, ulong address)
    {
        const int maxBytes = 80;
        if (address < unwind.ImageBase || address - unwind.ImageBase > uint.MaxValue ||
            !unwind.IsExecutableRva((uint)(address - unwind.ImageBase)))
            return false;
        var first = pe.MapVirtualAddressToRaw(address, false);
        var last = pe.MapVirtualAddressToRaw(address + maxBytes - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (first < 0 || last - first != maxBytes - 1 || last >= raw.Length)
            return false;
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(raw.Slice((int)first, maxBytes).ToArray()), address);
        var body = new Instruction[17];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = decoder.Decode();
            if (body[index].IsInvalid || body[index].CodeSize != CodeSize.Code64 ||
                body[index].NextIP > address + maxBytes ||
                body[index].HasRepPrefix || body[index].HasRepnePrefix ||
                body[index].SegmentPrefix != Register.None ||
                body[index].HasLockPrefix != (index == 14))
                return false;
        }
        if (unwind.ClassifySpan(address, body[^1].NextIP).Kind != X64UnwindProof.SpanKind.NoEntry ||
            !CardMarkerShape(body, out var enabledFlag, out var cardTable) ||
            enabledFlag < unwind.ImageBase || cardTable < unwind.ImageBase ||
            enabledFlag - unwind.ImageBase > uint.MaxValue ||
            cardTable - unwind.ImageBase > uint.MaxValue)
            return false;
        var flagRva = (uint)(enabledFlag - unwind.ImageBase);
        var tableRva = (uint)(cardTable - unwind.ImageBase);
        return HasWritableCardMarkerData(unwind, flagRva, tableRva);
    }

    internal static bool HasWritableCardMarkerData(X64UnwindProof.Index unwind,
        uint flagRva, uint tableRva)
    {
        // The mask admits 2^21 card bits, stored in 2^15 64-bit words.
        const uint cardTableBytes = (1U << 21) / 8;
        return unwind.IsWritableVirtualRangeInOneSection(flagRva, sizeof(int)) &&
               unwind.IsWritableVirtualRangeInOneSection(tableRva, cardTableBytes);
    }

    internal static bool CardMarkerShape(IReadOnlyList<Instruction> body,
        out ulong enabledFlag, out ulong cardTable)
    {
        enabledFlag = 0;
        cardTable = 0;
        if (body.Count != 17 || body[0].Code != Code.Cmp_rm32_imm8 ||
            !Memory(body[0], 0, Register.RIP, Register.None, 1,
                body[0].MemoryDisplacement64, 4) ||
            body[0].Op1Kind != OpKind.Immediate8to32 || body[0].GetImmediate(1) != 0 ||
            !RegisterMove(body[1], Register.R8, Register.RCX) ||
            body[2].Mnemonic != Mnemonic.Je || body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[16].IP ||
            body[3].Code != Code.Shr_rm64_imm8 ||
            !RegisterImmediate(body[3], Register.R8, 12) ||
            body[4].Code != Code.Lea_r64_m || body[4].Op0Kind != OpKind.Register ||
            body[4].Op0Register != Register.RCX ||
            !Memory(body[4], 1, Register.RIP, Register.None, 1,
                body[4].MemoryDisplacement64, 0) ||
            body[5].Code != Code.And_rm32_imm32 ||
            !RegisterImmediate(body[5], Register.R8D, 0x1fffff) ||
            body[6].Code != Code.Mov_r32_rm32 ||
            !Registers(body[6], Register.EAX, Register.R8D) ||
            body[7].Code != Code.Shr_rm64_imm8 ||
            !RegisterImmediate(body[7], Register.RAX, 6) ||
            body[8].Code != Code.And_rm32_imm8 ||
            !RegisterImmediate(body[8], Register.R8D, 0x3f) ||
            body[9].Code != Code.Lea_r64_m || body[9].Op0Kind != OpKind.Register ||
            body[9].Op0Register != Register.RDX ||
            !Memory(body[9], 1, Register.RCX, Register.RAX, 8, 0, 0) ||
            body[10].Code != Code.Prefetchw_m8 ||
            !Memory(body[10], 0, Register.RDX, Register.None, 1, 0, 1) ||
            body[11].Code != Code.Mov_r64_rm64 ||
            body[11].Op0Kind != OpKind.Register || body[11].Op0Register != Register.RAX ||
            !Memory(body[11], 1, Register.RDX, Register.None, 1, 0, 8) ||
            !RegisterMove(body[12], Register.RCX, Register.RAX) ||
            body[13].Code != Code.Bts_rm64_r64 ||
            !Registers(body[13], Register.RCX, Register.R8) ||
            body[14].Code != Code.Cmpxchg_rm64_r64 || !body[14].HasLockPrefix ||
            !Memory(body[14], 0, Register.RDX, Register.None, 1, 0, 8) ||
            body[14].Op1Kind != OpKind.Register || body[14].Op1Register != Register.RCX ||
            body[15].Mnemonic != Mnemonic.Jne || body[15].Op0Kind != OpKind.NearBranch64 ||
            body[15].NearBranchTarget != body[11].IP ||
            body[16].Code != Code.Retnq || body[16].OpCount != 0)
            return false;
        enabledFlag = body[0].MemoryDisplacement64;
        cardTable = body[4].MemoryDisplacement64;
        return true;
    }

    private static bool Jump(Instruction instruction) =>
        instruction.Code == Code.Jmp_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool RegisterMove(Instruction instruction, Register destination, Register source) =>
        instruction.Code == Code.Mov_r64_rm64 && Registers(instruction, destination, source);

    private static bool Registers(Instruction instruction, Register destination, Register source) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool RegisterImmediate(Instruction instruction, Register destination, ulong value) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind is OpKind.Immediate8 or OpKind.Immediate8to32 or
            OpKind.Immediate8to64 or OpKind.Immediate32 && instruction.GetImmediate(1) == value;

    private static bool Memory(Instruction instruction, int operand, Register @base,
        Register index, int scale, ulong offset, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory && instruction.MemoryBase == @base &&
        instruction.MemoryIndex == index && instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset &&
        (width == 0 || instruction.MemorySize.GetSize() == width);
}
