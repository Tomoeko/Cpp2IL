using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Checks that one native EH funclet saves the caught wrapper into the cleanup object.</summary>
internal static class X64FinallyFuncletProof
{
    internal static bool Check(PE pe, X64UnwindProof.Index index, ulong funclet, uint objectOffset)
    {
        var span = index.ClassifySpan(funclet, funclet + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != funclet ||
            X64NativeInstructionReader.Read(pe, index, funclet, 11, 64) is not
                [var saveFrame, var push, var reserve, var retainFrame,
                 var loadWrapperAddress, var loadException, var saveException,
                 var zero, var release, var pop, var ret])
            return false;
        return X64NativePaddingProof.HasInt3Padding(pe, ret.NextIP, span.End) &&
               Store(saveFrame, Register.RSP, 0x10, Register.RDX) &&
               push.Mnemonic == Mnemonic.Push && push.Op0Kind == OpKind.Register &&
               push.Op0Register == Register.RBP && Stack(reserve, Mnemonic.Sub, 0x20) &&
               Move(retainFrame, Register.RBP, Register.RDX) &&
               Load(loadWrapperAddress, Register.RAX, Register.RBP, 0x20) &&
               Load(loadException, Register.RCX, Register.RAX, 0) &&
               Store(saveException, Register.RBP, objectOffset, Register.RCX) &&
               zero.Mnemonic == Mnemonic.Mov && zero.Op0Kind == OpKind.Register &&
               zero.Op0Register == Register.RAX &&
               zero.Op1Kind is OpKind.Immediate64 or OpKind.Immediate32to64 &&
               zero.GetImmediate(1) == 0 &&
               Stack(release, Mnemonic.Add, 0x20) &&
               pop.Mnemonic == Mnemonic.Pop && pop.Op0Kind == OpKind.Register &&
               pop.Op0Register == Register.RBP && ret.Code == Code.Retnq;
    }

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Load(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis && i.MemoryIndex == Register.None &&
        i.MemoryDisplacement64 == offset;

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;
}
