using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using FieldReference = Cpp2IL.Core.ISIL.FieldReference;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds two ordered argument null tests and their later 32-bit field loads to one
/// target-runtime null helper. This authenticates the reads used by a null-arm probe;
/// field layout alone cannot establish that the probe belongs to the native method.
/// </summary>
internal static class X64SequentialNullFieldProof
{
    internal static bool Matches(MethodAnalysisContext method, FieldReference first, FieldReference second,
        RuntimeNullThrowEvidence helper)
    {
        try
        {
            if (!helper.IsValidFor(method.AppContext) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                method.UnderlyingPointer == 0 || first.Offset < 0 || second.Offset < 0)
                return false;

            method.EnsureRawBytes();
            if (method.AppContext.Binary is not PE pe || method.RawBytes.Length == 0 ||
                X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
                return false;
            var end = checked(method.UnderlyingPointer + (ulong)method.RawBytes.Length);
            var region = unwind.ClassifySpan(method.UnderlyingPointer, end);
            if (region is not { Kind: X64UnwindProof.SpanKind.HandlerFree } ||
                region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
                region.End < end || region.End - end > 15 ||
                unwind.ClassifySpan(method.UnderlyingPointer, region.End).Kind !=
                X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(method.UnderlyingPointer, region.End, 4, 0,
                    new byte[] { 4, 0x42 }) ||
                !X64NativePaddingProof.HasInt3Padding(pe, end, region.End))
                return false;
            // ClassifySpan also requires this entire range in one executable,
            // file-backed section. Corroborate contiguous raw bytes for the body.
            var rawStart = pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false);
            var rawEnd = pe.MapVirtualAddressToRaw(end - 1, false);
            if (rawStart < 0 || rawEnd < rawStart ||
                (ulong)(rawEnd - rawStart) != end - method.UnderlyingPointer - 1 ||
                rawEnd >= pe.GetRawBinaryContent().Length)
                return false;

            var native = X86Utils.Iterate(method).ToArray();
            if (!MatchesShape(native, method.UnderlyingPointer, end,
                    first.Offset, second.Offset, helper.NativeTarget) ||
                X86CallerExceptionRegionProof.Check(method, native,
                    new HashSet<ulong> { native[^1].IP }) != null)
                return false;

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    // This pure structural predicate permits mutation tests on decoded instructions
    // without writing altered binaries or relying on symbols as recovery evidence.
    internal static bool MatchesShape(IReadOnlyList<Instruction> native, ulong entry, ulong end,
        int firstOffset, int secondOffset, ulong helperTarget)
    {
        if (native.Count != 18 || native[0].IP != entry || native[^1].NextIP != end ||
            firstOffset < 0 || secondOffset < 0 ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            !native.Select(instruction => instruction.Length).SequenceEqual(
                new[] { 4, 3, 2, 3, 2, 4, 3, 3, 2, 2, 3, 3, 4, 1, 5, 4, 1, 5 }) ||
            native.Zip(native.Skip(1), (first, second) => first.NextIP != second.IP).Any(gap => gap))
            return false;

        var terminal = native[^1];
        return StackProlog(native[0]) && SelfTest(native[1], Register.RCX) &&
               NullBranch(native[2], terminal.IP) && SelfTest(native[3], Register.RDX) &&
               NullBranch(native[4], terminal.IP) &&
               FieldLoad(native[5], Register.R8D, Register.RDX, secondOffset) &&
               FieldLoad(native[6], Register.ECX, Register.RCX, firstOffset) &&
               Compare(native[7]) && native[8].Code == Code.Jl_rel8_64 &&
               native[8].NearBranchTarget == native[14].IP &&
               ZeroEax(native[9]) && Compare(native[10]) && SetGreater(native[11]) &&
               StackEpilog(native[12]) && native[13].Code == Code.Retnq &&
               native[14].Code == Code.Mov_r32_imm32 &&
               native[14].Op0Kind == OpKind.Register && native[14].Op0Register == Register.EAX &&
               native[14].Op1Kind == OpKind.Immediate32 &&
               native[14].Immediate32 == unchecked((uint)-1) &&
               StackEpilog(native[15]) && native[16].Code == Code.Retnq &&
               terminal.Code == Code.Call_rel32_64 && terminal.Op0Kind == OpKind.NearBranch64 &&
               terminal.NearBranchTarget == helperTarget;
    }

    private static bool StackProlog(Instruction instruction) =>
        instruction.Code == Code.Sub_rm64_imm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x28;

    private static bool StackEpilog(Instruction instruction) =>
        instruction.Code == Code.Add_rm64_imm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x28;

    private static bool SelfTest(Instruction instruction, Register register) =>
        instruction.Code == Code.Test_rm64_r64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == register &&
        instruction.Op1Register == register;

    private static bool NullBranch(Instruction instruction, ulong target) =>
        instruction.Code == Code.Je_rel8_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool FieldLoad(Instruction instruction, Register destination, Register receiver, int offset) =>
        instruction.Code == Code.Mov_r32_rm32 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == receiver && instruction.MemoryIndex == Register.None &&
        instruction.MemoryDisplacement64 == (ulong)offset && instruction.MemorySize.GetSize() == 4;

    private static bool Compare(Instruction instruction) =>
        instruction.Code == Code.Cmp_r32_rm32 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == Register.ECX &&
        instruction.Op1Register == Register.R8D;

    private static bool ZeroEax(Instruction instruction) =>
        instruction.Code == Code.Xor_r32_rm32 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == Register.EAX &&
        instruction.Op1Register == Register.EAX;

    private static bool SetGreater(Instruction instruction) =>
        instruction.Code == Code.Setg_rm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.AL;
}
