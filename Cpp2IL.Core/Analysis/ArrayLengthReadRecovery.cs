using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Materializes an independently proved SZARRAY Length read before signed type
/// propagation. The shared null-guard coalescer decides whether its check can be
/// retained by this exact read, without moving preceding managed effects.
/// </summary>
internal static class ArrayLengthReadRecovery
{
    private const string EvidenceKey = "x64-guarded-array-length";

    internal sealed record SetupRead(Instruction Operation, FieldReference Source);
    internal sealed record PrefixField(FieldReference Access, FieldAnalysisContext Field,
        LocalVariable Receiver, Register Register, TypeAnalysisContext ReceiverType, int Offset,
        TypeAnalysisContext FieldType, FieldAttributes Attributes, int StorageWidth);
    internal sealed record PrefixLocal(LocalVariable Local, Register Register,
        TypeAnalysisContext? Type, bool IsThis, bool IsMethodInfo);
    internal sealed record PrefixEffect(Instruction Operation, OpCode OpCode, int Width,
        CallSemantics Semantics, ulong? Address, IReadOnlyList<IOperand> Operands,
        IReadOnlyList<PrefixField> Fields, IReadOnlyList<PrefixLocal> Locals);
    private sealed record NullArmProbe(Instruction Probe, LocalVariable Value, Register Register, ArrayLength Access,
        Instruction Comparison, LocalVariable Condition, Instruction Branch,
        Block Guard, Block ProbeBlock, Block NormalArm, RuntimeNullThrowEvidence Helper);

    internal sealed class Evidence(Instruction operation, LocalVariable result, LocalVariable array,
        ArrayLength access, X64ArrayLengthReadProof.Site native, Instruction? capture, FieldReference? source,
        IReadOnlyList<SetupRead> setupReads)
    {
        internal Instruction Operation { get; } = operation;
        internal LocalVariable Result { get; } = result;
        internal LocalVariable Array { get; } = array;
        internal ArrayLength Access { get; } = access;
        internal X64ArrayLengthReadProof.Site Native { get; } = native;
        internal Instruction? Capture { get; } = capture;
        internal FieldReference? Source { get; } = source;
        internal IReadOnlyList<SetupRead> SetupReads { get; } = setupReads;
        internal bool RequiresNullProbe => SetupReads.Count != 0;
        internal bool GuardRecorded { get; set; }
        internal IReadOnlyList<PrefixEffect> PrefixEffects { get; set; } = [];
        private NullArmProbe? Probe { get; set; }

        internal bool HasValidProbe(MethodAnalysisContext method) =>
            Probe != null && ValidProbe(method, this, Probe);

        internal bool HasRecordedProbeComparison(Instruction comparison, LocalVariable reference) =>
            GuardRecorded && RequiresNullProbe && Probe is { } probe &&
            ReferenceEquals(probe.Comparison, comparison) && ReferenceEquals(Array, reference);

        internal void SetProbe(Instruction probe, LocalVariable value, ArrayLength access,
            Instruction comparison, Instruction branch, Block guard, Block probeBlock, Block normalArm,
            RuntimeNullThrowEvidence helper) =>
            Probe = new NullArmProbe(probe, value, value.Register, access, comparison,
                (LocalVariable)comparison.Operands[0], branch, guard, probeBlock, normalArm, helper);
    }

    internal static IReadOnlyList<Evidence> GetEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<List<Evidence>>(EvidenceKey) ?? [];

    internal static bool HasRecordedNullProbeComparison(MethodAnalysisContext method,
        Instruction comparison, LocalVariable reference) =>
        GetEvidence(method).Any(evidence => evidence.HasRecordedProbeComparison(comparison, reference));

    internal static void Run(MethodAnalysisContext method)
    {
        var evidence = new List<Evidence>();
        method.PutExtraData(EvidenceKey, evidence);
        if (method.ControlFlowGraph is not { } graph || !X64ArrayLengthReadProof.HasUnchangedMethod(method))
            return;
        foreach (var operation in graph.Instructions)
        {
            if (operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0 or 32,
                    CallSemantics: CallSemantics.Direct, NativeAddress: { } address,
                    Operands: [LocalVariable result, ISIL.MemoryOperand
                        { Base: LocalVariable array, Index: null, Scale: 0 } memory] } ||
                memory.Addend != Il2CppArrayUtils.GetLengthOffset(method.AppContext.Binary) ||
                result.Type != null && !ReferenceEquals(result.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
                X64ArrayLengthReadProof.Find(method, address) is not { } native ||
                !ResultSlot(method, operation, result, native) ||
                !TryBindArray(method, array, operation, native, out var capture, out var source) ||
                !TryBindSetupReads(method, native, capture, out var setupReads) ||
                !HasUnchangedOrder(method, operation))
                continue;
            // The matching runtime caps SZARRAY allocation at Int32.MaxValue and
            // GetLength casts max_length to Int32. This is a native32 Length
            // projection, never the native-sized LongLength operation.
            var access = new ArrayLength(array);
            operation.SetOperand(1, access);
            operation.IntegerBitWidth = 32;
            result.Type = method.AppContext.SystemTypes.SystemInt32Type;
            evidence.Add(new Evidence(operation, result, array, access, native, capture, source, setupReads));
        }
    }

    internal static bool HasBoundReceiver(MethodAnalysisContext method, LocalVariable receiver) =>
        GetEvidence(method).Any(evidence => ReferenceEquals(evidence.Array, receiver) && IsCurrent(method, evidence));

    internal static Evidence? TryGetBoundRead(MethodAnalysisContext method,
        Instruction operation, LocalVariable receiver) =>
        GetEvidence(method).FirstOrDefault(evidence => ReferenceEquals(evidence.Operation, operation) &&
            ReferenceEquals(evidence.Array, receiver) && IsCurrent(method, evidence));

    internal static bool RecordGuard(MethodAnalysisContext method, Evidence evidence,
        Instruction comparison, Instruction branch)
    {
        if (evidence.GuardRecorded || evidence.RequiresNullProbe ||
            !GetEvidence(method).Contains(evidence) || !IsCurrent(method, evidence) ||
            !MatchesGuard(evidence, comparison, branch) ||
            comparison.Destination is not LocalVariable condition ||
            !ReferenceEquals(condition.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
            !TryBindPrefixEffects(method, evidence, comparison, branch, null, out var prefix))
            return false;
        evidence.PrefixEffects = prefix;
        evidence.GuardRecorded = true;
        return true;
    }

    private static bool MatchesGuard(Evidence evidence, Instruction comparison, Instruction branch) =>
        comparison is { OpCode: OpCode.CheckEqual, IntegerBitWidth: 64,
                CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable condition, LocalVariable receiver, Immediate { Value: 0 }] } &&
        ReferenceEquals(receiver, evidence.Array) &&
        (comparison.NativeAddress == evidence.Native.TestAddress || comparison.NativeAddress == evidence.Native.BranchAddress) &&
        branch is { OpCode: OpCode.ConditionalJump, IntegerBitWidth: 0,
            CallSemantics: CallSemantics.Direct, Operands: [Block _, LocalVariable tested] } &&
        ReferenceEquals(tested, condition) && branch.NativeAddress == evidence.Native.BranchAddress;

    internal static bool RecordNullArmProbe(MethodAnalysisContext method, Evidence evidence,
        Instruction comparison, Instruction branch, Block guard, Block probeBlock, Block normalArm,
        Instruction probe, RuntimeNullThrowEvidence helper)
    {
        if (evidence.GuardRecorded || !evidence.RequiresNullProbe ||
            !GetEvidence(method).Contains(evidence) || !IsCurrent(method, evidence) ||
            !MatchesGuard(evidence, comparison, branch) ||
            probe.Operands is not [LocalVariable value, ArrayLength access])
            return false;
        var binding = new NullArmProbe(probe, value, value.Register, access, comparison,
            (LocalVariable)comparison.Operands[0], branch, guard, probeBlock, normalArm, helper);
        if (!ValidProbe(method, evidence, binding) ||
            !TryBindPrefixEffects(method, evidence, comparison, branch, probe, out var prefix))
            return false;
        evidence.PrefixEffects = prefix;
        evidence.SetProbe(probe, value, access, comparison, branch, guard, probeBlock, normalArm, helper);
        evidence.GuardRecorded = true;
        return true;
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph;
        if (graph == null)
            return GetEvidence(method).Count == 0;
        foreach (var evidence in GetEvidence(method))
        {
            if (!evidence.GuardRecorded || !IsCurrent(method, evidence) ||
                evidence.RequiresNullProbe && !evidence.HasValidProbe(method) ||
                !ValidPrefixEffects(method, evidence) ||
                graph.FindBlockByInstruction(evidence.Operation) is not { } readBlock ||
                !new DominatorInfo(graph).Dominates(readBlock, graph.ExitBlock))
                return false;
        }
        return true;
    }

    private static bool IsCurrent(MethodAnalysisContext method, Evidence evidence)
    {
        var operation = evidence.Operation;
        return operation is { OpCode: OpCode.Move, IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
                   Operands: [LocalVariable result, ArrayLength access], NativeAddress: { } address } &&
               ReferenceEquals(result, evidence.Result) && ReferenceEquals(access, evidence.Access) &&
               ReferenceEquals(access.Array, evidence.Array) &&
               ReferenceEquals(result.Type, method.AppContext.SystemTypes.SystemInt32Type) &&
               X64ArrayLengthReadProof.Find(method, address) is { } current &&
               X64ArrayLengthReadProof.SameSite(current, evidence.Native) &&
               ResultSlot(method, operation, result, current) &&
               TryBindArray(method, evidence.Array, operation, current, out var capture, out var source) &&
               ReferenceEquals(capture, evidence.Capture) && ReferenceEquals(source, evidence.Source) &&
               TryBindSetupReads(method, current, capture, out var setupReads) &&
               setupReads.SequenceEqual(evidence.SetupReads) &&
               HasUnchangedOrder(method, operation);
    }

    internal static bool HasBoundSetupRead(MethodAnalysisContext method,
        Instruction operation, LocalVariable receiver)
    {
        return GetEvidence(method).Any(evidence => ReferenceEquals(evidence.Array, receiver) &&
            IsCurrent(method, evidence) && evidence.SetupReads.Any(read => ReferenceEquals(read.Operation, operation)));
    }

    private static bool TryBindPrefixEffects(MethodAnalysisContext method, Evidence evidence,
        Instruction comparison, Instruction branch, Instruction? probe, out IReadOnlyList<PrefixEffect> effects)
    {
        var operations = PrefixOperations(method, evidence);
        var snapshots = new List<PrefixEffect>();
        effects = snapshots;
        if (X64NativeInstructionReader.ReadRootBody(method) is not { } body)
            return false;
        foreach (var operation in operations)
        {
            // Field accesses contain mutable field/receiver/offset projections.
            // Retaining their operand object alone would allow a later rewrite
            // to change an effect that must still occur before the null check.
            if (operation.NativeAddress is not { } address ||
                !operation.Operands.Any(operand => operand is FieldReference) ||
                operation.Operands.Any(operand => operand is not (FieldReference or LocalVariable or Immediate)) ||
                !HasBoundPrefixProjection(method, body, operation))
                return false;
            var fields = new List<PrefixField>();
            foreach (var access in operation.Operands.OfType<FieldReference>())
            {
                if (!TryBindPrefixField(method, body, address, access, out var field))
                    return false;
                fields.Add(field!);
            }
            var locals = operation.Operands.OfType<LocalVariable>().Select(local =>
                new PrefixLocal(local, local.Register, local.Type, local.IsThis, local.IsMethodInfo)).ToArray();
            snapshots.Add(new PrefixEffect(operation, operation.OpCode, operation.IntegerBitWidth,
                operation.CallSemantics, address, operation.Operands.ToArray(), fields, locals));
        }
        // This bounded route admits only mandatory preguard effects. An effect
        // on another conditional path needs a separate control-flow proof.
        return operations.All(operation => Precedes(method, operation, comparison) &&
            Precedes(method, operation, branch) && (probe == null || Precedes(method, operation, probe)));
    }

    private static Instruction[] PrefixOperations(MethodAnalysisContext method, Evidence evidence) =>
        method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions).Where(operation =>
            operation.NativeAddress < evidence.Native.TestAddress && IsObservable(operation)).ToArray();

    private static bool ValidPrefixEffects(MethodAnalysisContext method, Evidence evidence)
    {
        if (!PrefixOperations(method, evidence).SequenceEqual(evidence.PrefixEffects.Select(effect => effect.Operation)) ||
            X64NativeInstructionReader.ReadRootBody(method) is not { } body)
            return false;
        foreach (var effect in evidence.PrefixEffects)
        {
            var operation = effect.Operation;
            if (operation.OpCode != effect.OpCode || operation.IntegerBitWidth != effect.Width ||
                operation.CallSemantics != effect.Semantics || operation.NativeAddress != effect.Address ||
                !operation.Operands.SequenceEqual(effect.Operands) ||
                !HasBoundPrefixProjection(method, body, operation) ||
                effect.Fields.Any(field => !ValidPrefixField(method, body, effect.Address!.Value, field)) ||
                effect.Locals.Any(local => local.Local.Register != local.Register ||
                    !NullCheckedCall.SameOrdinaryType(local.Local.Type, local.Type) ||
                    local.Local.IsThis != local.IsThis || local.Local.IsMethodInfo != local.IsMethodInfo) ||
                !Precedes(method, operation, evidence.Operation))
                return false;
        }
        return true;
    }

    internal static bool HasBoundPrefixProjection(MethodAnalysisContext method,
        IReadOnlyList<Iced.Intel.Instruction> body, Instruction operation)
    {
        if (operation.CallSemantics != CallSemantics.Direct || operation.NativeAddress is not { } address ||
            body.Where(instruction => instruction.IP == address).ToArray() is not [var native])
            return false;
        if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                Operands: [LocalVariable value, FieldReference read] })
            return native.Code is Code.Mov_r32_rm32 or Code.Mov_r64_rm64 &&
                   native.Op0Kind == OpKind.Register && native.Op1Kind == OpKind.Memory &&
                   value.Register.Copy() == new Register(null, X86Utils.GetRegisterName(native.Op0Register)) &&
                   NullCheckedCall.SameOrdinaryType(value.Type, read.Field.FieldType);
        if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                Operands: [FieldReference stored, Immediate literal] })
            return LiteralFieldStoreProof.IsValidFor(method, operation, stored, literal);
        if (operation.Operands is not [FieldReference written, FieldReference loaded, Immediate { Value: 1 }] ||
            operation.IntegerBitWidth is not (0 or 32) ||
            !ReferenceEquals(written.Field, loaded.Field) || !ReferenceEquals(written.Local, loaded.Local) ||
            written.Offset != loaded.Offset || native.Op0Kind != OpKind.Memory ||
            written.Field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !(ReferenceEquals(written.Field.FieldType, method.AppContext.SystemTypes.SystemInt32Type) ||
              ReferenceEquals(written.Field.FieldType, method.AppContext.SystemTypes.SystemUInt32Type)) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(written, 32))
            return false;
        return operation.OpCode == OpCode.Add && native.Code == Code.Inc_rm32 ||
               operation.OpCode == OpCode.Subtract && native.Code == Code.Dec_rm32;
    }

    private static bool TryBindPrefixField(MethodAnalysisContext method, IReadOnlyList<Iced.Intel.Instruction> body,
        ulong address, FieldReference access, out PrefixField? snapshot)
    {
        snapshot = null;
        var field = access.Field;
        if (field.DeclaringType.Definition is not { } owner || field.Name != field.DefaultName ||
            access.Local.Type is not { } receiverType ||
            !Incoming(method, access.Local, method.DeclaringType!, false, out var incoming) ||
            body.Where(instruction => instruction.IP == address).ToArray() is not [var native] ||
            native.Op0Kind != OpKind.Memory && native.Op1Kind != OpKind.Memory ||
            native.MemoryIndex != NativeRegister.None || native.MemoryIndexScale != 1 ||
            native.MemoryDisplacement64 != (ulong)access.Offset ||
            native.HasLockPrefix || native.HasRepPrefix || native.HasRepnePrefix ||
            native.SegmentPrefix != NativeRegister.None ||
            !X64NativeRegisterAliasProof.IsAlias(body, address, native.MemoryBase, incoming))
            return false;
        var width = native.MemorySize.GetSize() * 8;
        if (width is not (8 or 16 or 32 or 64) || access.Offset < 0 ||
            (ulong)access.Offset + (uint)(width / 8) > owner.RawSizes.instance_size ||
            !(field.FieldType.IsValueType
                ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, width) ||
                  NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, width)
                : width == 64 && NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access)))
            return false;
        snapshot = new PrefixField(access, field, access.Local, access.Local.Register, receiverType,
            access.Offset, field.FieldType, field.Attributes, width);
        return true;
    }

    private static bool ValidPrefixField(MethodAnalysisContext method, IReadOnlyList<Iced.Intel.Instruction> body,
        ulong address, PrefixField snapshot)
    {
        var access = snapshot.Access;
        return ReferenceEquals(access.Field, snapshot.Field) && ReferenceEquals(access.Local, snapshot.Receiver) &&
               access.Local.Register == snapshot.Register && access.Offset == snapshot.Offset &&
               NullCheckedCall.SameOrdinaryType(access.Local.Type, snapshot.ReceiverType) &&
               NullCheckedCall.SameOrdinaryType(access.Field.FieldType, snapshot.FieldType) &&
               access.Field.Attributes == snapshot.Attributes &&
               TryBindPrefixField(method, body, address, access, out var current) &&
               current!.StorageWidth == snapshot.StorageWidth;
    }

    private static bool ValidProbe(MethodAnalysisContext method, Evidence evidence, NullArmProbe binding)
    {
        var graph = method.ControlFlowGraph;
        if (graph == null || !MatchesGuard(evidence, binding.Comparison, binding.Branch) ||
            !ReferenceEquals(binding.Comparison.Destination, binding.Condition) ||
            !ReferenceEquals(binding.Condition.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
            binding.Condition.IsThis || binding.Condition.IsMethodInfo ||
            method.ParameterLocals.Contains(binding.Condition) || Mutable(method, binding.Condition) ||
            graph.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, binding.Condition)) != 1 ||
            evidence.Capture != null &&
            (!Precedes(method, evidence.Capture, binding.Comparison) ||
             !Precedes(method, evidence.Capture, binding.Branch) ||
             !Precedes(method, evidence.Capture, binding.Probe)) ||
            evidence.PrefixEffects.Any(effect => !Precedes(method, effect.Operation, binding.Comparison) ||
                !Precedes(method, effect.Operation, binding.Branch) || !Precedes(method, effect.Operation, binding.Probe)) ||
            graph.FindBlockByInstruction(evidence.Operation) is not { } readBlock ||
            graph.FindBlockByInstruction(binding.Comparison) != binding.Guard ||
            graph.FindBlockByInstruction(binding.Branch) != binding.Guard ||
            binding.Guard.Instructions.IndexOf(binding.Comparison) >= binding.Guard.Instructions.IndexOf(binding.Branch) ||
            !ReferenceEquals(binding.Guard.Instructions.LastOrDefault(), binding.Branch) ||
            binding.Branch.Operands[0] is not Block taken || !ReferenceEquals(taken, binding.ProbeBlock) ||
            binding.Guard.Successors.Count != 2 || !binding.Guard.Successors.Contains(binding.ProbeBlock) ||
            !binding.Guard.Successors.Contains(binding.NormalArm) ||
            !graph.Blocks.Contains(binding.ProbeBlock) || !graph.Blocks.Contains(binding.NormalArm) ||
            binding.ProbeBlock.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, binding.Guard) ||
            binding.ProbeBlock.Successors is not [var successor] || !ReferenceEquals(successor, binding.NormalArm) ||
            binding.NormalArm.Predecessors.Count != 2 || !binding.NormalArm.Predecessors.Contains(binding.Guard) ||
            !binding.NormalArm.Predecessors.Contains(binding.ProbeBlock) ||
            binding.ProbeBlock.Instructions is not [var probe, { OpCode: OpCode.Jump, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands: [Block next] }] ||
            !ReferenceEquals(next, binding.NormalArm) || !ReferenceEquals(probe, binding.Probe) ||
            probe is not { OpCode: OpCode.Move, IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable value, ArrayLength access] } ||
            probe.NativeAddress != evidence.Native.NullCallAddress || !ReferenceEquals(value, binding.Value) ||
            !ReferenceEquals(access, binding.Access) || !ReferenceEquals(access.Array, evidence.Array) ||
            !ReferenceEquals(value.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
            value.IsThis || value.IsMethodInfo || method.ParameterLocals.Contains(value) ||
            value.Register != binding.Register ||
            method.Locals.Count(local => ReferenceEquals(local, value)) > 1 ||
            method.Locals.Any(local => !ReferenceEquals(local, value) && local.Register.Number == value.Register.Number) ||
            graph.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, value)) != 1 ||
            graph.Instructions.SelectMany(OperandEffects.ReadLocals).Any(local => ReferenceEquals(local, value)) ||
            Mutable(method, value) || binding.Helper.NativeTarget != evidence.Native.NullTarget ||
            !binding.Helper.IsValidFor(method.AppContext) ||
            !new DominatorInfo(graph).Dominates(binding.NormalArm, readBlock) ||
            evidence.SetupReads.Any(read => graph.FindBlockByInstruction(read.Operation) != binding.NormalArm ||
                !Precedes(method, read.Operation, evidence.Operation)))
            return false;
        return true;
    }

    private static bool TryBindSetupReads(MethodAnalysisContext method,
        X64ArrayLengthReadProof.Site native, Instruction? capture, out IReadOnlyList<SetupRead> reads)
    {
        var found = new List<SetupRead>();
        reads = found;
        var graph = method.ControlFlowGraph!;
        foreach (var load in native.Setup)
        {
            if (load.Op1Kind != OpKind.Memory)
                continue;
            var operations = graph.Instructions.Where(instruction => instruction.NativeAddress == load.IP).ToArray();
            if (operations is not [var operation] || !IsBoundSetupRead(method, operation, native, capture) ||
                operation.Operands[1] is not FieldReference source)
                return false;
            found.Add(new SetupRead(operation, source));
        }
        return true;
    }

    private static bool IsBoundSetupRead(MethodAnalysisContext method, Instruction operation,
        X64ArrayLengthReadProof.Site native, Instruction? capture)
    {
        var graph = method.ControlFlowGraph!;
        if (operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                NativeAddress: { } address, Operands: [LocalVariable value, FieldReference field] } ||
            native.Setup.Where(instruction => instruction.IP == address).ToArray() is not [var load] ||
            load.Op1Kind != OpKind.Memory || load.MemoryBase != NativeRegister.RCX ||
            !ReferenceEquals(field.Field.DeclaringType, method.DeclaringType) ||
            field.Field.Name != field.Field.DefaultName ||
            !ReferenceEquals(field.Field.FieldType, method.AppContext.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(value.Type, field.Field.FieldType) ||
            field.Offset != (long)load.MemoryDisplacement64 || field.Field.DeclaringType.Definition is not { } owner ||
            (ulong)field.Offset + 4 > owner.RawSizes.instance_size ||
            field.Field.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(field, 32) ||
            !Incoming(method, field.Local, method.DeclaringType!, false, out var incoming) || incoming != NativeRegister.RCX ||
            value.Register.Copy() != new Register(null, X86Utils.GetRegisterName(load.Op0Register)) ||
            value.IsThis || value.IsMethodInfo || method.ParameterLocals.Contains(value) || Mutable(method, value) ||
            !HasUnchangedOrder(method, operation) || X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
            !X64NativeRegisterAliasProof.IsAlias(body, address, load.MemoryBase, incoming))
            return false;
        // A previous, dominating dereference of the same original this value
        // has already established its nonnull identity on this path. A raw field
        // offset alone never makes an arbitrary field read pure setup.
        if (capture is { NativeAddress: { } capturedAt, Operands: [_, FieldReference capturedField] } &&
            capturedAt < native.TestAddress && ReferenceEquals(capturedField.Local, field.Local) &&
            Precedes(method, capture, operation))
            return true; // TryBindArray has independently authenticated this captured load.
        return graph.Instructions.Any(prior => prior is { OpCode: OpCode.Add, IntegerBitWidth: 0 or 32,
            CallSemantics: CallSemantics.Direct, NativeAddress: { } priorAddress,
            Operands: [FieldReference written, FieldReference readField, Immediate { Value: 1 }] } &&
            priorAddress < native.TestAddress && ReferenceEquals(written.Local, field.Local) &&
            ReferenceEquals(readField.Local, field.Local) && ReferenceEquals(written.Field, field.Field) &&
            ReferenceEquals(readField.Field, field.Field) && written.Offset == field.Offset &&
            readField.Offset == field.Offset && Precedes(method, prior, operation) &&
            body.Where(instruction => instruction.IP == priorAddress).ToArray() is
                [{ Code: Code.Inc_rm32, Op0Kind: OpKind.Memory } increment] &&
            increment.MemorySize.GetSize() == 4 && increment.MemoryIndex == NativeRegister.None &&
            increment.MemoryDisplacement64 == (ulong)field.Offset &&
            !increment.HasLockPrefix && !increment.HasRepPrefix && !increment.HasRepnePrefix &&
            increment.SegmentPrefix == NativeRegister.None &&
            X64NativeRegisterAliasProof.IsAlias(body, priorAddress, increment.MemoryBase, incoming));
    }

    private static bool ResultSlot(MethodAnalysisContext method, Instruction operation,
        LocalVariable result, X64ArrayLengthReadProof.Site native)
    {
        var graph = method.ControlFlowGraph!;
        var expected = new Register(null, native.ComparisonCapture ? "COMPARE_RIGHT" :
            X86Utils.GetRegisterName(native.ResultRegister));
        return graph.Instructions.Contains(operation) && graph.FindBlockByInstruction(operation) != null &&
               !result.IsThis && !result.IsMethodInfo && !method.ParameterLocals.Contains(result) &&
               result.Register.Copy() == expected &&
               graph.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, result)) == 1 &&
               !Mutable(method, result);
    }

    private static bool TryBindArray(MethodAnalysisContext method, LocalVariable array,
        Instruction read, X64ArrayLengthReadProof.Site native, out Instruction? capture, out FieldReference? source)
    {
        capture = null;
        source = null;
        var graph = method.ControlFlowGraph!;
        if (array.Type is not SzArrayTypeAnalysisContext type ||
            !NullCheckedCall.IsBoundedArrayReference(type) || array.IsThis || array.IsMethodInfo ||
            Mutable(method, array) || X64NativeInstructionReader.ReadRootBody(method) is not { } body)
            return false;
        if (method.ParameterLocals.Contains(array))
            return Incoming(method, array, array.Type, true, out var incoming) &&
                   X64NativeRegisterAliasProof.IsAlias(body, native.ReadAddress, native.ArrayRegister, incoming);
        var definitions = graph.Instructions.Where(instruction => ReferenceEquals(instruction.Destination, array)).ToArray();
        if (definitions is not [{ OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, NativeAddress: { } address,
                Operands: [LocalVariable destination, FieldReference field] } producer] ||
            !ReferenceEquals(destination, array) || !ReferenceEquals(field.Field.DeclaringType, method.DeclaringType) ||
            field.Field.Name != field.Field.DefaultName || field.Field.OverrideFieldType != null ||
            field.Field.BackingData?.Field.RawFieldType is not { } raw || !ExactArray(method, raw, type) ||
            field.Field.DeclaringType.Definition is not { } owner || field.Offset < 16 ||
            (ulong)field.Offset + 8 > owner.RawSizes.instance_size ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(field) ||
            !Incoming(method, field.Local, method.DeclaringType!, false, out var ownerRegister) ||
            !Precedes(method, producer, read) || !HasUnchangedOrder(method, producer) ||
            body.Where(instruction => instruction.IP == address).ToArray() is not
                [{ Code: Code.Mov_r64_rm64, Op0Kind: OpKind.Register, Op1Kind: OpKind.Memory } load] ||
            array.Register.Copy() != new Register(null, X86Utils.GetRegisterName(load.Op0Register)) ||
            load.MemorySize.GetSize() != 8 || load.MemoryIndex != NativeRegister.None ||
            load.MemoryDisplacement64 != (ulong)field.Offset || load.HasLockPrefix || load.HasRepPrefix ||
            load.HasRepnePrefix || load.SegmentPrefix != NativeRegister.None ||
            !X64NativeRegisterAliasProof.IsAlias(body, address, load.MemoryBase, ownerRegister) ||
            !X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body, native.ReadAddress, native.ArrayRegister, address))
            return false;
        capture = producer;
        source = field;
        return true;
    }

    private static bool Incoming(MethodAnalysisContext method, LocalVariable local,
        TypeAnalysisContext expected, bool requireArray, out NativeRegister incoming)
    {
        incoming = NativeRegister.None;
        if (!method.ParameterLocals.Contains(local) || local.IsMethodInfo || Mutable(method, local) ||
            !NullCheckedCall.SameOrdinaryType(local.Type, expected) ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)))
            return false;
        int slot;
        if (local.IsThis)
        {
            if (requireArray || method.IsStatic || !ReferenceEquals(local.Type, method.DeclaringType))
                return false;
            slot = 0;
        }
        else
        {
            if (!requireArray || expected is not SzArrayTypeAnalysisContext array ||
                LocalVariables.GetIncomingParameterIndex(method, local) is not { } index)
                return false;
            var parameter = method.Parameters[index];
            if (parameter.ParameterIndex != index || !ReferenceEquals(parameter.DeclaringMethod, method) ||
                parameter.OverrideParameterType != null || parameter.IsRef || parameter.Name != parameter.DefaultName ||
                parameter.Attributes != parameter.DefaultAttributes ||
                !NullCheckedCall.SameOrdinaryType(parameter.ParameterType, array) ||
                !NullCheckedCall.SameOrdinaryType(parameter.DefaultParameterType, array) ||
                parameter.Definition?.RawType is not { } raw || !ExactArray(method, raw, array))
                return false;
            slot = index + (method.IsStatic ? 0 : 1);
        }
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        return slot < abi.Length && slot < method.ParameterOperands.Count &&
               abi[slot] is Register original && method.ParameterOperands[slot] is Register current &&
               original == current && local.Register == original &&
               Enum.TryParse(original.Name, true, out incoming) &&
               incoming is >= NativeRegister.RAX and <= NativeRegister.R15 && incoming != NativeRegister.RSP;
    }

    private static bool ExactArray(MethodAnalysisContext method, Il2CppType raw, SzArrayTypeAnalysisContext type)
    {
        if (raw is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NullCheckedCall.IsBoundedArrayReference(type) || raw.GetEncapsulatedType() is not
                { NumMods: 0, Byref: 0, Pinned: 0 } element ||
            element.Type != type.ElementType.Type ||
            !ReferenceEquals(method.AppContext.ResolveIl2CppType(element), type.ElementType))
            return false;
        var value = type.ElementType;
        return value.Name == value.DefaultName && value.Namespace == value.DefaultNamespace &&
               value.Attributes == value.DefaultAttributes && ReferenceEquals(value.BaseType, value.DefaultBaseType);
    }

    private static bool Mutable(MethodAnalysisContext method, LocalVariable local) =>
        OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph!.Instructions).Any(changed =>
            changed.Register.Number == local.Register.Number);

    private static bool Precedes(MethodAnalysisContext method, Instruction producer, Instruction consumer)
    {
        var graph = method.ControlFlowGraph!;
        if (graph.FindBlockByInstruction(producer) is not { } before ||
            graph.FindBlockByInstruction(consumer) is not { } after)
            return false;
        return ReferenceEquals(before, after)
            ? before.Instructions.IndexOf(producer) < before.Instructions.IndexOf(consumer)
            : new DominatorInfo(graph).Dominates(before, after);
    }

    // Native addresses order observable operations; dominance independently keeps
    // a captured array available on every predecessor path to the Length read.
    internal static bool HasUnchangedOrder(MethodAnalysisContext method, Instruction read)
    {
        var operations = method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions).ToArray();
        var position = Array.IndexOf(operations, read);
        if (position < 0 || read.NativeAddress is not { } address)
            return false;
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = operations[index];
            if (ReferenceEquals(operation, read) || !IsObservable(operation))
                continue;
            if (operation.NativeAddress is not { } other || other == address ||
                (index < position) != (other < address))
                return false;
        }
        return true;
    }

    private static bool IsObservable(Instruction instruction) =>
        instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall or OpCode.Throw or
            OpCode.RuntimeNullThrow or OpCode.Newobj or OpCode.NewArr or OpCode.Divide or
            OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned ||
        instruction.Operands.Any(operand => operand is FieldReference or ArrayAccess or ArrayLength or ISIL.MemoryOperand);
}
