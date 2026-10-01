using System;
using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ScalarLaneDemandProof
{
    internal readonly record struct Projection(ulong Address, Register Destination,
        Register Source, int Width, bool IsZero);

    internal sealed class Shape
    {
        private readonly Projection[] _projections;
        internal ReadOnlySpan<Projection> Projections => _projections;

        internal Shape(IEnumerable<Projection> projections) => _projections = projections.ToArray();
    }

    // Work backwards through value versions. Only the low lane may be observed,
    // and its width must agree with the original incoming ABI value. A packed
    // copy does not round, convert or perform floating arithmetic.
    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body,
        IReadOnlyDictionary<Register, int> incoming, int returnWidth,
        Func<Instruction, int, bool>? authenticateLiteral = null)
    {
        if (returnWidth is not (32 or 64) || body.Count is < 2 or > 64 ||
            body[^1] is not { Code: Code.Retnq, OpCount: 0 }) return null;
        for (var ordinal = 0; ordinal < body.Count; ordinal++)
        {
            var instruction = body[ordinal];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                ordinal > 0 && instruction.IP != body[ordinal - 1].NextIP ||
                instruction.FlowControl != (ordinal == body.Count - 1 ? FlowControl.Return : FlowControl.Next))
                return null;
        }

        var needed = new Dictionary<Register, int> { [Register.XMM0] = returnWidth };
        var projections = new List<Projection>();
        for (var ordinal = body.Count - 2; ordinal >= 0; ordinal--)
        {
            var instruction = body[ordinal];
            if (instruction.Mnemonic == Mnemonic.Nop && instruction.OpCount == 0) continue;
            if (IsPackedCopy(instruction) || IsSelfZero(instruction))
            {
                var destination = instruction.Op0Register;
                if (!needed.TryGetValue(destination, out var width)) return null;
                needed.Remove(destination);
                var zero = IsSelfZero(instruction);
                var source = zero ? Register.None : instruction.Op1Register;
                if (!zero && !Need(source, width)) return null;
                projections.Add(new(instruction.IP, destination, source, width, zero));
                continue;
            }

            var scalarWidth = ScalarWidth(instruction);
            if (scalarWidth == 0 || instruction.OpCount != 2 || instruction.Op0Kind != OpKind.Register ||
                !IsScalarRegister(instruction.Op0Register) ||
                !needed.TryGetValue(instruction.Op0Register, out var demanded) || demanded != scalarWidth)
                return null;
            // Dead arithmetic can still affect the floating environment. This
            // projection proof does not authorize dropping those operations.
            if (instruction.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd)
                needed.Remove(instruction.Op0Register);
            if (instruction.Op1Kind == OpKind.Register)
            {
                if (!IsScalarRegister(instruction.Op1Register) || !Need(instruction.Op1Register, scalarWidth))
                    return null;
            }
            else if (instruction.Op1Kind != OpKind.Memory ||
                     instruction.MemorySize.GetSize() != scalarWidth / 8 ||
                     authenticateLiteral?.Invoke(instruction, scalarWidth) != true)
                return null;
        }
        if (projections.Count == 0 || needed.Any(pair =>
                !incoming.TryGetValue(pair.Key, out var originalWidth) || originalWidth != pair.Value))
            return null;
        return new(projections.OrderBy(site => site.Address));

        bool Need(Register register, int width)
        {
            if (!IsScalarRegister(register) || needed.TryGetValue(register, out var existing) && existing != width)
                return false;
            needed[register] = width;
            return true;
        }
    }

    internal static bool IsPackedCopy(Instruction instruction) => instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        IsScalarRegister(instruction.Op0Register) && IsScalarRegister(instruction.Op1Register) &&
        instruction.Code is Code.Movaps_xmm_xmmm128 or Code.Movaps_xmmm128_xmm or
            Code.Movups_xmm_xmmm128 or Code.Movups_xmmm128_xmm or
            Code.Movapd_xmm_xmmm128 or Code.Movapd_xmmm128_xmm or
            Code.Movupd_xmm_xmmm128 or Code.Movupd_xmmm128_xmm;

    internal static bool IsSelfZero(Instruction instruction) => instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        IsScalarRegister(instruction.Op0Register) && instruction.Op0Register == instruction.Op1Register &&
        instruction.Code is Code.Xorps_xmm_xmmm128 or Code.Xorpd_xmm_xmmm128;

    private static bool IsScalarRegister(Register register) => register is >= Register.XMM0 and <= Register.XMM5;

    private static int ScalarWidth(Instruction instruction) => instruction.Code switch
    {
        Code.Movss_xmm_xmmm32 or Code.Movss_xmmm32_xmm or Code.Addss_xmm_xmmm32 or Code.Subss_xmm_xmmm32 or
            Code.Mulss_xmm_xmmm32 or Code.Divss_xmm_xmmm32 => 32,
        Code.Movsd_xmm_xmmm64 or Code.Movsd_xmmm64_xmm or Code.Addsd_xmm_xmmm64 or Code.Subsd_xmm_xmmm64 or
            Code.Mulsd_xmm_xmmm64 or Code.Divsd_xmm_xmmm64 => 64,
        _ => 0
    };
}
