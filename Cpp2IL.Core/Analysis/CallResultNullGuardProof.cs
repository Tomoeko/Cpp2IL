using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a guarded call on one SSA call result when identical machine code has
/// been folded across unrelated managed methods. The receiver's unchanged
/// class chain must identify exactly one applicable managed target.
/// </summary>
internal static class CallResultNullGuardProof
{
    internal static bool HasBoundTarget(MethodAnalysisContext caller, LocalVariable result,
        Instruction origin, Instruction guardedCall, MethodAnalysisContext target)
    {
        if (origin is not { OpCode: OpCode.Call, IntegerBitWidth: 0 } ||
            !ReferenceEquals(origin.Destination, result) ||
            !TryGetProducer(origin, out var producer, out var producerReceiver) ||
            !ReferenceEquals(producer.AppContext, caller.AppContext) ||
            !ReferenceEquals(producer.ReturnType, result.Type) ||
            !HasDirectNativeCall(caller, origin, producer) ||
            !HasUnambiguousTarget(producer, producerReceiver.Type) ||
            !ReferenceEquals(target.AppContext, caller.AppContext) ||
            !HasDirectNativeCall(caller, guardedCall, target) ||
            !HasUnambiguousTarget(target, result.Type))
            return false;

        return true;
    }

    private static bool TryGetProducer(Instruction origin,
        out MethodAnalysisContext producer, out LocalVariable receiver)
    {
        if (NullCheckedCall.TryGet(origin, out producer, out receiver))
            return true;
        producer = null!;
        receiver = null!;
        if (origin.Operands is not [MethodAnalysisContext candidate, LocalVariable, ..] ||
            candidate.Parameters.Count != 0)
            return false;
        const int count = 3;
        if (origin.Operands.Count <= count ||
            origin.Operands[count] is not Immediate { Value: 0 } ||
            origin.Operands.Skip(count + 1).Any(operand =>
                operand is not LocalVariable) ||
            !HasExactReferenceGetterBody(candidate))
            return false;

        // The lifter can retain unrelated caller registers beyond the managed
        // ABI. This exact two-instruction getter reads only RCX, so neither
        // the null MethodInfo slot nor any surplus register can affect it.
        var managedPrefix = new Instruction(-1, OpCode.Call,
            origin.Operands.Take(count).ToList());
        return NullCheckedCall.TryGet(managedPrefix, out producer, out receiver);
    }

    private static bool HasExactReferenceGetterBody(MethodAnalysisContext producer)
    {
        if (producer.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(producer.AppContext) is not { } unwind ||
            producer.Parameters.Count != 0 || producer.UnderlyingPointer == 0 ||
            producer.DeclaringType is not { } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            !NullCheckedCall.IsReferenceClass(producer.ReturnType) ||
            producer.Definition?.RawReturnType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 })
            return false;
        try
        {
            producer.EnsureRawBytes();
            var start = producer.UnderlyingPointer;
            var length = producer.RawBytes.Length;
            if (length < 5 || start > ulong.MaxValue - (ulong)length ||
                unwind.ClassifySpan(start, start + (ulong)length).Kind !=
                    X64UnwindProof.SpanKind.NoEntry ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                    producer.RawBytes.AsSpan(), start) ||
                Enumerable.Range(1, length - 1).Any(offset =>
                    producer.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)))
                return false;

            var body = X86Utils.Iterate(producer.RawBytes.AsSpan(), start, false);
            if (body.Count < 2 || body[0].IP != start ||
                body[^1].NextIP != start + (ulong)length ||
                body.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None) ||
                body.Where((instruction, index) => index > 0 &&
                    instruction.IP != body[index - 1].NextIP).Any() ||
                X86CallerExceptionRegionProof.Check(producer, body,
                    new HashSet<ulong>()) != null ||
                body[0].Code != Code.Mov_r64_rm64 ||
                body[0].Op0Kind != OpKind.Register ||
                body[0].Op0Register != NativeRegister.RAX ||
                body[0].Op1Kind != OpKind.Memory ||
                body[0].MemoryBase != NativeRegister.RCX ||
                body[0].MemoryIndex != NativeRegister.None ||
                body[0].MemorySize.GetSize() != 8 ||
                body[0].MemoryDisplacement64 > int.MaxValue ||
                body[1].Code != Code.Retnq || body[1].OpCount != 0 ||
                body[1].IP != body[0].NextIP ||
                body.Skip(2).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[1].NextIP,
                    start + (ulong)length))
                return false;

            var offset = checked((int)body[0].MemoryDisplacement64);
            var matches = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == offset &&
                ReferenceEquals(field.FieldType, producer.ReturnType) &&
                field.BackingData?.Field.RawFieldType is
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
            return matches is [{ } field] &&
                   NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                       new FieldReference(field, new LocalVariable("native-receiver",
                           new ISIL.Register(null, "rcx"), owner), offset));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasDirectNativeCall(MethodAnalysisContext caller,
        Instruction lifted, MethodAnalysisContext target)
    {
        if (lifted.NativeAddress is not { } address ||
            caller.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(caller.AppContext) is not { } unwind ||
            address < caller.UnderlyingPointer ||
            address - caller.UnderlyingPointer > int.MaxValue - 5)
            return false;
        try
        {
            caller.EnsureRawBytes();
            var offset = checked((int)(address - caller.UnderlyingPointer));
            if (offset > caller.RawBytes.Length - 5 ||
                unwind.ClassifySpan(address, address + 5).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, caller.RawBytes.AsSpan().Slice(offset, 5), address))
                return false;
            var native = X86Utils.Iterate(caller).Where(instruction =>
                instruction.IP == address).ToArray();
            return native is [{ Code: Code.Call_rel32_64,
                Op0Kind: OpKind.NearBranch64 } call] &&
                !call.HasLockPrefix && !call.HasRepPrefix && !call.HasRepnePrefix &&
                call.SegmentPrefix == NativeRegister.None && call.Length == 5 &&
                call.NearBranchTarget == target.UnderlyingPointer;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasUnambiguousTarget(MethodAnalysisContext target,
        TypeAnalysisContext? receiverType)
    {
        if (receiverType == null || !NullCheckedCall.IsReferenceClass(receiverType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target,
                requireUniqueBinding: false) ||
            !target.AppContext.MethodsByAddress.TryGetValue(target.UnderlyingPointer,
                out var bindings) || bindings.Count == 0 ||
            bindings.Count(candidate => ReferenceEquals(candidate, target)) != 1 ||
            bindings.Any(candidate => candidate.IsStatic ||
                candidate.DeclaringType == null))
            return false;

        var owners = new HashSet<TypeAnalysisContext>();
        for (var type = receiverType; type != null; type = type.BaseType)
        {
            if (!owners.Add(type) || !NullCheckedCall.IsReferenceClass(type))
                return false;
        }

        return target.DeclaringType != null && owners.Contains(target.DeclaringType) &&
               bindings.Where(candidate => candidate.DeclaringType != null &&
                   owners.Contains(candidate.DeclaringType)).ToArray() is [var applicable] &&
               ReferenceEquals(applicable, target);
    }
}
