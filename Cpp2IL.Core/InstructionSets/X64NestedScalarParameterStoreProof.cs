using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves an ordinary instance's sole scalar argument stored through one
/// captured reference field. The nested-reference null failure precedes every
/// store; no arithmetic, metadata initialization or reference write occurs.
/// </summary>
internal static partial class X64NestedScalarParameterStoreProof
{
    internal sealed record Shape(int SourceOffset, int ValueOffset, int Width,
        bool Floating, ulong NullHelper);

    internal sealed record Evidence(FieldAnalysisContext SourceField,
        FieldAnalysisContext ValueField, Shape Native,
        X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Evidence other) => Native == other.Native &&
            ReferenceEquals(SourceField, other.SourceField) && ReferenceEquals(ValueField, other.ValueField) &&
            Input.Matches(other.Input);
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                X64Stack28BodyProof.Read(method, 8, 96) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { body[7].IP }) != null ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings.Count is < 1 or > 128 || bindings.Distinct().Count() != bindings.Count ||
                bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1)
                return null;

            var snapshots = new List<object>();
            BoundFields? selected = null;
            // Count identities before eligibility. An ineligible folded identity
            // cannot silently disappear from the original native target scope.
            snapshots.Add(bindings.Count);
            foreach (var binding in bindings)
            {
                if (binding.UnderlyingPointer != method.UnderlyingPointer ||
                    BindMetadata(binding, shape) is not { } fields)
                    return null;
                if (ReferenceEquals(binding, method)) selected = fields;
                CaptureBinding(binding, fields, snapshots);
            }
            if (selected == null) return null;
            var length = checked((int)(body[7].NextIP - method.UnderlyingPointer));
            return new Evidence(selected.Source, selected.Value, shape,
                new X64SmallAggregateFieldGetterProof.InputState(snapshots,
                    method.RawBytes.AsSpan().Slice(0, length).ToArray()));
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count != 8 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Sub_rm64_imm8 || !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Mov_r64_rm64 || body[1].Op0Kind != OpKind.Register || body[1].Op0Register != Register.RAX ||
            !Memory(body[1], 1, Register.RCX, 8) ||
            body[2].Code != Code.Test_rm64_r64 || body[2].OpCount != 2 ||
            body[2].Op0Kind != OpKind.Register || body[2].Op0Register != Register.RAX ||
            body[2].Op1Kind != OpKind.Register || body[2].Op1Register != Register.RAX ||
            body[3].Code is not (Code.Je_rel8_64 or Code.Je_rel32_64) ||
            body[3].OpCount != 1 || body[3].Op0Kind != OpKind.NearBranch64 || body[3].NearBranchTarget != body[7].IP ||
            body[5].Code != Code.Add_rm64_imm8 || !X64Stack28BodyProof.Stack(body[5], Mnemonic.Add) ||
            body[6].Code != Code.Retnq || body[6].OpCount != 0 ||
            body[7].Code != Code.Call_rel32_64 || body[7].OpCount != 1 ||
            body[7].Op0Kind != OpKind.NearBranch64 || body[7].NearBranchTarget == 0)
            return null;
        var store = body[4];
        var (width, register, floating) = store.Code switch
        {
            Code.Mov_rm8_r8 => (8, Register.DL, false),
            Code.Mov_rm32_r32 => (32, Register.EDX, false),
            Code.Movss_xmmm32_xmm => (32, Register.XMM1, true),
            _ => (0, Register.None, false)
        };
        if (width == 0 || !Memory(store, 0, Register.RAX, width / 8) ||
            store.Op1Kind != OpKind.Register || store.Op1Register != register)
            return null;
        return new Shape((int)body[1].MemoryDisplacement64, (int)store.MemoryDisplacement64,
            width, floating, body[7].NearBranchTarget);
    }

    private static bool Memory(Instruction instruction, int operand, Register owner, int bytes) =>
        instruction.OpCount == 2 && instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == owner && instruction.MemoryIndex == Register.None &&
        instruction.MemoryIndexScale == 1 && instruction.MemorySize.GetSize() == bytes &&
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue;
}
