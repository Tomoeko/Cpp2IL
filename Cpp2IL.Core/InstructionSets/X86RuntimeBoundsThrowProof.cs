using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identifies the exact target's nonreturning index exception path. This is a runtime
/// operation, not an ordinary managed constructor/throw: ObjectInit suppresses exceptions
/// raised by the constructor. Only a proved implicit array access may replace this path.
/// </summary>
internal static class X86RuntimeBoundsThrowProof
{
    internal static bool TryIdentify(ApplicationAnalysisContext app, ulong target)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X86RuntimeNullThrowProof.BindIdentity(app, "IndexOutOfRangeException") == null)
            return false;
        try
        {
            var unwind = X64UnwindProof.ForApplication(app);
            return TryProve(target, Read, pe.GetVirtualAddressOfExportedFunctionByName,
                address => X86RuntimeNullThrowProof.ReadReadOnlyName(pe.GetRawBinaryContent(), unwind, address),
                regions => AllowsUnwind(unwind, regions));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return false;
        }

        IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            var start = pe.GetVirtualAddressOfPrimaryExecutableSection();
            var code = pe.GetEntirePrimaryExecutableSection();
            if (address < start || address - start >= (ulong)code.Length)
                return null;
            var offset = checked((int)(address - start));
            var bytes = code.Slice(offset, Math.Min(count * 15, code.Length - offset)).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
            var instructions = new Instruction[count];
            for (var i = 0; i < count; i++)
                instructions[i] = decoder.Decode();
            return instructions;
        }
    }

    internal enum Frame { Stack28, PushRbxStack30 }
    internal readonly record struct Region(ulong Start, ulong End, Frame Prolog);

    internal static bool TryProve(ulong target, Func<ulong, int, IReadOnlyList<Instruction>?> read,
        Func<string, ulong> export, Func<ulong, string?> readString,
        Func<IReadOnlyList<Region>, bool> allowsUnwind)
    {
        var corlib = Thunk("il2cpp_get_corlib");
        var lookup = Thunk("il2cpp_class_from_name");
        var initialize = Thunk("il2cpp_runtime_object_init");
        var allocatorExport = Read(export("il2cpp_object_new"), 3);
        if (corlib == 0 || lookup == 0 || initialize == 0 || allocatorExport == null ||
            !Stack(allocatorExport[0], Mnemonic.Sub, 0x28) || !Call(allocatorExport[1]) ||
            !Jump(allocatorExport[2]))
            return false;
        var allocatorExit = Read(allocatorExport[2].NearBranchTarget, 2);
        if (allocatorExit == null || !Stack(allocatorExit[0], Mnemonic.Add, 0x28) ||
            allocatorExit[1].Code != Code.Retnq)
            return false;
        var allocate = allocatorExport[1].NearBranchTarget;
        var raiserExport = Read(export("il2cpp_raise_exception"), 3);
        if (raiserExport == null || !Stack(raiserExport[0], Mnemonic.Sub, 0x28) ||
            !Registers(raiserExport[1], Mnemonic.Xor, Register.EDX, Register.EDX) ||
            !Call(raiserExport[2]))
            return false;
        var raise = raiserExport[2].NearBranchTarget;
        if (new[] { corlib, lookup, initialize, allocate, raise }.Distinct().Count() != 5)
            return false;

        // Codegen wrapper -> GetIndexOutOfRangeException/raise -> no-message factory.
        var wrapper = Read(target, 2);
        if (wrapper == null || !Stack(wrapper[0], Mnemonic.Sub, 0x28) || !Call(wrapper[1]))
            return false;
        var constructAndRaise = Read(wrapper[1].NearBranchTarget, 5);
        if (constructAndRaise == null || !Stack(constructAndRaise[0], Mnemonic.Sub, 0x28) ||
            !Call(constructAndRaise[1]) ||
            !Registers(constructAndRaise[2], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Registers(constructAndRaise[3], Mnemonic.Xor, Register.EDX, Register.EDX) ||
            !Call(constructAndRaise[4], raise))
            return false;

        // The private message view is zeroed in this frame and its address never escapes.
        // Therefore the conditional message-write block cannot execute. The factory
        // resolves the exact corlib class, allocates it and calls runtime ObjectInit.
        var factory = Read(constructAndRaise[1].NearBranchTarget, 17);
        if (factory == null || !Unary(factory[0], Mnemonic.Push, Register.RBX) ||
            !Stack(factory[1], Mnemonic.Sub, 0x30) ||
            !Registers(factory[2], Mnemonic.Xor, Register.EAX, Register.EAX) ||
            !Store(factory[3], Register.RSP, 0x20, Register.RAX) ||
            !Store(factory[4], Register.RSP, 0x28, Register.RAX) ||
            !Call(factory[5], corlib) ||
            !Literal(factory[6], Register.R8, "IndexOutOfRangeException") ||
            !Registers(factory[7], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Literal(factory[8], Register.RDX, "System") ||
            !Call(factory[9], lookup) ||
            !Registers(factory[10], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Call(factory[11], allocate) ||
            !Registers(factory[12], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Registers(factory[13], Mnemonic.Mov, Register.RBX, Register.RAX) ||
            !Call(factory[14], initialize) ||
            factory[15].Mnemonic != Mnemonic.Cmp || factory[15].OpCount != 2 ||
            !Memory(factory[15], 0, Register.RSP, 0x28) ||
            factory[15].Op1Kind is not (OpKind.Immediate8to64 or OpKind.Immediate32to64) ||
            factory[15].GetImmediate(1) != 0 ||
            factory[16].Mnemonic != Mnemonic.Jbe || factory[16].Op0Kind != OpKind.NearBranch64 ||
            factory[16].NearBranchTarget < factory[16].NextIP ||
            factory[16].NearBranchTarget - factory[16].NextIP > 64)
            return false;
        var exit = Read(factory[16].NearBranchTarget, 4);
        if (exit == null || !Registers(exit[0], Mnemonic.Mov, Register.RAX, Register.RBX) ||
            !Stack(exit[1], Mnemonic.Add, 0x30) ||
            !Unary(exit[2], Mnemonic.Pop, Register.RBX) ||
            exit[3].Code != Code.Retnq)
            return false;
        return allowsUnwind([
            new(wrapper[0].IP, wrapper[^1].NextIP, Frame.Stack28),
            new(constructAndRaise[0].IP, constructAndRaise[^1].NextIP, Frame.Stack28),
            new(factory[0].IP, exit[^1].NextIP, Frame.PushRbxStack30),
        ]);

        IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            if (address == 0 || read(address, count) is not { } body || body.Count != count)
                return null;
            var next = address;
            foreach (var instruction in body)
            {
                if (instruction.IP != next || instruction.Length == 0 || instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                    instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != Register.None)
                    return null;
                next = instruction.NextIP;
            }
            return body;
        }

        ulong Thunk(string name)
        {
            var body = Read(export(name), 1);
            return body != null && Jump(body[0]) ? body[0].NearBranchTarget : 0;
        }

        bool Literal(Instruction instruction, Register destination, string value)
            => instruction.Mnemonic == Mnemonic.Lea && instruction.OpCount == 2 &&
               instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
               instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == Register.RIP &&
               instruction.MemoryIndex == Register.None &&
               readString(instruction.IPRelativeMemoryAddress) == value;
    }

    internal static bool AllowsUnwind(X64UnwindProof.Index? index, IReadOnlyList<Region> regions)
    {
        if (index == null || regions.Count != 3)
            return false;
        foreach (var region in regions)
        {
            var (prolog, codes) = region.Prolog switch
            {
                Frame.Stack28 => ((byte)4, new byte[] { 4, 0x42 }),
                Frame.PushRbxStack30 => ((byte)6, new byte[] { 6, 0x52, 2, 0x30 }),
                _ => ((byte)0, Array.Empty<byte>()),
            };
            if (!index.MatchesUnwind(region.Start, region.End, prolog, 0, codes))
                return false;
        }
        return true;
    }

    private static bool Registers(Instruction i, Mnemonic mnemonic, Register destination, Register source)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op1Kind == OpKind.Register && i.Op0Register == destination && i.Op1Register == source;
    private static bool Unary(Instruction i, Mnemonic mnemonic, Register register)
        => i.Mnemonic == mnemonic && i.OpCount == 1 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == register;
    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == Register.RSP && i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           i.GetImmediate(1) == size;
    private static bool Memory(Instruction i, int operand, Register register, ulong displacement)
        => i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == register &&
           i.MemoryIndex == Register.None && i.MemoryDisplacement64 == displacement &&
           i.MemorySize.GetSize() == 8;
    private static bool Store(Instruction i, Register address, ulong displacement, Register value)
        => i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && Memory(i, 0, address, displacement) &&
           i.Op1Kind == OpKind.Register && i.Op1Register == value;
    private static bool Call(Instruction i, ulong target = 0)
        => i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0 && (target == 0 || i.NearBranchTarget == target);
    private static bool Jump(Instruction i)
        => i.Mnemonic == Mnemonic.Jmp && i.OpCount == 1 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0;
}
