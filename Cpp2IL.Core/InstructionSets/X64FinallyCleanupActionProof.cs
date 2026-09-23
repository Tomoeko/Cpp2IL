using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves that an EH4 object cleanup increments one pointed-to Int32 and, if
/// its saved exception is nonnull, rethrows that same exception object.
/// </summary>
internal static class X64FinallyCleanupActionProof
{
    internal static bool Check(PE pe, X64UnwindProof.Index index, ulong action)
    {
        var span = index.ClassifySpan(action, action + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != action ||
            X64NativeInstructionReader.Read(pe, index, action, 10, 64) is not
                [var reserve, var loadSlot, var loadCounter, var increment,
                 var loadException, var test, var branch, var release,
                 var ret, var rethrow])
            return false;
        return X64NativePaddingProof.HasInt3Padding(pe, rethrow.NextIP, span.End) &&
               Stack(reserve, Mnemonic.Sub, 0x28) &&
               Load(loadSlot, Register.RAX, Register.RCX, 8) &&
               Load(loadCounter, Register.RDX, Register.RAX, 0) &&
               increment.Mnemonic == Mnemonic.Inc && increment.Op0Kind == OpKind.Memory &&
               increment.MemoryBase == Register.RDX && increment.MemoryIndex == Register.None &&
               increment.MemoryDisplacement64 == 0 && increment.MemorySize.GetSize() == 4 &&
               Load(loadException, Register.RCX, Register.RCX, 0) &&
               test.Mnemonic == Mnemonic.Test && test.Op0Kind == OpKind.Register &&
               test.Op0Register == Register.RCX && test.Op1Kind == OpKind.Register &&
               test.Op1Register == Register.RCX &&
               branch.Mnemonic == Mnemonic.Jne && branch.Op0Kind == OpKind.NearBranch64 &&
               branch.NearBranchTarget == rethrow.IP &&
               Stack(release, Mnemonic.Add, 0x28) && ret.Code == Code.Retnq &&
               rethrow.Code == Code.Call_rel32_64 && rethrow.Op0Kind == OpKind.NearBranch64 &&
               X64FinallyRethrowProof.Check(pe, index, rethrow.NearBranchTarget);
    }

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Load(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis && i.MemoryIndex == Register.None &&
        i.MemoryDisplacement64 == offset;
}
