using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a frameless instance leaf that increments one Int32 field before
/// storing its sole class-reference argument and tail-transferring to the
/// installed GC card marker. The intervening INT3 bytes and next unwind root
/// close the native leaf without treating neighboring code as method effects.
/// </summary>
internal static class X64OwnerIncrementReferenceStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext CounterField,
        FieldAnalysisContext ReferenceField);

    internal sealed record Shape(int CounterOffset, int ReferenceOffset,
        ulong BarrierTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings is not [var bound] ||
                !ReferenceEquals(bound, method) ||
                method.DeclaringType is not { } owner ||
                !X64OwnerArrayArgumentTailProof.OrdinaryClass(owner) ||
                X64NativeInstructionReader.Read(pe, unwind,
                    method.UnderlyingPointer, 4, 32) is not { } body ||
                TryProveShape(body) is not { } shape ||
                !ClosedLeaf(method, body, pe, unwind) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind,
                    shape.BarrierTarget))
                return null;
            return Bind(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool ClosedLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body, PE pe,
        X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        var end = body[^1].NextIP;
        var next = method.AppContext.MethodsByAddress.Keys
            .Where(address => address > start && address <= start + 32)
            .DefaultIfEmpty(0UL).Min();
        if (body[0].IP != start || end <= start || end > start + 31 ||
            next <= end || next - end > 15 ||
            unwind.ClassifySpan(start, next).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
            !unwind.HasFunctionEntryAt(next, next + 1) ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, next))
            return false;

        method.EnsureRawBytes();
        var bodyLength = checked((int)(end - start));
        if (method.RawBytes.Length < bodyLength)
            return false;
        var image = pe.GetRawBinaryContent();
        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        if (rawStart < 0 || rawStart > image.Length - bodyLength ||
            !method.RawBytes.AsSpan().Slice(0, bodyLength)
                .SequenceEqual(image.Slice((int)rawStart, bodyLength)))
            return false;
        var closedLength = checked((int)(next - start));
        return rawStart <= image.Length - closedLength &&
               X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                   unwind, image.Slice((int)rawStart, closedLength), start);
    }

    private static Evidence? Bind(MethodAnalysisContext method, Shape shape)
    {
        var owner = method.DeclaringType!;
        var valueFields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.ReferenceOffset).ToArray();
        if (valueFields is not [{ } valueField] ||
            valueField.Name != valueField.DefaultName ||
            valueField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawValue ||
            !X64OwnerArrayArgumentTailProof.OrdinaryClass(valueField.FieldType) ||
            !ReferenceEquals(rawValue.AsClass(), valueField.FieldType.Definition) ||
            !X64OwnerArrayArgumentTailProof.OrdinaryTarget(method, owner,
                valueField.FieldType) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(valueField,
                    new LocalVariable("proved-owner",
                        new ManagedRegister(null, "rcx"), owner),
                    shape.ReferenceOffset)))
            return null;

        var counterFields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.CounterOffset).ToArray();
        if (counterFields is not [{ } counterField] ||
            counterField.Name != counterField.DefaultName ||
            counterField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(counterField.FieldType,
                method.AppContext.SystemTypes.SystemInt32Type) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                new FieldReference(counterField,
                    new LocalVariable("proved-owner",
                        new ManagedRegister(null, "rcx"), owner),
                    shape.CounterOffset), 32))
            return null;
        return new Evidence(counterField, valueField);
    }

    internal static Shape? TryProveShape(
        IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 4 ||
            body[0].Code != Code.Inc_rm32 ||
            !Memory(body[0], 0, NativeRegister.RCX, 4,
                out var counterOffset) ||
            body[1].Code is not (Code.Add_rm64_imm8 or Code.Add_rm64_imm32) ||
            body[1].Op0Kind != OpKind.Register ||
            body[1].Op0Register != NativeRegister.RCX ||
            body[1].Op1Kind is not (OpKind.Immediate8to64 or
                OpKind.Immediate32to64) ||
            body[1].GetImmediate(1) is < 16 or > 0x1000 - 8 ||
            body[2].Code != Code.Mov_rm64_r64 ||
            !Memory(body[2], 0, NativeRegister.RCX, 8, out var zero) ||
            zero != 0 ||
            body[2].Op1Kind != OpKind.Register ||
            body[2].Op1Register != NativeRegister.RDX ||
            body[3].Code != Code.Jmp_rel32_64 ||
            body[3].Op0Kind != OpKind.NearBranch64 ||
            body[3].NearBranchTarget == 0 ||
            counterOffset is < 16 or > 0x1000 - 4 ||
            counterOffset % 4 != 0)
            return null;
        var referenceOffset = (int)body[1].GetImmediate(1);
        if (referenceOffset % 8 != 0)
            return null;
        return new Shape(counterOffset, referenceOffset,
            body[3].NearBranchTarget);
    }

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister baseRegister, int width, out int offset)
    {
        offset = 0;
        if (instruction.GetOpKind(operand) != OpKind.Memory ||
            instruction.MemoryBase != baseRegister ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != width ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }
}
