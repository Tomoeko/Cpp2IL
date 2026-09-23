using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds one native rethrow chain to the original exception object and to the
/// player's noncontinuable C++ throw helper. An arbitrary no-return call is not
/// sufficient evidence for managed rethrow semantics.
/// </summary>
internal static class X64FinallyRethrowProof
{
    internal static bool Check(PE pe, X64UnwindProof.Index index, ulong wrapper)
    {
        if (X64NativeInstructionReader.Read(pe, index, wrapper, 2, 16) is not
                [var reserve, var call] ||
            !Stack(reserve, 0x28) || !Call(call, out var rethrow) ||
            X64NativeInstructionReader.Read(pe, index, rethrow, 7, 64) is not
                [var frame, var argument, var wrapperAddress, var construct,
                 var throwInfo, var thrownAddress, var throwCall] ||
            !Stack(frame, 0x28) || !Move(argument, Register.RDX, Register.RCX) ||
            !Address(wrapperAddress, Register.RCX, Register.RSP, 0x38) ||
            !Call(construct, out var constructor) ||
            !RipAddress(throwInfo, Register.RDX) ||
            index.MapReadOnlyData(throwInfo.IPRelativeMemoryAddress, 8) < 0 ||
            !Address(thrownAddress, Register.RCX, Register.RSP, 0x38) ||
            !Call(throwCall, out var cxxThrow) ||
            X64NativeInstructionReader.Read(pe, index, constructor, 3, 16) is not
                [var saveException, var returnWrapper, var ret] ||
            !Store(saveException, Register.RCX, 0, Register.RDX) ||
            !Move(returnWrapper, Register.RAX, Register.RCX) || ret.Code != Code.Retnq ||
            !X64NativeCxxThrowProof.Check(pe, index, cxxThrow))
            return false;
        return true;
    }

    private static bool Stack(Instruction i, ulong size) => i.Mnemonic == Mnemonic.Sub &&
        i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Address(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;

    private static bool RipAddress(Instruction i, Register destination) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RIP && i.MemoryIndex == Register.None;

    private static bool Call(Instruction i, out ulong target)
    {
        target = i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64
            ? i.NearBranchTarget : 0;
        return target != 0;
    }
}
