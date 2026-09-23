using System.Collections.Generic;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves that a native double-width dividend is a single managed integer.</summary>
internal static class X86DivisionProof
{
    public static HashSet<ulong> FindSingleWidthDividends(IReadOnlyList<Instruction> body)
    {
        var entries = new HashSet<ulong>();
        foreach (var instruction in body)
        {
            // An indirect entry cannot be bounded by this local proof.
            if (instruction.FlowControl == FlowControl.IndirectBranch)
                return [];
            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                entries.Add(instruction.NearBranchTarget);
        }

        var proved = new HashSet<ulong>();
        var infoFactory = new InstructionInfoFactory();
        for (var index = 0; index < body.Count; index++)
        {
            var division = body[index];
            if (division.Mnemonic is not (Mnemonic.Div or Mnemonic.Idiv) || entries.Contains(division.IP))
                continue;
            // Field resolution must not replace a native dword/qword read with a narrower
            // field at the same offset. Memory divisors need their own read-width proof.
            if (division.Op0Kind != OpKind.Register)
                continue;
            var width = division.Op0Register.GetSize();
            if (width is not (4 or 8))
                continue;

            for (var previous = index - 1; previous >= 0; previous--)
            {
                var candidate = body[previous];
                if (candidate.FlowControl != FlowControl.Next)
                    break;
                var writesHigh = false;
                var writesLow = false;
                foreach (var register in infoFactory.GetInfo(candidate).GetUsedRegisters())
                {
                    if (register.Access is not (OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                        continue;
                    writesHigh |= register.Register.GetFullRegister() == Register.RDX;
                    writesLow |= register.Register.GetFullRegister() == Register.RAX;
                }
                if (writesHigh)
                {
                    if (division.Mnemonic == Mnemonic.Div ? SetsHighToZero(candidate) :
                        width == 4 ? candidate.Mnemonic == Mnemonic.Cdq : candidate.Mnemonic == Mnemonic.Cqo)
                        proved.Add(division.IP);
                    break;
                }
                // Sign extension must describe the same low half consumed by IDIV.
                if ((division.Mnemonic == Mnemonic.Idiv && writesLow) || entries.Contains(candidate.IP))
                    break;
            }
        }
        return proved;
    }

    private static bool SetsHighToZero(Instruction instruction)
    {
        // A write to EDX also clears the upper32 bits of RDX in x64 mode. DX/DL/DH
        // writes do not establish either full dividend width and must not qualify.
        if (instruction.Op0Kind != OpKind.Register || instruction.Op0Register is not (Register.EDX or Register.RDX))
            return false;
        if (instruction.Mnemonic == Mnemonic.Xor && instruction.Op1Kind == OpKind.Register)
            return instruction.Op1Register == instruction.Op0Register;
        return instruction.Mnemonic == Mnemonic.Mov && instruction.Op1Kind is
            OpKind.Immediate32 or OpKind.Immediate32to64 or OpKind.Immediate64 && instruction.GetImmediate(1) == 0;
    }
}
