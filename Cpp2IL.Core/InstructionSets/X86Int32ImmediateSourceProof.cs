using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates a register's sole SSA definition as a native MOV r32, imm32.
/// Its raw unsigned immediate is the same 32-bit value as the signed Int32
/// operand, but only a separately typed arithmetic use supplies signedness.
/// </summary>
internal static class X86Int32ImmediateSourceProof
{
    internal static bool IsExclusiveArithmeticSource(MethodAnalysisContext method,
        LocalVariable local)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } pe ||
            pe.InstructionSetId != DefaultInstructionSets.X86_64 ||
            local.Type != null || local.Register.Version < 0 ||
            method.ParameterLocals.Contains(local) || method.ControlFlowGraph == null)
            return false;

        ISIL.Instruction? definition = null;
        var uses = 0;
        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (ReferenceEquals(instruction.Destination, local))
            {
                if (definition != null)
                    return false;
                definition = instruction;
                continue;
            }

            if (instruction.Operands.Any(operand => Uses(operand, local)))
            {
                if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply) ||
                    instruction.IntegerBitWidth != 32 ||
                    !instruction.Operands.Skip(1).Any(operand => ReferenceEquals(operand, local)))
                    return false;
                uses++;
            }
        }
        if (uses == 0 || definition is not { OpCode: OpCode.Move,
                IntegerBitWidth: 0, NativeAddress: { } address,
                Operands: [LocalVariable, Immediate { Value: >= 0 and <= uint.MaxValue } value] } ||
            address < method.UnderlyingPointer ||
            address - method.UnderlyingPointer >= (ulong)method.RawBytes.Length ||
            !pe.TryMapVirtualAddressToRaw(address, out var mappedRaw))
            return false;

        var offset = checked((int)(address - method.UnderlyingPointer));
        var image = pe.GetRawBinaryContent();
        if (mappedRaw < 0 || mappedRaw >= image.Length)
            return false;
        var byteCount = Math.Min(15, Math.Min(method.RawBytes.Length - offset,
            image.Length - checked((int)mappedRaw)));
        var bytes = method.RawBytes.AsSpan().Slice(offset, byteCount).ToArray();
        var native = Decoder.Create(64, new ByteArrayCodeReader(bytes), address).Decode();
        return !native.IsInvalid && native.CodeSize == CodeSize.Code64 &&
               native.Length > 0 && native.Length <= byteCount &&
               native.NextIP > address &&
               pe.TryMapVirtualAddressToRaw(native.NextIP - 1, out var lastRaw) &&
               lastRaw == mappedRaw + native.Length - 1 &&
               image.Slice(checked((int)mappedRaw), native.Length).SequenceEqual(
                   bytes.AsSpan(0, native.Length)) &&
               native.Mnemonic == Mnemonic.Mov && native.Code == Code.Mov_r32_imm32 &&
               native.Op0Kind == OpKind.Register && native.Op1Kind == OpKind.Immediate32 &&
               native.Op0Register.GetSize() == 4 &&
               X86Utils.GetRegisterName(native.Op0Register) == local.Register.Name &&
               native.GetImmediate(1) == unchecked((uint)value.Value);
    }

    private static bool Uses(IOperand operand, LocalVariable local) =>
        ReferenceEquals(operand, local) ||
        operand is ISIL.MemoryOperand memory &&
            (ReferenceEquals(memory.Base, local) || ReferenceEquals(memory.Index, local)) ||
        operand is AddressOf address && ReferenceEquals(address.Target, local);
}
