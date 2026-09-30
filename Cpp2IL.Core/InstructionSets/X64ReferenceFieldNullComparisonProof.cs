using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedOpCode = Cpp2IL.Core.ISIL.OpCode;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A complete CMP/SETcc/RET leaf compares an unchanged instance reference field
/// with zero. Unsigned greater-than-zero is nonnull and unsigned less-than-or-equal-zero
/// is null. This proof does not turn references into integers.
/// </summary>
internal static class X64ReferenceFieldNullComparisonProof
{
    internal readonly record struct Evidence(LocalVariable Value, bool IsNull);

    internal static Evidence? TryIdentify(MethodAnalysisContext method, ManagedInstruction comparison)
    {
        var app = method.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } pe ||
            pe.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.IsStatic || method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.Definition is not { RawReturnType: {
                    Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [] } ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(method.DefaultReturnType, app.SystemTypes.SystemBooleanType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.RawBytes.Length is < 9 or > 12 ||
            method.ControlFlowGraph is not { } graph ||
            comparison is not { IntegerBitWidth: 64, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable { Type: { } resultType }, LocalVariable value,
                    Immediate { Value: 0 }] } ||
            !ReferenceEquals(resultType, app.SystemTypes.SystemBooleanType) ||
            !IsNullPredicate(comparison.OpCode, out var isNull) ||
            value.Type is not { } valueType || value.IsThis || value.IsMethodInfo || value.IsReturn ||
            !IsOrdinaryReference(valueType) || !graph.Instructions.Contains(comparison))
            return null;

        var definitions = graph.Instructions.Where(instruction =>
            ReferenceEquals(instruction.Destination, value)).ToArray();
        if (definitions is not [{ OpCode: ManagedOpCode.Move, IntegerBitWidth: 0,
                Operands: [LocalVariable destination, FieldReference field] } definition] ||
            !ReferenceEquals(destination, value) ||
            !ReferenceEquals(field.Field.FieldType, valueType) ||
            !ReferenceEquals(field.Field.FieldType, field.Field.DefaultFieldType) ||
            field.Field.Attributes != field.Field.DefaultAttributes ||
            field.Field.IsStatic || field.Field.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } ||
            // Exact Windows player observations also cover reference slots at
            // offsets 72 and 88, including null-receiver faults. Other wider
            // offsets remain unproved.
            !IsProvedNullReceiverOffset(field.Offset, pe.PointerSizeBytes) ||
            field.Offset != field.Field.Offset ||
            field.Offset != field.Field.DefaultOffset ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(field) ||
            !field.Local.IsThis || !method.ParameterLocals.Contains(field.Local) ||
            field.Local.Type is not { } receiverType ||
            !NullCheckedCall.HasUnchangedReferenceBase(receiverType, field.Field.DeclaringType) ||
            graph.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, field.Local)) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Contains(field.Local) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Contains(value))
            return null;

        var reads = graph.Instructions.Where(instruction =>
            OperandEffects.ReadLocals(instruction).Any(local => ReferenceEquals(local, value))).ToArray();
        if (reads is not [var onlyRead] || !ReferenceEquals(onlyRead, comparison) ||
            OperandEffects.ReadLocals(comparison).Count(local => ReferenceEquals(local, value)) != 1 ||
            !graph.Blocks.Any(block => block.Instructions.Contains(definition) &&
                block.Instructions.Contains(comparison) &&
                block.Instructions.IndexOf(definition) < block.Instructions.IndexOf(comparison)) ||
            definition.NativeAddress is not { } compareIP ||
            comparison.NativeAddress is not { } predicateIP ||
            compareIP < method.UnderlyingPointer || predicateIP < method.UnderlyingPointer)
            return null;

        var end = method.UnderlyingPointer + (ulong)method.RawBytes.Length;
        if (end < method.UnderlyingPointer || compareIP >= end || predicateIP >= end ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
            Enumerable.Range(1, method.RawBytes.Length - 1).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return null;
        var body = X86Utils.Disassemble(method.RawBytes.AsSpan(), method.UnderlyingPointer, false).ToArray();
        if (body.Length == 0 || body[^1].NextIP != end ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64) ||
            !IsFileBacked(pe, method) ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;
        if (!IsClosedLeafBody(body, method.UnderlyingPointer, compareIP,
                predicateIP, end, field.Offset, field.Local.Register.Name, comparison.OpCode))
            return null;

        return new Evidence(value, isNull);
    }

    private static bool IsOrdinaryReference(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) || NullCheckedCall.IsBoundedArrayReference(type);

    internal static bool IsProvedNullReceiverOffset(int fieldOffset, int pointerSize) =>
        pointerSize == 8 && (fieldOffset is 16 or 24 or 32 or 72 or 88);

    private static bool IsNullPredicate(ManagedOpCode op, out bool isNull)
    {
        isNull = op is ManagedOpCode.CheckEqual or ManagedOpCode.CheckLessOrEqualUnsigned;
        return isNull || op is ManagedOpCode.CheckNotEqual or ManagedOpCode.CheckGreaterUnsigned;
    }

    internal static bool IsClosedLeafBody(IReadOnlyList<NativeInstruction> body, ulong entry,
        ulong compareIP, ulong predicateIP, ulong end, int fieldOffset,
        string receiverRegister, ManagedOpCode op)
    {
        // A single two-byte AX self-exchange is an architectural NOP. Apart
        // from that bounded entry padding, the entire body is one field read,
        // one flag consumer into AL, and a plain return.
        var first = body.Count == 4 && IsEntryPadding(body[0], entry) ? 1 : 0;
        if (body.Count != first + 3)
            return false;
        var compare = body[first];
        var predicate = body[first + 1];
        var ret = body[first + 2];
        return compare.IP == (first == 0 ? entry : body[0].NextIP) && compare.IP == compareIP &&
               predicate.IP == predicateIP && ret.IP == predicate.NextIP &&
               ret.Code == Code.Retnq && ret.OpCount == 0 && ret.NextIP == end &&
               IsExactNativeSite(compare, predicate, fieldOffset, receiverRegister, op);
    }

    private static bool IsEntryPadding(NativeInstruction instruction, ulong entry) =>
        instruction.IP == entry && instruction.Length == 2 &&
        instruction.Code == Code.Nopw && instruction.CodeSize == CodeSize.Code64 &&
        instruction.OpCount == 0 &&
        instruction.SegmentPrefix == NativeRegister.None &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix;

    internal static bool IsExactNativeSite(NativeInstruction compare, NativeInstruction predicate,
        int fieldOffset, string receiverRegister, ManagedOpCode op)
    {
        if (fieldOffset < 0 || compare.Code != Code.Cmp_rm64_imm8 ||
            compare.CodeSize != CodeSize.Code64 || compare.Op0Kind != OpKind.Memory ||
            compare.Op1Kind != OpKind.Immediate8to64 || compare.GetImmediate(1) != 0 ||
            // The Win64 instance receiver is the full RCX pointer. ECX with an
            // address-size override truncates it even though register-name
            // normalization would otherwise report the same spelling.
            receiverRegister != "rcx" || compare.MemoryBase != NativeRegister.RCX ||
            compare.MemoryIndex != NativeRegister.None || compare.SegmentPrefix != NativeRegister.None ||
            compare.MemoryDisplacement64 != (ulong)fieldOffset ||
            compare.HasLockPrefix || compare.HasRepPrefix || compare.HasRepnePrefix ||
            X86Utils.GetRegisterName(compare.MemoryBase) != receiverRegister)
            return false;

        if (predicate.IP != compare.NextIP || predicate.CodeSize != CodeSize.Code64 ||
            predicate.HasLockPrefix || predicate.HasRepPrefix || predicate.HasRepnePrefix ||
            predicate.SegmentPrefix != NativeRegister.None)
            return false;

        return (op, predicate.Code) switch
        {
            (ManagedOpCode.CheckEqual, Code.Sete_rm8) or
                (ManagedOpCode.CheckNotEqual, Code.Setne_rm8) or
                (ManagedOpCode.CheckGreaterUnsigned, Code.Seta_rm8) or
                (ManagedOpCode.CheckLessOrEqualUnsigned, Code.Setbe_rm8)
                => predicate.Op0Kind == OpKind.Register && predicate.Op0Register == NativeRegister.AL,
            _ => false
        };
    }

    private static bool IsFileBacked(PE pe, MethodAnalysisContext method)
    {
        var length = method.RawBytes.Length;
        var start = method.UnderlyingPointer;
        var end = start + (ulong)length;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        return first >= 0 && last - first == length - 1 &&
               last < image.Length &&
               image.Slice((int)first, length).SequenceEqual(method.RawBytes.AsSpan());
    }
}
