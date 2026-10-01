using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

// Identifies a complete native effect sequence only. Definition identity,
// original field binding, aliases and managed memory semantics are separate.
internal static class X64GenericReferenceLeafShape
{
    internal sealed record Shape(ulong Start, ulong End, int CounterOffset);

    internal static Shape? TryDecode(byte[] bytes, ulong address)
    {
        if (bytes.Length is < 7 or > 16 || address == 0 ||
            address > ulong.MaxValue - (ulong)bytes.Length)
            return null;
        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(64, reader);
        decoder.IP = address;
        var instructions = new List<Instruction>();
        while (reader.CanReadByte)
        {
            decoder.Decode(out var instruction);
            instructions.Add(instruction);
        }
        return TryProveShape(instructions);
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count != 3 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            body.Where((instruction, ordinal) => ordinal > 0 &&
                instruction.IP != body[ordinal - 1].NextIP).Any())
            return null;
        var increment = body[0];
        var copy = body[1];
        var ret = body[2];
        if (increment.Code != Code.Inc_rm32 || increment.OpCount != 1 ||
            increment.Op0Kind != OpKind.Memory ||
            increment.MemoryBase != Register.RCX ||
            increment.MemoryIndex != Register.None ||
            increment.MemoryIndexScale != 1 ||
            increment.MemorySize.GetSize() != sizeof(int) ||
            increment.MemorySegment != Register.DS ||
            increment.MemoryDisplacement64 is < 16 or > 0x1000 - sizeof(int) ||
            increment.MemoryDisplacement64 % sizeof(int) != 0 ||
            copy.Code is not (Code.Mov_r64_rm64 or Code.Mov_rm64_r64) || copy.OpCount != 2 ||
            copy.Op0Kind != OpKind.Register || copy.Op0Register != Register.RAX ||
            copy.Op1Kind != OpKind.Register || copy.Op1Register != Register.RDX ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new(increment.IP, ret.NextIP, checked((int)increment.MemoryDisplacement64));
    }
}
