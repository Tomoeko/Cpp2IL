using System;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Reads a closed, handler-free x64 region with a 40-byte stack allocation.
/// Only INT3 padding may follow the requested instructions within the region.
/// The caller must prove the instructions, signature, helpers and effects.
/// </summary>
internal static class X64Stack28BodyProof
{
    internal static Instruction[]? Read(MethodAnalysisContext method,
        int instructionCount, int maximumBytes)
    {
        if (instructionCount <= 0 || maximumBytes <= 0 ||
            method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;

        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End <= start || region.End - start > (ulong)maximumBytes ||
            !unwind.MatchesUnwind(start, region.End, 4, 0, [4, 0x42]) ||
            method.AppContext.MethodsByAddress.Keys.Any(address =>
                address > start && address < region.End))
            return null;

        var native = X86Utils.Iterate(method)
            .TakeWhile(instruction => instruction.IP < region.End).ToArray();
        if (native.Length < instructionCount ||
            native.Length > instructionCount + 32 || native[0].IP != start ||
            native[^1].NextIP > region.End ||
            native.Skip(instructionCount).Any(instruction => instruction.Code != Code.Int3) ||
            native.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4)
            return null;

        var body = native.Take(instructionCount).ToArray();
        var length = checked((int)(region.End - start));
        var bodyLength = checked((int)(body[^1].NextIP - start));
        if (length - bodyLength > 32 || method.RawBytes.Length < bodyLength ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan().Slice(0, bodyLength), start))
            return null;

        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(region.End - 1, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - length || last - first != length - 1)
            return null;
        for (var offset = 0; offset < length; offset++)
        {
            var address = start + (ulong)offset;
            if (!unwind.IsExecutableRva(checked((uint)(address - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(address, false) != first + offset ||
                offset >= bodyLength && image[(int)first + offset] != 0xCC)
                return null;
        }
        return body;
    }

    internal static bool Stack(Instruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;
}
