using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Register provenance for one authenticated native caller. Direct external
/// jumps end a path; only independently established no-return calls do so.
/// Unknown writes, spills and cyclic reaching definitions fail closed.
/// </summary>
internal sealed class X64NativeInvocationValues
{
    internal readonly record struct Source(Register Entry, ulong? Definition = null,
        Register DefinedRegister = Register.None, ulong? Literal = null);

    private readonly IReadOnlyList<Instruction> _body;
    private readonly Dictionary<ulong, int> _addresses;
    private readonly List<int>[] _predecessors;
    private readonly HashSet<int> _reachable;
    private readonly InstructionInfoFactory _information = new();
    private readonly Dictionary<int, (int Delta, int Reserved)?> _callFrames = new();

    private X64NativeInvocationValues(IReadOnlyList<Instruction> body,
        Dictionary<ulong, int> addresses, List<int>[] predecessors, HashSet<int> reachable)
    {
        _body = body;
        _addresses = addresses;
        _predecessors = predecessors;
        _reachable = reachable;
    }

    internal static X64NativeInvocationValues? Create(IReadOnlyList<Instruction> body,
        ISet<ulong> noReturn)
    {
        if (body.Count is 0 or > 512)
            return null;
        var addresses = new Dictionary<ulong, int>();
        for (var index = 0; index < body.Count; index++)
        {
            var native = body[index];
            if (native.IsInvalid || native.CodeSize != CodeSize.Code64 || native.HasLockPrefix ||
                native.HasRepPrefix || native.HasRepnePrefix || native.SegmentPrefix != Register.None ||
                addresses.ContainsKey(native.IP) || index > 0 && body[index - 1].NextIP != native.IP)
                return null;
            addresses.Add(native.IP, index);
        }
        var predecessors = Enumerable.Range(0, body.Count).Select(_ => new List<int>()).ToArray();
        var successors = Enumerable.Range(0, body.Count).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < body.Count; index++)
        {
            var native = body[index];
            if (noReturn.Contains(native.IP))
            {
                if (native.Code != Code.Call_rel32_64)
                    return null;
                continue;
            }
            switch (native.FlowControl)
            {
                case FlowControl.Return:
                case FlowControl.Interrupt:
                    break;
                case FlowControl.UnconditionalBranch:
                case FlowControl.ConditionalBranch:
                    if (native.Op0Kind != OpKind.NearBranch64)
                        return null;
                    if (addresses.TryGetValue(native.NearBranchTarget, out var target))
                        Add(index, target);
                    else if (native.FlowControl != FlowControl.UnconditionalBranch ||
                             native.Code is not (Code.Jmp_rel8_64 or Code.Jmp_rel32_64) ||
                             native.NearBranchTarget >= body[0].IP && native.NearBranchTarget < body[^1].NextIP)
                        return null;
                    if (native.FlowControl == FlowControl.UnconditionalBranch)
                        break;
                    goto case FlowControl.Next;
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                case FlowControl.Next:
                    if (index + 1 < body.Count)
                        Add(index, index + 1);
                    else
                        return null;
                    break;
                default:
                    return null;
            }
        }
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (reachable.Add(index))
                foreach (var next in successors[index]) pending.Push(next);
        }
        return new(body, addresses, predecessors, reachable);

        void Add(int from, int to)
        {
            predecessors[to].Add(from);
            successors[from].Add(to);
        }
    }

    internal bool Dominates(ulong definition, ulong use)
    {
        if (!_addresses.TryGetValue(definition, out var source) || !_addresses.TryGetValue(use, out var destination) ||
            !_reachable.Contains(source) || !_reachable.Contains(destination))
            return false;
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(destination);
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (index == source) continue;
            if (index == 0) return false;
            if (!visited.Add(index)) continue;
            var inputs = _predecessors[index].Where(_reachable.Contains).ToArray();
            if (inputs.Length == 0) return false;
            foreach (var previous in inputs) pending.Push(previous);
        }
        return true;
    }

    internal bool IsReachable(ulong address) => _addresses.TryGetValue(address, out var index) && _reachable.Contains(index);

    internal bool Matches(ulong useAddress, Register register, int bits, Source source)
    {
        if (!_addresses.TryGetValue(useAddress, out var use) || !_reachable.Contains(use) ||
            register is < Register.RAX or > Register.R15 || register == Register.RSP || bits is not (8 or 16 or 32 or 64) ||
            source.Definition is { } definition && !_addresses.ContainsKey(definition))
            return false;
        var memo = new Dictionary<(int, Register, int), bool>();
        var visiting = new HashSet<(int, Register, int)>();
        return Before(use, register, bits);

        bool Before(int index, Register value, int width)
        {
            if (value == Register.RSP) return false;
            if (index == 0)
                return source.Definition == null && source.Literal == null && value == source.Entry &&
                       !_predecessors[0].Any(_reachable.Contains);
            var key = (index, value, width);
            if (memo.TryGetValue(key, out var result)) return result;
            if (!visiting.Add(key)) return false;
            var inputs = _predecessors[index].Where(_reachable.Contains).ToArray();
            result = inputs.Length != 0 && inputs.All(previous => After(previous, value, width));
            visiting.Remove(key);
            memo[key] = result;
            return result;
        }

        bool After(int index, Register value, int width)
        {
            var native = _body[index];
            if (native.IP == source.Definition && value == source.DefinedRegister)
                return true; // The typed consumer independently authenticates this definition.
            if (native.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                value is Register.RAX or Register.RCX or Register.RDX or Register.R8 or Register.R9 or Register.R10 or Register.R11)
                return false;
            if (native.Op0Kind == OpKind.Register && native.Op0Register.GetFullRegister() == value &&
                native.Op0Register is not (Register.AH or Register.BH or Register.CH or Register.DH))
            {
                var written = native.Op0Register.GetSize() * 8;
                if (source.Literal is { } literal &&
                    native.Mnemonic == Mnemonic.Mov && native.Op1Kind is
                        OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64 or OpKind.Immediate32to64 &&
                    (written >= width || written == 32 && width == 64) &&
                    Equal(native.GetImmediate(1), literal, width, written))
                    return true;
                if (source.Literal is { } zero && (zero & Mask(width)) == 0 && native.Mnemonic == Mnemonic.Xor &&
                    native.Op1Kind == OpKind.Register && native.Op0Register == native.Op1Register &&
                    (written >= width || written == 32 && width == 64))
                    return true;
                if (source.Literal is { } displacement && width == 32 && written == 32 &&
                    native.Code == Code.Lea_r32_m && native.Op1Kind == OpKind.Memory &&
                    native.MemoryIndex == Register.None && native.MemoryBase is >= Register.RAX and <= Register.R15 &&
                    native.MemoryBase != Register.RSP &&
                    (uint)native.MemoryDisplacement64 == (uint)displacement &&
                    Matches(native.IP, native.MemoryBase, 64, new(Register.None, Literal: 0)))
                    return true;
                if (native.Mnemonic == Mnemonic.Mov && native.Op1Kind == OpKind.Register &&
                    native.Op1Register is not (Register.AH or Register.BH or Register.CH or Register.DH) &&
                    written >= width && native.Op1Register.GetSize() * 8 >= width)
                    return Before(index, native.Op1Register.GetFullRegister(), width);
                if (native.Mnemonic == Mnemonic.Movzx && native.Op1Kind == OpKind.Register &&
                    native.Op1Register is not (Register.AH or Register.BH or Register.CH or Register.DH) &&
                    written >= width && native.Op1Register.GetSize() * 8 >= width)
                    return Before(index, native.Op1Register.GetFullRegister(), width);
                if (native.Mnemonic is Mnemonic.Movsx or Mnemonic.Movsxd && native.Op1Kind == OpKind.Register &&
                    native.Op1Register is not (Register.AH or Register.BH or Register.CH or Register.DH) &&
                    written >= width && native.Op1Register.GetSize() * 8 >= width)
                    return Before(index, native.Op1Register.GetFullRegister(), width);
                if (native.Code == Code.Cdqe && value == Register.RAX && width <= 32)
                    return Before(index, value, width);
                if (source.Literal is { } small && width == 64 && small <= uint.MaxValue && written == 32 &&
                    native.Mnemonic == Mnemonic.Mov && native.Op1Kind == OpKind.Register &&
                    native.Op1Register.GetSize() == 4)
                    return Before(index, native.Op1Register.GetFullRegister(), 32);
            }
            if (_information.GetInfo(native).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == value && used.Access is
                        OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                return false;
            return Before(index, value, width);
        }
    }

    internal bool HasCallFrame(ulong useAddress, bool tail)
    {
        if (!_addresses.TryGetValue(useAddress, out var use) || !_reachable.Contains(use)) return false;
        var visiting = new HashSet<int>();
        return Before(use) is { } frame && (tail ? frame.Delta == 0 :
            frame.Reserved >= 32 && frame.Delta <= -32 && (frame.Delta & 15) == 8);

        (int Delta, int Reserved)? Before(int index)
        {
            if (_callFrames.TryGetValue(index, out var cached)) return cached;
            if (index == 0)
                return _callFrames[index] = !_predecessors[0].Any(_reachable.Contains) ? (0, 0) : null;
            if (!visiting.Add(index)) return null;
            (int Delta, int Reserved)? value = null;
            foreach (var previous in _predecessors[index].Where(_reachable.Contains))
            {
                if (Before(previous) is not { } input || !Shift(_body[previous], ref input) ||
                    value is { } established && input != established)
                {
                    visiting.Remove(index);
                    return _callFrames[index] = null;
                }
                value = input;
            }
            visiting.Remove(index);
            // The native graph is immutable for this proof. Share each merged
            // frame across callsites; branch diamonds must not enumerate paths.
            return _callFrames[index] = value;
        }

        bool Shift(Instruction native, ref (int Delta, int Reserved) frame)
        {
            if (native.Code == Code.Push_r64 && native.Op0Register != Register.RSP)
            { frame = (frame.Delta - 8, 0); return true; }
            if (native.Code == Code.Pop_r64 && native.Op0Register != Register.RSP)
            { frame = (frame.Delta + 8, 0); return true; }
            if (native.Op0Kind == OpKind.Register && native.Op0Register == Register.RSP &&
                native.Mnemonic is Mnemonic.Add or Mnemonic.Sub && native.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
                native.GetImmediate(1) is > 0 and <= 4096)
            {
                var amount = (int)native.GetImmediate(1);
                frame = native.Mnemonic == Mnemonic.Sub
                    ? (frame.Delta - amount, frame.Reserved + amount)
                    : (frame.Delta + amount, System.Math.Max(0, frame.Reserved - amount));
                return true;
            }
            return !_information.GetInfo(native).GetUsedRegisters().Any(used => used.Register.GetFullRegister() == Register.RSP &&
                used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite) ||
                native.FlowControl is FlowControl.Call or FlowControl.IndirectCall;
        }
    }

    private static bool Equal(ulong actual, ulong expected, int bits, int written)
        => (written == 32 && bits == 64 ? actual & uint.MaxValue : actual & Mask(bits)) == (expected & Mask(bits));

    private static ulong Mask(int bits) => bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
}
