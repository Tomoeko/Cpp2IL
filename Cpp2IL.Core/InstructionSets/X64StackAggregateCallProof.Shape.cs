using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64StackAggregateCallProof
{
    // This is a byte-copy recipe, not a floating-point operation. MOVSD carries
    // two Single fields without interpreting their combined bits as a Double.
    internal sealed record ArgumentCopy(ulong Call, ulong Address, ulong FirstRead,
        ulong LowRead, ulong HighRead, ulong LowWrite, ulong HighWrite,
        Register Source, Register Argument, uint StackOffset, uint FrameSize)
    {
        internal ulong[] ReplacedAddresses => [LowRead, HighRead, LowWrite, HighWrite];
    }

    internal static ArgumentCopy? TryFindArgumentCopy(IReadOnlyList<Instruction> body,
        ulong callAddress, Register argument, Register callResult = Register.None, Register callerResult = Register.None,
        int callResultBytes = 0, int callerResultBytes = 0)
    {
        if (body.Count is < 7 or > 1024 || body.Any(instruction => !Clean(instruction)) ||
            body.Any(instruction => instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 &&
                instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op1Register == Register.RSP) ||
            body.Select((instruction, index) => index == 0 || body[index - 1].NextIP == instruction.IP).Any(valid => !valid))
            return null;
        var call = Index(body, callAddress);
        if (call < 0 || body[call].Code != Code.Call_rel32_64 || argument is not
            (Register.RCX or Register.RDX or Register.R8 or Register.R9)) return null;
        var allocation = body.Take(call).Where(instruction => instruction.Code is
            Code.Sub_rm64_imm8 or Code.Sub_rm64_imm32 && instruction.Op0Register == Register.RSP).ToArray();
        if (allocation is not [var allocated] || allocated.GetImmediate(1) is < 0x30 or > 4096)
            return null;
        var frameSize = checked((uint)allocated.GetImmediate(1));
        var addressIndex = LastWrite(body, call, argument);
        if (addressIndex < 0 || body[addressIndex] is not { Code: Code.Lea_r64_m } address ||
            !FrameMemory(address, frameSize, 12) || address.MemoryDisplacement64 % 4 != 0)
            return null;
        var slot = checked((uint)address.MemoryDisplacement64);
        var start = call - 1;
        while (start >= 0 && body[start].FlowControl == FlowControl.Next &&
               body[start].IP > allocated.IP) start--;
        start++;
        var lowWrites = Enumerable.Range(start, call - start).Where(index =>
            IsCopyStore(body[index], slot, 8, frameSize)).ToArray();
        var highWrites = Enumerable.Range(start, call - start).Where(index =>
            IsCopyStore(body[index], slot + 8, 4, frameSize)).ToArray();
        if (lowWrites is not [var lowWrite] || highWrites is not [var highWrite]) return null;
        var lowRead = LastWrite(body, lowWrite, body[lowWrite].Op1Register);
        var highRead = LastWrite(body, highWrite, body[highWrite].Op1Register);
        if (lowRead < start || highRead < start ||
            !IsCopyLoad(body[lowRead], 8) || !IsCopyLoad(body[highRead], 4) ||
            body[lowRead].MemoryBase != body[highRead].MemoryBase ||
            body[lowRead].MemoryDisplacement64 != 0 || body[highRead].MemoryDisplacement64 != 8 ||
            body[lowRead].Op0Register.GetFullRegister() != body[lowWrite].Op1Register.GetFullRegister() ||
            body[highRead].Op0Register.GetFullRegister() != body[highWrite].Op1Register.GetFullRegister())
            return null;
        var first = Math.Min(lowRead, highRead);
        var replaced = new HashSet<int> { lowRead, highRead, lowWrite, highWrite };
        if (replaced.Count != 4 || body.Skip(first).Take(call - first).Any(instruction => instruction.FlowControl != FlowControl.Next) ||
            body.Skip(first).Take(Math.Max(lowRead, highRead) - first).Any(instruction => Writes(instruction, body[lowRead].MemoryBase)) ||
            body.Any(instruction => instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget > body[first].IP &&
                instruction.NearBranchTarget <= body[call].IP)) return null;
        for (var index = first; index < call; index++)
        {
            var instruction = body[index];
            if (instruction.MemoryBase != Register.RSP || !Overlaps(instruction, slot, 12)) continue;
            if (!replaced.Contains(index) && index != addressIndex) return null;
        }
        // Native scratch registers cannot retain a second observable use of a
        // removed packed load. Calls overwrite the volatile copies; otherwise
        // require an independent overwrite before another read.
        if (body.Skip(addressIndex + 1).Take(call - addressIndex - 1).Any(instruction => new InstructionInfoFactory().GetInfo(instruction)
                .GetUsedRegisters().Any(used => used.Register.GetFullRegister() == argument)) ||
            !OnlyUsedByStore(body, lowRead, lowWrite, call, callResult, callerResult, callResultBytes, callerResultBytes) ||
            !OnlyUsedByStore(body, highRead, highWrite, call, callResult, callerResult, callResultBytes, callerResultBytes) ||
            !PrivateLifetime(body, first, call, addressIndex, slot, frameSize)) return null;
        return new(body[call].IP, address.IP, body[first].IP, body[lowRead].IP, body[highRead].IP,
            body[lowWrite].IP, body[highWrite].IP, body[lowRead].MemoryBase, argument, slot, frameSize);
    }

    // Track the original parameter's native address through the complete prefix
    // CFG. A may-alias is insufficient for a load: every incoming path must name
    // the same original storage, and no earlier call or store may expose it.
    internal static bool HasUnescapedParameterOrigin(IReadOnlyList<Instruction> body,
        ArgumentCopy copy, Register incoming, ISet<ulong>? provedNoReturnCalls = null)
    {
        var stop = Index(body, copy.Call);
        var read = Index(body, copy.FirstRead);
        if (stop < 0 || incoming is not (Register.RCX or Register.RDX or Register.R8 or Register.R9)) return false;
        var addresses = body.Select((instruction, index) => (instruction.IP, index)).ToDictionary(pair => pair.IP, pair => pair.index);
        var states = new Dictionary<Register, byte>?[body.Count];
        states[0] = new() { [incoming] = 1 };
        var pending = new Queue<int>(); pending.Enqueue(0);
        var visits = 0;
        var info = new InstructionInfoFactory();
        while (pending.Count != 0)
        {
            if (++visits > body.Count * 64) return false;
            var index = pending.Dequeue();
            if (index == stop) continue;
            var instruction = body[index];
            var before = states[index]!;
            if (provedNoReturnCalls?.Contains(instruction.IP) == true) continue;
            byte State(Register register) => ValueOr(before, register.GetFullRegister(), (byte)2);
            var used = info.GetInfo(instruction);
            if (used.GetUsedMemory().Any(memory => memory.Access is not (OpAccess.Read or OpAccess.CondRead) &&
                    (State(memory.Base) & 1) != 0) ||
                instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register &&
                (State(instruction.Op1Register) & 1) != 0) return false;
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                new[] { Register.RCX, Register.RDX, Register.R8, Register.R9 }.Any(register => (State(register) & 1) != 0))
                return false;
            var after = new Dictionary<Register, byte>(before);
            var moved = instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && instruction.Op0Kind == OpKind.Register &&
                instruction.Op1Kind == OpKind.Register ? State(instruction.Op1Register) : (byte)2;
            var sourceMayAlias = used.GetUsedRegisters().Any(register =>
                register.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                (State(register.Register) & 1) != 0 && (instruction.Code == Code.Lea_r64_m ||
                    Enumerable.Range(0, instruction.OpCount).Any(operand => instruction.GetOpKind(operand) == OpKind.Register &&
                        instruction.GetOpRegister(operand).GetFullRegister() == register.Register.GetFullRegister())));
            foreach (var register in used.GetUsedRegisters())
                if (IsWrite(register.Access)) after[register.Register.GetFullRegister()] =
                    sourceMayAlias || register.Access is OpAccess.CondWrite or OpAccess.ReadCondWrite ||
                    (State(register.Register) & 1) != 0 && register.Register.GetSize() < 8 ? (byte)3 : (byte)2;
            if (ZeroRegister(instruction) is { } cleared) after[cleared] = 2;
            if (instruction.Op0Kind == OpKind.Register && instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && instruction.Op1Kind == OpKind.Register)
                after[instruction.Op0Register] = moved;
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                foreach (var register in new[] { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11 })
                    after[register] = (State(register) & 1) != 0 ? (byte)3 : (byte)2;
            foreach (var next in Successors(index, instruction))
            {
                if (next < 0 || next >= body.Count) return false;
                if (states[next] == null) { states[next] = new(after); pending.Enqueue(next); continue; }
                var changed = false;
                foreach (var register in states[next]!.Keys.Concat(after.Keys).Distinct().ToArray())
                {
                    var merged = (byte)(ValueOr(states[next]!, register, (byte)2) | ValueOr(after, register, (byte)2));
                    if (ValueOr(states[next]!, register, (byte)2) == merged) continue;
                    states[next]![register] = merged; changed = true;
                }
                if (changed) pending.Enqueue(next);
            }
        }
        return states[read] is { } atRead && ValueOr(atRead, copy.Source, (byte)2) == 1 &&
            states[stop] is { } atCall && new[] { Register.RCX, Register.RDX, Register.R8, Register.R9 }
                .All(register => (ValueOr(atCall, register, (byte)2) & 1) == 0);

        IEnumerable<int> Successors(int index, Instruction instruction)
        {
            if (instruction.FlowControl is FlowControl.Return or FlowControl.Exception) yield break;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {
                if (instruction.Op0Kind != OpKind.NearBranch64 || !addresses.TryGetValue(instruction.NearBranchTarget, out var target))
                    yield return -1;
                else yield return target;
                if (instruction.FlowControl == FlowControl.UnconditionalBranch) yield break;
            }
            if (instruction.FlowControl is FlowControl.IndirectBranch) { yield return -1; yield break; }
            yield return index + 1;
        }
    }

    private static bool PrivateLifetime(IReadOnlyList<Instruction> body, int first, int call, int address,
        uint slot, uint frame)
    {
        // A prior escaped address can keep the native buffer alive. Do not
        // infer its lifetime from a new SSA version or a later complete write.
        if (body.Take(first).Any(instruction => instruction.Code == Code.Lea_r64_m &&
            instruction.MemoryBase == Register.RSP)) return false;
        for (var index = first; index <= call; index++)
            if (index != address && body[index].Code == Code.Lea_r64_m && body[index].MemoryBase == Register.RSP) return false;
        var addresses = body.Select((instruction, index) => (instruction.IP, index)).ToDictionary(pair => pair.IP, pair => pair.index);
        var pending = new Queue<(int Index, int Written, Dictionary<Register, long> Aliases)>();
        // ABI volatility permits a callee to overwrite this pointer; it does not
        // establish that every callee actually did. Keep the may-alias until an
        // independently observed caller write replaces it.
        // MinValue is a may-alias: the callee may preserve this argument pointer
        // or change it. Writes through it cannot count as definite replacement.
        pending.Enqueue((call + 1, 0, new() { [Register.RSP] = 0, [body[address].Op0Register] = long.MinValue }));
        var visited = new HashSet<(int, int, string)>();
        var info = new InstructionInfoFactory();
        while (pending.Count != 0)
        {
            var (index, written, aliases) = pending.Dequeue();
            if (index < 0 || index >= body.Count || visited.Count > body.Count * 128) return false;
            var key = (index, written, string.Join(";", aliases.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value)));
            if (!visited.Add(key)) continue;
            var instruction = body[index];
            if (instruction.FlowControl == FlowControl.Return || instruction.Code == Code.Int3) continue;
            var nativeInfo = info.GetInfo(instruction);
            if (instruction.Code != Code.Lea_r64_m && aliases.TryGetValue(instruction.MemoryBase, out var baseOffset))
            {
                if (baseOffset == long.MinValue) return false;
                if (instruction.MemoryIndex != Register.None) return false;
                var offset = checked(baseOffset + unchecked((long)instruction.MemoryDisplacement64));
                var width = instruction.MemorySize.GetSize();
                if (offset < slot + 12 && offset + width > slot)
                {
                    if (offset < slot || offset + width > slot + 12 || instruction.Op0Kind != OpKind.Memory ||
                        instruction.Code is not (Code.Mov_rm32_r32 or Code.Mov_rm64_r64 or Code.Movsd_xmmm64_xmm or
                            Code.Movss_xmmm32_xmm or Code.Mov_rm32_imm32 or Code.Mov_rm64_imm32)) return false;
                    for (var at = offset - slot; at < offset - slot + width; at++) written |= 1 << checked((int)at);
                    if (written == 0xFFF) continue;
                }
            }
            // Before every old byte is replaced, exposing any frame address to
            // another call or storing that pointer needs a separate escape proof.
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                new[] { Register.RCX, Register.RDX, Register.R8, Register.R9 }.Any(aliases.ContainsKey) ||
                instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register &&
                aliases.ContainsKey(instruction.Op1Register.GetFullRegister())) return false;
            if (Writes(instruction, Register.RSP) && instruction.FlowControl is not (FlowControl.Call or FlowControl.IndirectCall))
            {
                if (instruction.Code is Code.Add_rm64_imm8 or Code.Add_rm64_imm32 &&
                    instruction.Op0Register == Register.RSP && instruction.GetImmediate(1) == frame ||
                    instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && instruction.Op0Register == Register.RSP &&
                    ValueOr(aliases, instruction.Op1Register, long.MinValue) == frame) continue;
                return false;
            }
            long? copiedOffset = null;
            if (instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
                aliases.TryGetValue(instruction.Op1Register, out var sourceOffset)) copiedOffset = sourceOffset;
            if (instruction.Code == Code.Lea_r64_m && aliases.TryGetValue(instruction.MemoryBase, out var frameOffset))
            {
                if (instruction.MemoryIndex != Register.None) return false;
                copiedOffset = checked(frameOffset + unchecked((long)instruction.MemoryDisplacement64));
            }
            var after = new Dictionary<Register, long>(aliases);
            foreach (var used in nativeInfo.GetUsedRegisters())
            {
                var register = used.Register.GetFullRegister();
                if (register != Register.RSP && aliases.ContainsKey(register))
                {
                    if (used.Access is OpAccess.CondWrite or OpAccess.ReadCondWrite) return false;
                    if (IsWrite(used.Access) && used.Register.GetSize() < 8 && ZeroRegister(instruction) != register) return false;
                    if (used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                        (aliases[register] == long.MinValue || copiedOffset == null) && ZeroRegister(instruction) != register) return false;
                }
                if (used.Access is OpAccess.Write or OpAccess.ReadWrite) after.Remove(register);
            }
            if (copiedOffset is { } copied) after[instruction.Op0Register] = copied;
            after[Register.RSP] = 0;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {
                if (instruction.Op0Kind != OpKind.NearBranch64 || !addresses.TryGetValue(instruction.NearBranchTarget, out var target)) return false;
                pending.Enqueue((target, written, after));
                if (instruction.FlowControl == FlowControl.UnconditionalBranch) continue;
            }
            if (instruction.FlowControl is FlowControl.IndirectBranch or FlowControl.Exception or FlowControl.Interrupt) return false;
            pending.Enqueue((index + 1, written, after));
        }
        return true;
    }

    private static bool OnlyUsedByStore(IReadOnlyList<Instruction> body, int load, int store, int call,
        Register callResult, Register callerResult, int callResultBytes, int callerResultBytes)
    {
        var register = body[load].Op0Register.GetFullRegister();
        callResult = callResult.GetFullRegister();
        callerResult = callerResult.GetFullRegister();
        // The consuming call kills the lifted volatile scratch. Callee-saved
        // scratch requires a separate all-path liveness proof, which this first
        // recipe deliberately does not claim from a linear later overwrite.
        if (!IsVolatile(register)) return false;
        var info = new InstructionInfoFactory();
        var mask = (1 << body[load].MemorySize.GetSize()) - 1;
        for (var index = load + 1; index < call; index++)
        {
            var instruction = body[index];
            var uses = info.GetInfo(instruction).GetUsedRegisters().Where(used => used.Register.GetFullRegister() == register).ToArray();
            foreach (var used in uses)
            {
                if (HighByteRegister(used.Register)) return false;
                var width = SingleLaneOperation(instruction.Code) && register is >= Register.ZMM0 and <= Register.ZMM5 ? 4 : used.Register.GetSize();
                if (index != store && used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                    (mask & ByteMask(width)) != 0 && ZeroRegister(instruction) != register) return false;
                if (used.Access is OpAccess.Write or OpAccess.ReadWrite) mask &= ~ByteMask(width);
            }
            if (ZeroRegister(instruction) == register) mask = 0;
            if (mask == 0) return index > store;
        }
        // An original Single result defines low32, not the whole packed64
        // scratch. Preserve the remaining old bytes across every successor.
        if (register == callResult) mask &= ~ByteMask(callResultBytes);
        if (mask == 0) return true;
        var addresses = body.Select((instruction, index) => (instruction.IP, index)).ToDictionary(pair => pair.IP, pair => pair.index);
        var pending = new Queue<(int Index, int Mask)>(); pending.Enqueue((call + 1, mask));
        var visited = new HashSet<(int, int)>();
        while (pending.Count != 0)
        {
            var (index, remaining) = pending.Dequeue();
            if (index < 0 || index >= body.Count) return false;
            if (!visited.Add((index, remaining))) continue;
            var instruction = body[index];
            var uses = info.GetInfo(instruction).GetUsedRegisters().Where(used => used.Register.GetFullRegister() == register).ToArray();
            foreach (var used in uses)
            {
                if (HighByteRegister(used.Register)) return false;
                var width = SingleLaneOperation(instruction.Code) && register is >= Register.ZMM0 and <= Register.ZMM5
                    ? 4 : used.Register.GetSize();
                if (used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                    (remaining & ByteMask(width)) != 0 && ZeroRegister(instruction) != register) return false;
                if (used.Access is OpAccess.Write or OpAccess.ReadWrite) remaining &= ~ByteMask(width);
            }
            if (ZeroRegister(instruction) == register) remaining = 0;
            if (remaining == 0) continue;
            if (instruction.FlowControl == FlowControl.Return)
            {
                if (register == callerResult && (remaining & ByteMask(callerResultBytes)) != 0) return false;
                continue;
            }
            if (instruction.Code == Code.Int3) continue;
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall) return false;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {
                if (instruction.Op0Kind != OpKind.NearBranch64 || !addresses.TryGetValue(instruction.NearBranchTarget, out var target)) return false;
                pending.Enqueue((target, remaining));
                if (instruction.FlowControl == FlowControl.UnconditionalBranch) continue;
            }
            if (instruction.FlowControl is FlowControl.IndirectBranch or FlowControl.Exception or FlowControl.Interrupt) return false;
            // No unqualified later call is used as a kill of this value.
            pending.Enqueue((index + 1, remaining));
        }
        return true;
    }

    private static int ByteMask(int width) => width <= 0 ? 0 : (1 << Math.Min(8, width)) - 1;

    private static bool HighByteRegister(Register register) => register is Register.AH or Register.BH or Register.CH or Register.DH;

    private static Register? ZeroRegister(Instruction instruction) => instruction.Code is Code.Xor_r32_rm32 or Code.Xor_rm32_r32 or
        Code.Xor_r64_rm64 or Code.Xor_rm64_r64 && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == instruction.Op1Register ? instruction.Op0Register.GetFullRegister() : null;

    private static bool SingleLaneOperation(Code code) => code is Code.Movss_xmm_xmmm32 or Code.Movss_xmmm32_xmm or
        Code.Addss_xmm_xmmm32 or Code.Subss_xmm_xmmm32 or Code.Mulss_xmm_xmmm32 or Code.Divss_xmm_xmmm32 or
        Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32;

    internal static bool HasZeroRegisterArgument(IReadOnlyList<Instruction> body, ArgumentCopy copy, Register register)
    {
        var call = Index(body, copy.Call);
        var last = call < 0 ? -1 : LastWrite(body, call, register);
        // TryFindArgumentCopy has proved this window branch-free and without
        // interior entries. A linear earlier predecessor definition is not enough.
        if (last < 0 || body[last].IP < copy.FirstRead) return false;
        var instruction = body[last];
        return instruction.Code is Code.Xor_r32_rm32 or Code.Xor_rm32_r32 or Code.Xor_r64_rm64 or Code.Xor_rm64_r64 &&
            instruction.Op0Register.GetFullRegister() == register && instruction.Op1Register.GetFullRegister() == register ||
            instruction.Code is Code.Mov_r32_imm32 or Code.Mov_r64_imm64 && instruction.GetImmediate(1) == 0;
    }

    private static bool IsVolatile(Register register) => register is Register.RAX or Register.RCX or Register.RDX or
        Register.R8 or Register.R9 or Register.R10 or Register.R11 || register is >= Register.ZMM0 and <= Register.ZMM5;
    private static bool IsCopyLoad(Instruction instruction, uint width) => instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryIndex == Register.None && instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == width && (width == 8 ? instruction.Code is Code.Movsd_xmm_xmmm64 or Code.Mov_r64_rm64 :
            instruction.Code == Code.Mov_r32_rm32) && instruction.MemoryBase is not (Register.None or Register.RSP or Register.RIP);
    private static bool IsCopyStore(Instruction instruction, uint offset, uint width, uint frame) =>
        instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register && instruction.MemoryDisplacement64 == offset &&
        FrameMemory(instruction, frame, width) && (width == 8 ? instruction.Code is Code.Movsd_xmmm64_xmm or Code.Mov_rm64_r64 :
            instruction.Code == Code.Mov_rm32_r32);
    private static bool FrameMemory(Instruction instruction, uint frame, uint width) => width <= frame &&
        instruction.MemoryBase == Register.RSP && instruction.MemoryIndex == Register.None && instruction.MemoryIndexScale == 1 &&
        instruction.MemoryDisplacement64 >= 0x20 && instruction.MemoryDisplacement64 <= frame - width;
    private static bool Overlaps(Instruction instruction, uint slot, uint width) =>
        instruction.MemoryDisplacement64 < slot + width &&
        (instruction.Code == Code.Lea_r64_m ? instruction.MemoryDisplacement64 >= slot :
            instruction.MemoryDisplacement64 + (ulong)instruction.MemorySize.GetSize() > slot);
    private static int Index(IReadOnlyList<Instruction> body, ulong address)
    {
        for (var index = 0; index < body.Count; index++) if (body[index].IP == address) return index;
        return -1;
    }
    private static int LastWrite(IReadOnlyList<Instruction> body, int before, Register register)
    {
        for (var index = before - 1; index >= 0; index--) if (Writes(body[index], register)) return index;
        return -1;
    }
    private static T ValueOr<T>(Dictionary<Register, T> values, Register register, T fallback) where T : struct =>
        values.TryGetValue(register, out var value) ? value : fallback;
    private static bool Writes(Instruction instruction, Register register) => new InstructionInfoFactory().GetInfo(instruction)
        .GetUsedRegisters().Any(used => used.Register.GetFullRegister() == register.GetFullRegister() && IsWrite(used.Access));
    private static bool IsWrite(OpAccess access) => access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;
    private static bool Clean(Instruction instruction) => !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix && instruction.SegmentPrefix == Register.None;
}
