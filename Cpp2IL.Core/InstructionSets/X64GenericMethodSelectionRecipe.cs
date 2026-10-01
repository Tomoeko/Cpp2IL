using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

// This authenticates native selection order and its lower-call obligations.
// Map identity, cache state and lower effects require separate proof components.
internal static class X64GenericMethodSelectionRecipe
{
    internal sealed class Evidence
    {
        private readonly byte[] _body;
        internal ulong Entry { get; }
        internal ulong MethodTableMap { get; }
        internal ulong Find { get; }
        internal ulong SharedInstantiation { get; }
        internal ulong FullySharedInstantiation { get; }
        internal ulong MakePointers { get; }
        internal bool CompleteHelperQualified => false;

        private Evidence(ulong entry, byte[] body, ulong map, ulong find,
            ulong shared, ulong fullyShared, ulong makePointers)
        {
            Entry = entry;
            _body = (byte[])body.Clone();
            MethodTableMap = map;
            Find = find;
            SharedInstantiation = shared;
            FullySharedInstantiation = fullyShared;
            MakePointers = makePointers;
        }

        internal bool MatchesBody(ReadOnlySpan<byte> current) => current.SequenceEqual(_body);

        internal static Evidence? Identify(ulong entry, byte[] body)
        {
            if (entry == 0 || body.Length != 396 || entry > ulong.MaxValue - 396) return null;
            var c = Decode(entry, body);
            if (c.Length != 97 || c[^1].NextIP != entry + 396) return null;

            // Preserve the complete Win64 frame, result buffer and original method key.
            if (!Store(c[0], Register.RSP, 8, Register.RBX, 8) ||
                !Store(c[1], Register.RSP, 16, Register.RSI, 8) ||
                !Store(c[2], Register.RSP, 24, Register.RDI, 8) ||
                !Store(c[3], Register.RSP, 32, Register.R14, 8) ||
                !OneRegister(c[4], Mnemonic.Push, Register.RBP) ||
                !Reg(c[5], Mnemonic.Mov, Register.RBP, Register.RSP) ||
                !Imm(c[6], Mnemonic.Sub, Register.RSP, 96) ||
                !Load(c[7], Register.RAX, Register.R8, 0, 8) ||
                !Reg(c[8], Mnemonic.Mov, Register.RDI, Register.R8) ||
                !Store(c[9], Register.RBP, -40, Register.RAX, 8) ||
                !Reg(c[10], Mnemonic.Mov, Register.RSI, Register.RDX) ||
                !Load(c[11], Register.RAX, Register.R8, 8, 8) ||
                !Reg(c[12], Mnemonic.Mov, Register.RBX, Register.RCX) ||
                !Store(c[13], Register.RBP, -32, Register.RAX, 8) ||
                !Address(c[14], Register.R8, Register.RBP, -64) ||
                !Address(c[15], Register.RAX, Register.RBP, -48) ||
                !Store(c[16], Register.RBP, -48, Register.RDX, 8) ||
                !Reg(c[17], Mnemonic.Xor, Register.R14D, Register.R14D) ||
                !Store(c[18], Register.RBP, -56, Register.RAX, 8) ||
                !Address(c[19], Register.RDX, Register.RBP, -24) ||
                !Store(c[20], Register.RBP, -64, Register.R14D, 4) ||
                !Rip(c[21], Mnemonic.Lea, Register.RCX, out var map) ||
                map == 0 || map > ulong.MaxValue - 104 ||
                !Call(c[22], out var find) ||
                !IteratorTest(c, 23, map) || !Branch(c[29], Mnemonic.Je, c[35])) return null;

            // Both direct and shared hits use the same non-fully-shared result arm.
            if (!Load(c[30], Register.RDX, Register.RDX, 16, 8) ||
                !Reg(c[31], Mnemonic.Xor, Register.R8D, Register.R8D) ||
                !Reg(c[32], Mnemonic.Mov, Register.RCX, Register.RBX) ||
                !Call(c[33], out var make) || !Branch(c[34], Mnemonic.Jmp, c[88]) ||
                !Load(c[35], Register.RCX, Register.RDI, 0, 8) ||
                !Call(c[36], out var shared) ||
                !Load(c[37], Register.RCX, Register.RDI, 8, 8) ||
                !Store(c[38], Register.RBP, -40, Register.RAX, 8) ||
                !CallTo(c[39], shared) || !Store(c[40], Register.RBP, -32, Register.RAX, 8) ||
                !RepeatFind(c, 41, map, find) || !IteratorTest(c, 48, map) ||
                !Branch(c[54], Mnemonic.Jne, c[30])) return null;

            // A second miss preserves original contexts and original generic containers.
            if (!Load(c[55], Register.RCX, Register.RSI, 32, 8) ||
                !Load(c[56], Register.RDX, Register.RDI, 0, 8) ||
                !Load(c[57], Register.RCX, Register.RCX, 240, 8) ||
                !Call(c[58], out var fullyShared) ||
                !Load(c[59], Register.RDX, Register.RDI, 8, 8) ||
                !Load(c[60], Register.RCX, Register.RSI, 64, 8) ||
                !Store(c[61], Register.RBP, -40, Register.RAX, 8) ||
                !CallTo(c[62], fullyShared) || !Store(c[63], Register.RBP, -32, Register.RAX, 8) ||
                !RepeatFind(c, 64, map, find) || !IteratorTest(c, 71, map) ||
                !Branch(c[77], Mnemonic.Je, c[83]) ||
                !Load(c[78], Register.RDX, Register.RDX, 16, 8) ||
                !Imm(c[79], Mnemonic.Mov, Register.R8L, 1) ||
                !Reg(c[80], Mnemonic.Mov, Register.RCX, Register.RBX) ||
                !CallTo(c[81], make) || !Branch(c[82], Mnemonic.Jmp, c[88])) return null;

            // Missing registration clears all four result words and returns its buffer.
            if (!Reg(c[83], Mnemonic.Xor, Register.EAX, Register.EAX) ||
                !Store(c[84], Register.RBX, 0, Register.R14, 8) ||
                !Store(c[85], Register.RBX, 8, Register.R14, 8) ||
                !Store(c[86], Register.RBX, 16, Register.R14, 8) ||
                !Store(c[87], Register.RBX, 24, Register.RAX, 8) ||
                !Address(c[88], Register.R11, Register.RSP, 96) ||
                !Reg(c[89], Mnemonic.Mov, Register.RAX, Register.RBX) ||
                !Load(c[90], Register.RBX, Register.R11, 16, 8) ||
                !Load(c[91], Register.RSI, Register.R11, 24, 8) ||
                !Load(c[92], Register.RDI, Register.R11, 32, 8) ||
                !Load(c[93], Register.R14, Register.R11, 40, 8) ||
                !Reg(c[94], Mnemonic.Mov, Register.RSP, Register.R11) ||
                !OneRegister(c[95], Mnemonic.Pop, Register.RBP) ||
                c[96].Code != Code.Retnq || c[96].OpCount != 0) return null;

            var targets = new[] { find, shared, fullyShared, make };
            if (targets.Any(address => address == 0 || address >= entry && address < entry + 396) ||
                targets.Distinct().Count() != targets.Length) return null;
            return new Evidence(entry, body, map, find, shared, fullyShared, make);
        }
    }

    internal static Instruction[] Decode(ulong entry, byte[] body)
    {
        if (entry > ulong.MaxValue - (ulong)body.Length) return [];
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(body), entry);
        var result = new List<Instruction>();
        while (decoder.IP < entry + (ulong)body.Length)
        {
            var i = decoder.Decode();
            if (i.IsInvalid || i.CodeSize != CodeSize.Code64 || i.HasLockPrefix || i.HasRepPrefix ||
                i.HasRepnePrefix || i.SegmentPrefix != Register.None || i.NextIP > entry + (ulong)body.Length)
                return [];
            result.Add(i);
        }
        return result.ToArray();
    }

    private static bool IteratorTest(Instruction[] c, int at, ulong map) =>
        RipAt(c[at], Mnemonic.Mov, Register.RAX, map + 72) &&
        Load(c[at + 1], Register.RDX, Register.RBP, -16, 8) &&
        Address(c[at + 2], Register.RCX, Register.RAX, 0, Register.RAX, 2) &&
        RipAt(c[at + 3], Mnemonic.Mov, Register.RAX, map + 104) &&
        Address(c[at + 4], Register.R8, Register.RAX, 0, Register.RCX, 8) &&
        Reg(c[at + 5], Mnemonic.Cmp, Register.RDX, Register.R8);

    private static bool RepeatFind(Instruction[] c, int at, ulong map, ulong find) =>
        Address(c[at], Register.R8, Register.RBP, -64) &&
        Address(c[at + 1], Register.RAX, Register.RBP, -48) &&
        Store(c[at + 2], Register.RBP, -64, Register.R14D, 4) &&
        Address(c[at + 3], Register.RDX, Register.RBP, -24) &&
        Store(c[at + 4], Register.RBP, -56, Register.RAX, 8) &&
        RipAt(c[at + 5], Mnemonic.Lea, Register.RCX, map) && CallTo(c[at + 6], find);

    private static bool Rip(Instruction i, Mnemonic mnemonic, Register destination, out ulong address)
    {
        address = 0;
        if (i.Mnemonic != mnemonic || i.OpCount != 2 || i.Op0Kind != OpKind.Register ||
            i.Op0Register != destination || i.Op1Kind != OpKind.Memory || !i.IsIPRelativeMemoryOperand ||
            i.MemoryBase != Register.RIP || i.MemoryIndex != Register.None ||
            i.MemorySegment != Register.DS || mnemonic == Mnemonic.Mov && i.MemorySize.GetSize() != 8)
            return false;
        address = i.IPRelativeMemoryAddress;
        return true;
    }
    private static bool RipAt(Instruction i, Mnemonic mnemonic, Register destination, ulong address) =>
        Rip(i, mnemonic, destination, out var actual) && actual == address;
    private static bool Reg(Instruction i, Mnemonic mnemonic, Register destination, Register source) =>
        i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
        i.Op0Register == destination && i.Op1Register == source;
    private static bool OneRegister(Instruction i, Mnemonic mnemonic, Register register) =>
        i.Mnemonic == mnemonic && i.OpCount == 1 && i.Op0Kind == OpKind.Register && i.Op0Register == register;
    private static bool Imm(Instruction i, Mnemonic mnemonic, Register register, ulong value) =>
        i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind is OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or
            OpKind.Immediate32 or OpKind.Immediate32to64 && i.GetImmediate(1) == value;
    private static bool Memory(Instruction i, Register source, long displacement,
        Register index = Register.None, int scale = 1) =>
        i.MemoryBase == source && i.MemoryIndex == index && i.MemoryIndexScale == scale &&
        unchecked((long)i.MemoryDisplacement64) == displacement &&
        i.MemorySegment == (source is Register.RBP or Register.RSP ? Register.SS : Register.DS);
    private static bool Load(Instruction i, Register destination, Register source, long displacement, int width) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && Memory(i, source, displacement) && i.MemorySize.GetSize() == width;
    private static bool Store(Instruction i, Register destination, long displacement, Register source, int width) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Memory && i.Op1Kind == OpKind.Register &&
        i.Op1Register == source && Memory(i, destination, displacement) && i.MemorySize.GetSize() == width;
    private static bool Address(Instruction i, Register destination, Register source, long displacement,
        Register index = Register.None, int scale = 1) =>
        i.Mnemonic == Mnemonic.Lea && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && Memory(i, source, displacement, index, scale);
    private static bool Call(Instruction i, out ulong target)
    {
        target = i.NearBranchTarget;
        return i.Code == Code.Call_rel32_64 && i.OpCount == 1 && i.Op0Kind == OpKind.NearBranch64;
    }
    private static bool CallTo(Instruction i, ulong target) => Call(i, out var actual) && actual == target;
    private static bool Branch(Instruction i, Mnemonic mnemonic, Instruction target) =>
        i.Mnemonic == mnemonic && i.OpCount == 1 && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target.IP;
}
