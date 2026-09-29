using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a pointer copied through integer registers on every entry path to a use.
/// Partial writes, volatile call clobbers, spills and cyclic provenance fail closed.
/// </summary>
internal static class X64NativeRegisterAliasProof
{
    internal static bool IsAlias(IReadOnlyList<Instruction> body, ulong useAddress,
        Register useRegister, Register entryRegister, ulong? resultCall = null)
        => IsAliasCore(body, useAddress, useRegister, entryRegister, resultCall,
            resultCall == null ? null : Register.RAX, requireCall: resultCall != null);

    // The caller must bind this load's memory operand to unchanged field metadata
    // and independently prove its base. A field is captured here once; another
    // load of the same address is not the same snapshot after intervening effects.
    internal static bool IsAliasFromFieldLoad(IReadOnlyList<Instruction> body,
        ulong useAddress, Register useRegister, ulong loadAddress)
    {
        var sources = body.Where(instruction => instruction.IP == loadAddress).ToArray();
        if (sources is not [{ Code: Code.Mov_r64_rm64, Op0Kind: OpKind.Register,
                Op1Kind: OpKind.Memory } load] ||
            load.Op0Register is < Register.RAX or > Register.R15 ||
            load.Op0Register == Register.RSP || load.MemoryBase == Register.RSP ||
            load.MemoryBase is < Register.RAX or > Register.R15 ||
            load.MemoryIndex != Register.None || load.MemorySize.GetSize() != 8 ||
            load.HasLockPrefix || load.HasRepPrefix || load.HasRepnePrefix ||
            load.SegmentPrefix != Register.None)
            return false;
        return IsAliasCore(body, useAddress, useRegister, Register.None,
            loadAddress, load.Op0Register, requireCall: false);
    }

    private static bool IsAliasCore(IReadOnlyList<Instruction> body, ulong useAddress,
        Register useRegister, Register entryRegister, ulong? definitionAddress,
        Register? definitionRegister, bool requireCall)
    {
        if (body.Count is 0 or > 512 || useRegister == Register.RSP ||
            useRegister is < Register.RAX or > Register.R15 ||
            definitionAddress == null && entryRegister is < Register.RAX or > Register.R15)
            return false;
        var addresses = new Dictionary<ulong, int>();
        for (var index = 0; index < body.Count; index++)
        {
            if (body[index].IsInvalid || body[index].CodeSize != CodeSize.Code64 ||
                addresses.ContainsKey(body[index].IP) ||
                index != 0 && body[index - 1].NextIP != body[index].IP)
                return false;
            addresses.Add(body[index].IP, index);
        }
        if (!addresses.TryGetValue(useAddress, out var use) || definitionAddress is { } definition &&
            (!addresses.TryGetValue(definition, out var source) ||
             requireCall && body[source].Code != Code.Call_rel32_64))
            return false;

        var predecessors = Enumerable.Range(0, body.Count).Select(_ => new List<int>()).ToArray();
        var successors = Enumerable.Range(0, body.Count).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < body.Count; index++)
        {
            var native = body[index];
            switch (native.FlowControl)
            {
                case FlowControl.Return:
                case FlowControl.Interrupt:
                    break;
                case FlowControl.ConditionalBranch:
                case FlowControl.UnconditionalBranch:
                    if (native.Op0Kind != OpKind.NearBranch64 ||
                        !addresses.TryGetValue(native.NearBranchTarget, out var target))
                        return false;
                    AddEdge(index, target);
                    if (native.FlowControl == FlowControl.UnconditionalBranch)
                        break;
                    goto case FlowControl.Next;
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                case FlowControl.Next:
                    if (index + 1 < body.Count)
                        AddEdge(index, index + 1);
                    break;
                default:
                    return false;
            }
        }
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (reachable.Add(index))
                foreach (var successor in successors[index])
                    pending.Push(successor);
        }
        if (!reachable.Contains(use))
            return false;

        var information = new InstructionInfoFactory();
        var memo = new Dictionary<(int Index, Register Register), bool>();
        var visiting = new HashSet<(int Index, Register Register)>();
        return Before(use, useRegister);

        void AddEdge(int from, int to)
        {
            successors[from].Add(to);
            predecessors[to].Add(from);
        }

        bool Before(int index, Register register)
        {
            if (register == Register.RSP)
                return false;
            if (index == 0)
                return definitionAddress == null && register == entryRegister &&
                       !predecessors[0].Any(reachable.Contains);
            var key = (index, register);
            if (memo.TryGetValue(key, out var proved))
                return proved;
            if (!visiting.Add(key))
                return false;
            var inputs = predecessors[index].Where(reachable.Contains).ToArray();
            proved = inputs.Length != 0 && inputs.All(previous => After(previous, register));
            visiting.Remove(key);
            memo[key] = proved;
            return proved;
        }

        bool After(int index, Register register)
        {
            var native = body[index];
            if (native.IP == definitionAddress && register == definitionRegister)
                return true;
            if (native.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                register is Register.RAX or Register.RCX or Register.RDX or
                    Register.R8 or Register.R9 or Register.R10 or Register.R11)
                return false;
            if (native.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 &&
                native.Op0Kind == OpKind.Register && native.Op0Register == register &&
                native.Op1Kind == OpKind.Register &&
                native.Op1Register is >= Register.RAX and <= Register.R15 &&
                !native.HasLockPrefix && !native.HasRepPrefix && !native.HasRepnePrefix &&
                native.SegmentPrefix == Register.None)
                return Before(index, native.Op1Register);
            if (information.GetInfo(native).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == register &&
                    used.Access is OpAccess.Write or OpAccess.CondWrite or
                        OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                return false;
            return Before(index, register);
        }
    }
}
