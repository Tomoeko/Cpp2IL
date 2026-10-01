using System.Collections.Generic;
using System.Linq;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ScalarDoubleLeafProof
{
    internal enum Kind { FieldSum, SignedParameterScale }
    internal sealed record Shape(Kind Operation, Instruction First, Instruction Arithmetic,
        Instruction Return, Instruction? Clear);

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count is < 3 or > 5 || body[^1] is not { Code: Code.Retnq, OpCount: 0 } ||
            body.Where((site, ordinal) => site.IsInvalid || site.CodeSize != CodeSize.Code64 ||
                site.HasLockPrefix || site.HasRepPrefix || site.HasRepnePrefix || site.SegmentPrefix != Register.None ||
                site.FlowControl != (ordinal == body.Count - 1 ? FlowControl.Return : FlowControl.Next) ||
                ordinal > 0 && site.IP != body[ordinal - 1].NextIP).Any()) return null;
        var position = body[0] is { Mnemonic: Mnemonic.Nop, OpCount: 0 } ? 1 : 0;
        Instruction? clear = null;
        if (body[position] is { Code: Code.Xorps_xmm_xmmm128, OpCount: 2,
                Op0Kind: OpKind.Register, Op1Kind: OpKind.Register,
                Op0Register: Register.XMM0, Op1Register: Register.XMM0 })
            clear = body[position++];
        if (position != body.Count - 3) return null;
        var first = body[position];
        var arithmetic = body[position + 1];
        if (clear == null && first.Code == Code.Movsd_xmm_xmmm64 &&
            arithmetic.Code == Code.Addsd_xmm_xmmm64 && ReceiverRead(first) && ReceiverRead(arithmetic))
            return new(Kind.FieldSum, first, arithmetic, body[^1], null);
        if (first is { Code: Code.Cvtsi2sd_xmm_rm64, OpCount: 2,
                Op0Kind: OpKind.Register, Op0Register: Register.XMM0,
                Op1Kind: OpKind.Register, Op1Register: Register.RCX } &&
            arithmetic is { Code: Code.Mulsd_xmm_xmmm64, OpCount: 2,
                Op0Kind: OpKind.Register, Op0Register: Register.XMM0, Op1Kind: OpKind.Memory } &&
            arithmetic.IsIPRelativeMemoryOperand && arithmetic.MemoryIndex == Register.None &&
            arithmetic.MemorySize.GetSize() == sizeof(double))
            return new(Kind.SignedParameterScale, first, arithmetic, body[^1], clear);
        return null;

        static bool ReceiverRead(Instruction site) => site.OpCount == 2 && site.Op0Kind == OpKind.Register &&
            site.Op0Register == Register.XMM0 && site.Op1Kind == OpKind.Memory &&
            site.MemoryBase == Register.RCX && site.MemoryIndex == Register.None && site.MemoryIndexScale == 1 &&
            site.MemorySize.GetSize() == sizeof(double) &&
            site.MemoryDisplacement64 is >= 16 and <= int.MaxValue && site.MemoryDisplacement64 % sizeof(double) == 0;
    }

    // Signed Int64 converts to a finite binary64 value of magnitude at most 2^63.
    // A normal coefficient in this exponent range keeps every nonzero result
    // normal and finite, including the largest rounded integer. The FP control
    // and status contract remains the existing default-rounding arithmetic one.
    internal static bool HasFiniteNormalScaleDomain(ulong bits) =>
        ((bits >> 52) & 0x7FF) is >= 1 and <= 1983;
}
