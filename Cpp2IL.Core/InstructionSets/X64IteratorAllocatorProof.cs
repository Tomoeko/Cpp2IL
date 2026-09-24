using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identifies the codegen allocator by the installed il2cpp_object_new export's
/// internal Object::New call. The exported wrapper catches exceptions, so only
/// its internal target (through a transparent codegen JMP) is admissible.
/// </summary>
internal static class X64IteratorAllocatorProof
{
    internal static bool IsAllocator(ApplicationAnalysisContext app, ulong candidate)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            candidate == 0)
            return false;

        var export = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_object_new");
        var wrapper = Read(pe, export, 6);
        var stub = Read(pe, candidate, 1);
        if (wrapper == null || stub == null || !TryProveShape(wrapper, stub) ||
            unwind.GetHandler(export) is not { Start: var exportStart, End: var exportEnd } ||
            exportStart != export || exportEnd < wrapper[^1].NextIP ||
            !X64NativePaddingProof.HasInt3Padding(pe, wrapper[^1].NextIP, exportEnd))
            return false;
        var transfer = stub[0];
        if (transfer.NextIP - candidate != 5 ||
            unwind.ClassifySpan(candidate, transfer.NextIP) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry } ||
            !X64NativePaddingProof.HasInt3Padding(pe, transfer.NextIP, candidate + 16))
            return false;
        return true;
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> wrapper,
        IReadOnlyList<NativeInstruction> stub)
    {
        if (wrapper is not [var frame, var call, var jump, var catchReturn,
                var restore, var ret] ||
            stub is not [{ Code: Code.Jmp_rel32_64 } transfer])
            return false;
        return Stack(frame, Mnemonic.Sub, 0x28) && Call(call) &&
               jump.Code is Code.Jmp_rel8_64 or Code.Jmp_rel32_64 &&
               jump.NearBranchTarget == restore.IP &&
               catchReturn.Mnemonic == Mnemonic.Xor &&
               catchReturn.Op0Kind == OpKind.Register &&
               catchReturn.Op1Kind == OpKind.Register &&
               catchReturn.Op0Register == NativeRegister.EAX &&
               catchReturn.Op1Register == NativeRegister.EAX &&
               Stack(restore, Mnemonic.Add, 0x28) && ret.Code == Code.Retnq &&
               ret.OpCount == 0 && transfer.NearBranchTarget == call.NearBranchTarget;
    }

    private static IReadOnlyList<NativeInstruction>? Read(PE pe, ulong address, int count)
    {
        var start = pe.GetVirtualAddressOfPrimaryExecutableSection();
        var code = pe.GetEntirePrimaryExecutableSection();
        if (address < start || address - start >= (ulong)code.Length)
            return null;
        var offset = checked((int)(address - start));
        var bytes = code.Slice(offset, Math.Min(count * 15, code.Length - offset)).ToArray();
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var result = new NativeInstruction[count];
        var next = address;
        for (var index = 0; index < count; index++)
        {
            var instruction = decoder.Decode();
            if (instruction.IP != next || instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None ||
                !FileBacked(pe, instruction.IP, instruction.NextIP))
                return null;
            result[index] = instruction;
            next = instruction.NextIP;
        }
        return result;
    }

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > 15)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong value) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == value;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.NearBranchTarget != 0;
}
