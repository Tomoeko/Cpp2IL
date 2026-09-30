using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;

namespace Cpp2IL.Core.Analysis;

/// <summary>Correlates ordinary integer save/allocate/restore frames with their exact root unwind program.</summary>
internal static class X64NativeInvocationFrameProof
{
    internal static bool IsValid(MethodAnalysisContext method, IReadOnlyList<Instruction> body,
        X64NativeInvocationValues values)
    {
        var unwind = X64UnwindProof.ForApplication(method.AppContext);
        return unwind != null && IsValid(body, values, (prolog, codes) =>
            unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, prolog, 0, codes));
    }

    internal static bool IsValid(IReadOnlyList<Instruction> body, X64NativeInvocationValues values,
        System.Func<byte, byte[], bool> matchesUnwind)
    {
        if (body.Count < 3) return false;
        var cursor = 0;
        var homes = new List<Instruction>();
        while (cursor < body.Count && body[cursor] is { Code: Code.Mov_rm64_r64, Op0Kind: OpKind.Memory, Op1Kind: OpKind.Register } save &&
            save.MemoryBase == Register.RSP && save.MemoryIndex == Register.None && save.MemorySize.GetSize() == 8 &&
            save.MemoryDisplacement64 is >= 8 and <= 32 && (save.MemoryDisplacement64 & 7) == 0 && Nonvolatile(save.Op1Register))
        {
            if (homes.Any(previous => previous.Op1Register == save.Op1Register ||
                    previous.MemoryDisplacement64 == save.MemoryDisplacement64)) return false;
            homes.Add(save);
            cursor++;
        }
        var pushes = new List<Instruction>();
        while (cursor < body.Count && body[cursor] is { Code: Code.Push_r64 } push && Nonvolatile(push.Op0Register))
        {
            if (pushes.Count >= 8 || pushes.Any(previous => previous.Op0Register == push.Op0Register) ||
                homes.Any(saved => saved.Op1Register == push.Op0Register)) return false;
            pushes.Add(push);
            cursor++;
        }
        if (cursor >= body.Count || body[cursor] is not { Code: Code.Sub_rm64_imm8 or Code.Sub_rm64_imm32,
                Op0Kind: OpKind.Register, Op0Register: Register.RSP } allocation ||
            allocation.GetImmediate(1) is < 32 or > 128 || (allocation.GetImmediate(1) & 7) != 0)
            return false;
        var allocated = allocation.GetImmediate(1);
        var prologEnd = allocation.NextIP;
        var prologLength = prologEnd - body[0].IP;
        var frameSize = allocated + (ulong)pushes.Count * 8;
        if (prologLength > byte.MaxValue) return false;
        var codes = new List<byte>();
        foreach (var homeSave in homes.AsEnumerable().Reverse())
        {
            var slot = (homeSave.MemoryDisplacement64 + frameSize) / 8;
            if (slot > ushort.MaxValue) return false;
            codes.Add((byte)prologLength);
            codes.Add((byte)((Number(homeSave.Op1Register) << 4) | 4));
            codes.Add((byte)slot);
            codes.Add((byte)(slot >> 8));
        }
        codes.Add((byte)prologLength);
        codes.Add((byte)((((allocated - 8) / 8) << 4) | 2));
        foreach (var push in pushes.AsEnumerable().Reverse())
        {
            codes.Add((byte)(push.NextIP - body[0].IP));
            codes.Add((byte)(Number(push.Op0Register) << 4));
        }
        if (!matchesUnwind((byte)prologLength, codes.ToArray())) return false;
        var savedRegisters = new HashSet<Register>(pushes.Select(push => push.Op0Register));
        savedRegisters.UnionWith(homes.Select(home => home.Op1Register));
        var stackWriters = new HashSet<ulong>(pushes.Select(push => push.IP)) { allocation.IP };
        var information = new InstructionInfoFactory();
        var exits = 0;
        for (var index = 0; index < body.Count; index++)
        {
            var native = body[index];
            if (!values.IsReachable(native.IP)) continue;
            if (native.FlowControl is FlowControl.Call or FlowControl.IndirectCall && !values.HasCallFrame(native.IP, false))
                return false;
            if (native.IP >= prologEnd && native.Op0Kind == OpKind.Memory && native.MemoryBase == Register.RSP ||
                native.IP >= prologEnd && native.Code == Code.Push_r64)
                return false; // Unproved spills can overwrite a saved nonvolatile value.
            foreach (var used in information.GetInfo(native).GetUsedRegisters())
                if (used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                {
                    var register = used.Register.GetFullRegister();
                    if (Nonvolatile(register) && !savedRegisters.Contains(register) ||
                        register is >= Register.ZMM6 and <= Register.ZMM15) return false;
                }
            var exit = native.FlowControl == FlowControl.Return || native.FlowControl == FlowControl.UnconditionalBranch &&
                native.Op0Kind == OpKind.NearBranch64 && (native.NearBranchTarget < body[0].IP || native.NearBranchTarget >= body[^1].NextIP);
            if (!exit) continue;
            exits++;
            if (!values.HasCallFrame(native.IP, true)) return false;
            var previous = index;
            foreach (var push in pushes)
            {
                if (--previous < 0 || body[previous].Code != Code.Pop_r64 || body[previous].Op0Register != push.Op0Register ||
                    !values.Dominates(body[previous].IP, native.IP)) return false;
                stackWriters.Add(body[previous].IP);
            }
            if (--previous < 0 || body[previous].Code is not (Code.Add_rm64_imm8 or Code.Add_rm64_imm32) ||
                body[previous].Op0Kind != OpKind.Register || body[previous].Op0Register != Register.RSP ||
                body[previous].GetImmediate(1) != allocated || !values.Dominates(body[previous].IP, native.IP)) return false;
            stackWriters.Add(body[previous].IP);
            // Home restores can be interleaved with the return calculation. Each
            // must be the final write of its register before this exit and must
            // still read the original slot with the allocated stack offset.
            foreach (var original in homes)
            {
                var restore = body.Take(previous).LastOrDefault(candidate => values.IsReachable(candidate.IP) &&
                    information.GetInfo(candidate).GetUsedRegisters().Any(used =>
                        used.Register.GetFullRegister() == original.Op1Register && used.Access is
                            OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite));
                if (restore is not { Code: Code.Mov_r64_rm64, Op0Kind: OpKind.Register, Op1Kind: OpKind.Memory } ||
                    restore.Op0Register != original.Op1Register || restore.MemoryBase != Register.RSP ||
                    restore.MemoryIndex != Register.None || restore.MemorySize.GetSize() != 8 ||
                    restore.MemoryDisplacement64 != original.MemoryDisplacement64 + frameSize ||
                    !values.Dominates(restore.IP, native.IP) ||
                    !values.HasStackOffset(restore.IP, -checked((int)frameSize))) return false;
            }
        }
        foreach (var native in body.Where(native => values.IsReachable(native.IP)))
            if (native.FlowControl is not (FlowControl.Call or FlowControl.IndirectCall or FlowControl.Return) &&
                !stackWriters.Contains(native.IP) && information.GetInfo(native).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == Register.RSP && used.Access is
                        OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                return false;
        return exits > 0;
    }

    private static bool Nonvolatile(Register register) => register is Register.RBX or Register.RBP or Register.RSI or Register.RDI
        or Register.R12 or Register.R13 or Register.R14 or Register.R15;

    private static int Number(Register register) => register switch
    {
        Register.RBX => 3, Register.RBP => 5, Register.RSI => 6, Register.RDI => 7,
        Register.R12 => 12, Register.R13 => 13, Register.R14 => 14, Register.R15 => 15, _ => -1,
    };
}
