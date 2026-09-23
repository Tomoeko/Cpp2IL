using System;
using System.Collections.Generic;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identifies the bounded MSVC C++ throw routine used by a catch funclet. Every
/// reachable native path must reach a noncontinuable RaiseException import call.
/// This proves no normal return from the helper, not managed catch identity.
/// </summary>
internal static class X64NativeCxxThrowProof
{
    internal static bool Check(PE pe, X64UnwindProof.Index index, ulong target)
    {
        var import = pe.GetVirtualAddressOfImportedFunctionByName("KERNEL32.dll", "RaiseException");
        if (import == 0 || target == ulong.MaxValue)
            return false;
        var span = index.ClassifySpan(target, target + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != target ||
            span.End <= target || span.End - target is < 100 or > 256 ||
            index.ClassifySpan(target, span.End) != span)
            return false;
        var rawStart = pe.MapVirtualAddressToRaw(target, false);
        var rawEnd = pe.MapVirtualAddressToRaw(span.End - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != span.End - target - 1 || rawEnd >= raw.Length)
            return false;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(
            raw.Slice((int)rawStart, (int)(span.End - target)).ToArray()), target);
        var code = new List<Instruction>();
        while (decoder.IP < span.End && code.Count < 64)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.NextIP > span.End ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None)
                return false;
            code.Add(instruction);
        }
        if (decoder.IP != span.End || code.Count < 41 ||
            !Store(code[0], Register.RSP, 0x18, Register.RBX) ||
            !Store(code[1], Register.RSP, 0x20, Register.RSI) ||
            !Push(code[2], Register.RDI) || !Stack(code[3], 0x50) ||
            !Move(code[4], Register.RBX, Register.RDX) ||
            !Move(code[5], Register.RSI, Register.RCX) ||
            !Immediate(code[6], Register.EDI, 0x19930520) ||
            !Immediate(code[27], Register.EDI, 0x1994000) ||
            !Immediate(code[28], Register.EDX, 1) ||
            !Store(code[29], Register.RSP, 0x28, Register.RDI) ||
            !Address(code[30], Register.R9, Register.RSP, 0x28) ||
            !Store(code[31], Register.RSP, 0x30, Register.RSI) ||
            !Immediate(code[32], Register.ECX, 0xe06d7363) ||
            !Store(code[33], Register.RSP, 0x38, Register.RBX) ||
            !Store(code[34], Register.RSP, 0x40, Register.RAX) ||
            !Address(code[35], Register.R8D, Register.RDX, 3) ||
            code[36].Mnemonic != Mnemonic.Call || code[36].Op0Kind != OpKind.Memory ||
            code[36].MemoryBase != Register.RIP ||
            code[36].IPRelativeMemoryAddress != import)
            return false;

        var byAddress = new Dictionary<ulong, int>();
        for (var i = 0; i <= 36; i++)
            byAddress.Add(code[i].IP, i);
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(0);
        var reachedRaise = false;
        while (pending.Count != 0)
        {
            var i = pending.Pop();
            if (!seen.Add(i))
                continue;
            if (i == 36)
            {
                reachedRaise = true;
                continue;
            }
            var instruction = code[i];
            if (i + 1 > 36)
                return false;
            switch (instruction.FlowControl)
            {
                case FlowControl.Next:
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                    pending.Push(i + 1);
                    break;
                case FlowControl.ConditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        instruction.NearBranchTarget <= instruction.IP ||
                        !byAddress.TryGetValue(instruction.NearBranchTarget, out var branch))
                        return false;
                    pending.Push(branch);
                    pending.Push(i + 1);
                    break;
                case FlowControl.UnconditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        instruction.NearBranchTarget <= instruction.IP ||
                        !byAddress.TryGetValue(instruction.NearBranchTarget, out var jump))
                        return false;
                    pending.Push(jump);
                    break;
                default:
                    return false;
            }
        }
        return reachedRaise;
    }

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Push(Instruction i, Register register) => i.Mnemonic == Mnemonic.Push &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Stack(Instruction i, ulong size) => i.Mnemonic == Mnemonic.Sub &&
        i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Immediate(Instruction i, Register destination, uint value) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register &&
        i.Op0Register == destination && i.Op1Kind == OpKind.Immediate32 && i.Immediate32 == value;

    private static bool Address(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;
}
