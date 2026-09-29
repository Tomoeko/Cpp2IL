using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using Instruction = Iced.Intel.Instruction;
using Register = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identifies the exact target's codegen raise wrapper: clear trace_ips and
/// stack_trace, then forward the original exception and managed frame to the
/// exported Exception::Raise implementation. This does not prove construction,
/// formatting, ownership cleanup or the caller's exceptional control flow.
/// </summary>
internal static class X64CodegenRaiseExceptionProof
{
    internal enum Frame { Leaf, Stack28, SavedRbxRdiStack20 }
    internal readonly record struct Region(ulong Start, ulong End, Frame Prolog);

    internal static bool TryIdentify(ApplicationAnalysisContext app, ulong target)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind)
            return false;
        try
        {
            return HasTraceLayout(app) && TryProve(target,
                (address, count) => X64NativeInstructionReader.Read(pe, unwind, address, count, count * 15),
                pe.GetVirtualAddressOfExportedFunctionByName,
                regions => AllowsUnwind(unwind, regions) && regions.All(region =>
                    X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, region.Start,
                        checked((uint)(region.End - region.Start)))));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return false;
        }
    }

    internal static bool HasTraceLayout(ApplicationAnalysisContext app)
    {
        // Corlib identity and the unchanged Exception declaration are independent
        // of the native candidate. No constructor is invoked by this operation.
        if (X86RuntimeNullThrowProof.BindIdentity(app, "Exception")?.DeclaringType is not
                { } type || !ReferenceEquals(type, app.SystemTypes.SystemExceptionType))
            return false;
        // Il2CppException names these native slots trace_ips and stack_trace.
        // This corlib profile declares their managed storage as an Object
        // _stackTrace and String _stackTraceString at the same exact offsets.
        var ips = type.Fields.Where(field => field.Name == "_stackTrace").ToArray();
        var trace = type.Fields.Where(field => field.Name == "_stackTraceString").ToArray();
        if (ips is not [var ipField] || trace is not [var traceField] ||
            !ReferenceEquals(ipField.FieldType, app.SystemTypes.SystemObjectType) ||
            !ReferenceEquals(traceField.FieldType, app.SystemTypes.SystemStringType))
            return false;
        return Field(ipField, 0x38) && Field(traceField, 0x40);

        bool Field(FieldAnalysisContext field, int offset)
            => ReferenceEquals(field.DeclaringType, type) && field.Name == field.DefaultName &&
               field.Offset == offset && NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                   new FieldReference(field, new LocalVariable("runtime-exception",
                       new ISIL.Register(null, "rcx"), type), offset));
    }

    internal static bool TryProve(ulong target,
        Func<ulong, int, IReadOnlyList<Instruction>?> read,
        Func<string, ulong> export, Func<IReadOnlyList<Region>, bool> allowsUnwind)
    {
        // Exception::Raise's no-return ABI comes from the matching exported API.
        // The export must preserve RCX and supply lastManagedFrame=null.
        var api = Read(export("il2cpp_raise_exception"), 3);
        if (api == null || api[0].Length != 4 || !Stack(api[0], 0x28) ||
            !Registers(api[1], Mnemonic.Xor, Register.EDX, Register.EDX) || !Call(api[2]))
            return false;
        var raise = api[2].NearBranchTarget;
        var body = Read(target, 12);
        if (body == null || target == api[0].IP || target == raise ||
            body[0].Length != 5 || body[1].Length != 1 || body[2].Length != 4 ||
            !Store(body[0], Register.RSP, 8, Register.RBX) ||
            !Unary(body[1], Mnemonic.Push, Register.RDI) || !Stack(body[2], 0x20) ||
            !Registers(body[3], Mnemonic.Mov, Register.RDI, Register.RCX) ||
            !Registers(body[4], Mnemonic.Mov, Register.RBX, Register.RDX) ||
            !Address(body[5], Mnemonic.Add, Register.RCX, 0x38) || !Call(body[6]) ||
            !LoadAddress(body[7], Register.RCX, Register.RDI, 0x40) ||
            !Call(body[8], body[6].NearBranchTarget) ||
            !Registers(body[9], Mnemonic.Mov, Register.RDX, Register.RBX) ||
            !Registers(body[10], Mnemonic.Mov, Register.RCX, Register.RDI) ||
            !Call(body[11], raise))
            return false;
        var clearAddress = body[6].NearBranchTarget;
        if (new[] { target, api[0].IP, raise, clearAddress }.Distinct().Count() != 4)
            return false;
        var clear = Read(clearAddress, 2);
        if (clear == null || clear[0].Mnemonic != Mnemonic.Mov || clear[0].OpCount != 2 ||
            !Memory(clear[0], 0, Register.RCX, 0) ||
            clear[0].Op1Kind != OpKind.Immediate32to64 || clear[0].GetImmediate(1) != 0 ||
            clear[1].Code != Code.Retnq || clear[1].OpCount != 0)
            return false;
        return allowsUnwind([
            new(body[0].IP, body[^1].NextIP, Frame.SavedRbxRdiStack20),
            new(clear[0].IP, clear[^1].NextIP, Frame.Leaf),
            new(api[0].IP, api[^1].NextIP, Frame.Stack28)
        ]);

        IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            if (address == 0 || read(address, count) is not { } code || code.Count != count)
                return null;
            var next = address;
            foreach (var instruction in code)
            {
                if (instruction.IP != next || instruction.IsInvalid || instruction.Length == 0 ||
                    instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                    instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != Register.None)
                    return null;
                next = instruction.NextIP;
            }
            return code;
        }
    }

    internal static bool AllowsUnwind(X64UnwindProof.Index index, IReadOnlyList<Region> regions)
    {
        if (regions.Count != 3)
            return false;
        foreach (var region in regions)
        {
            if (region.Prolog == Frame.Leaf)
            {
                if (index.ClassifySpan(region.Start, region.End).Kind != X64UnwindProof.SpanKind.NoEntry)
                    return false;
                continue;
            }
            var (prolog, codes) = region.Prolog switch
            {
                Frame.Stack28 => ((byte)4, new byte[] { 4, 0x42 }),
                Frame.SavedRbxRdiStack20 => ((byte)10,
                    new byte[] { 10, 0x34, 6, 0, 10, 0x32, 6, 0x70 }),
                _ => ((byte)0, Array.Empty<byte>())
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
    private static bool Address(Instruction i, Mnemonic mnemonic, Register register, ulong value)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == register && i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           i.GetImmediate(1) == value;
    private static bool Stack(Instruction i, ulong size) => Address(i, Mnemonic.Sub, Register.RSP, size);
    private static bool Memory(Instruction i, int operand, Register register, ulong displacement)
        => i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == register &&
           i.MemoryIndex == Register.None && i.MemoryDisplacement64 == displacement &&
           i.MemorySize.GetSize() == 8;
    private static bool Store(Instruction i, Register address, ulong displacement, Register value)
        => i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && Memory(i, 0, address, displacement) &&
           i.Op1Kind == OpKind.Register && i.Op1Register == value;
    private static bool LoadAddress(Instruction i, Register destination, Register address, ulong offset)
        => i.Mnemonic == Mnemonic.Lea && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == destination && i.Op1Kind == OpKind.Memory &&
           i.MemoryBase == address && i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;
    private static bool Call(Instruction i, ulong target = 0)
        => i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0 && (target == 0 || i.NearBranchTarget == target);
}
