using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates low32 transfers in a complete straight-line register/field body.
/// A managed result type alone never establishes a truncation or a store width.
/// </summary>
internal static class X64IntegerTruncationProof
{
    internal sealed record Site(ulong Address, NativeRegister Source, NativeRegister Destination,
        NativeRegister Receiver, int Offset);

    internal static NativeInstruction[]? FindBody(MethodAnalysisContext method)
    {
        if (!X64ArrayLengthReadProof.HasUnchangedMethod(method) || method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
            return null;
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        try
        {
            // A frameless leaf has no .pdata entry. Its first RET closes this
            // branch-free family; every consumed byte still has to agree with
            // the current executable image and authenticated unwind index.
            var prefix = X86Utils.Iterate(method).Take(64).ToArray();
            var terminal = Array.FindIndex(prefix, instruction => instruction.Code == Code.Retnq);
            if (terminal < 0)
                return null;
            var body = prefix.Take(terminal + 1).ToArray();
            var start = method.UnderlyingPointer;
            var end = body[^1].NextIP;
            if (!IsClosedBody(body) || body[0].IP != start || end <= start || end - start > 512 ||
                unwind.ClassifySpan(start, end) is not
                    { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var provedStart, End: var provedEnd } ||
                provedStart != start || provedEnd != end ||
                method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < end))
                return null;
            var length = checked((int)(end - start));
            return method.RawBytes.Length >= length &&
                   X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                       method.RawBytes.AsSpan().Slice(0, length), start) &&
                   X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, start, (uint)length)
                ? body : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static bool IsClosedBody(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is < 2 or > 64 || body[^1].Code != Code.Retnq ||
            body.Take(body.Count - 1).Any(instruction => instruction.FlowControl != FlowControl.Next))
            return false;
        var information = new InstructionInfoFactory();
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 || !NoPrefixes(instruction) ||
                information.GetInfo(instruction).GetUsedRegisters().Any(used =>
                    used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                    used.Register.GetFullRegister() is NativeRegister.RBX or NativeRegister.RBP or NativeRegister.RSI or
                        NativeRegister.RDI or NativeRegister.R12 or NativeRegister.R13 or NativeRegister.R14 or NativeRegister.R15) ||
                index != 0 && body[index - 1].NextIP != instruction.IP)
                return false;
            if (instruction.Code == Code.Retnq || instruction.Mnemonic == Mnemonic.Nop)
                continue;
            if (TryGetSite(instruction) != null)
                continue;
            if (instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 &&
                instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
                IntegerRegister(instruction.Op0Register) && IntegerRegister(instruction.Op1Register))
                continue;
            if (instruction.Code is Code.Shr_rm64_imm8 or Code.Sar_rm64_imm8 &&
                instruction.Op0Kind == OpKind.Register && IntegerRegister(instruction.Op0Register) &&
                instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 is > 0 and < 64)
                continue;
            return false;
        }
        return body.Any(instruction => TryGetSite(instruction) != null);
    }

    internal static Site? TryGetSite(NativeInstruction instruction)
    {
        if (!NoPrefixes(instruction) || instruction.CodeSize != CodeSize.Code64 ||
            instruction.Code is not (Code.Mov_r32_rm32 or Code.Mov_rm32_r32) ||
            instruction.Op1Kind != OpKind.Register || instruction.Op1Register.GetSize() != 4 ||
            !IntegerRegister(instruction.Op1Register.GetFullRegister()))
            return null;
        var source = instruction.Op1Register.GetFullRegister();
        if (instruction.Op0Kind == OpKind.Register && instruction.Op0Register.GetSize() == 4 &&
            IntegerRegister(instruction.Op0Register.GetFullRegister()))
            return new Site(instruction.IP, source, instruction.Op0Register.GetFullRegister(), NativeRegister.None, 0);
        if (instruction.Op0Kind != OpKind.Memory || instruction.MemorySize.GetSize() != 4 ||
            !IntegerRegister(instruction.MemoryBase) || instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 || instruction.MemoryDisplacement64 is < 16 or > 4096)
            return null;
        return new Site(instruction.IP, source, NativeRegister.None, instruction.MemoryBase,
            checked((int)instruction.MemoryDisplacement64));
    }

    internal static bool MatchesRegister(Cpp2IL.Core.ISIL.Register register, NativeRegister native) =>
        register.Copy() == new Cpp2IL.Core.ISIL.Register(null, native.ToString().ToLowerInvariant());

    private static bool IntegerRegister(NativeRegister register) =>
        register is >= NativeRegister.RAX and <= NativeRegister.R15 && register != NativeRegister.RSP;

    private static bool NoPrefixes(NativeInstruction instruction) =>
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
        instruction.SegmentPrefix == NativeRegister.None;
}
