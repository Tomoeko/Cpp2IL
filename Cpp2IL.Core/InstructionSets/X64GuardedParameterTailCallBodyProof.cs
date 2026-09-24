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
/// Authenticates the complete x64 body shared by narrow, metadata-specific
/// proofs of a field-receiver call with one forwarded argument register.
/// The caller still has to prove the argument ABI and the unique managed target.
/// </summary>
internal static class X64GuardedParameterTailCallBodyProof
{
    internal readonly record struct Shape(ulong ReceiverOffset, ulong TargetAddress);

    internal static Shape? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer == 0 || method.UnderlyingPointer == ulong.MaxValue)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 20 or > 48 ||
            region.Start < unwind.ImageBase || region.End - 1 - unwind.ImageBase > uint.MaxValue)
            return null;

        method.EnsureRawBytes();
        var length = checked((int)(region.End - region.Start));
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        var image = pe.GetRawBinaryContent();
        // Method slices can stop at the final instruction while the unwind
        // region includes INT3 alignment. Validate the slice and the padding
        // separately against the same file-backed executable region.
        var decodedLength = Math.Min(length, method.RawBytes.Length);
        if (decodedLength < 20 || rawStart < 0 || rawEnd < rawStart ||
            rawEnd >= image.Length || rawEnd - rawStart != length - 1 ||
            Enumerable.Range(0, length).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(region.Start + (ulong)offset - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(region.Start + (ulong)offset, false) != rawStart + offset) ||
            !image.Slice(checked((int)rawStart), decodedLength).SequenceEqual(
                method.RawBytes.AsSpan().Slice(0, decodedLength)) ||
            Enumerable.Range(1, length - 1).Any(offset =>
                app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)))
            return null;

        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        if (native.Length is < 8 or > 24 || native[0].IP != region.Start ||
            native[7].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(8).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[7].NextIP, region.End) ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }) ||
            !FieldLoad(native[1], out var receiverOffset) ||
            !Test(native[2], NativeRegister.RCX) ||
            native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
            native[3].NearBranchTarget != native[7].IP ||
            !Zero(native[4], NativeRegister.R8D) ||
            !Stack(native[5], Mnemonic.Add) ||
            native[6].Mnemonic != Mnemonic.Jmp || native[6].Op0Kind != OpKind.NearBranch64 ||
            native[7].Code != Code.Call_rel32_64 || native[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, native[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native.Take(8).ToArray(),
                new HashSet<ulong> { native[7].IP }) != null)
            return null;

        return new Shape(receiverOffset, native[6].NearBranchTarget);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool FieldLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RCX && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8 &&
               // A null owner must fault on this reference-field load.
               offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;
}
