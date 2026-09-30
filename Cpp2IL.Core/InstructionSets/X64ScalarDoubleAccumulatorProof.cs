using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates a complete Boolean-gated, separately rounded binary64 field update.</summary>
internal static class X64ScalarDoubleAccumulatorProof
{
    internal const string EvidenceKey = "X64ScalarDoubleAccumulatorProof";
    internal sealed record Shape(int Pending, int Current, int Baseline, int Total, NativeRegister Temporary);
    private sealed record Evidence(NativeInstruction[] Body, Shape Native, FieldAnalysisContext Pending,
        FieldAnalysisContext Current, FieldAnalysisContext Baseline, FieldAnalysisContext Total, object[] Input);

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        if (decoded.Count < 8 || TryProveShape(decoded.Take(8).ToArray()) is not { } candidate ||
            Find(method) is not { } evidence || candidate != evidence.Native ||
            !decoded.Take(8).SequenceEqual(evidence.Body)) return null;
        method.PutExtraData(EvidenceKey, evidence);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var pending = new Register(null, "double_accumulator_pending");
        var disabled = new Register(null, "double_accumulator_disabled");
        var current = new Register(null, "double_accumulator_current");
        var baseline = new Register(null, "double_accumulator_baseline");
        var delta = new Register(null, "double_accumulator_delta");
        var total = new Register(null, "double_accumulator_total");
        var sum = new Register(null, "double_accumulator_sum");
        var returned = new Instruction(10, OpCode.Return) { NativeAddress = evidence.Body[7].IP };
        return
        [
            new(0, OpCode.Move, pending, Memory(evidence.Pending))
                { NativeAddress = evidence.Body[0].IP, IntegerBitWidth = 8 },
            new(1, OpCode.CheckEqual, disabled, pending, new Immediate(0))
                { NativeAddress = evidence.Body[0].IP, IntegerBitWidth = 8 },
            new(2, OpCode.ConditionalJump, returned, disabled) { NativeAddress = evidence.Body[1].IP },
            new(3, OpCode.Move, current, Memory(evidence.Current)) { NativeAddress = evidence.Body[2].IP },
            new(4, OpCode.Move, baseline, Memory(evidence.Baseline)) { NativeAddress = evidence.Body[3].IP },
            new(5, OpCode.FloatSubtract, delta, current, baseline, new Immediate(64)) { NativeAddress = evidence.Body[3].IP },
            new(6, OpCode.Move, Memory(evidence.Pending), new Immediate(0)) { NativeAddress = evidence.Body[4].IP },
            new(7, OpCode.Move, total, Memory(evidence.Total)) { NativeAddress = evidence.Body[5].IP },
            new(8, OpCode.FloatAdd, sum, delta, total, new Immediate(64)) { NativeAddress = evidence.Body[5].IP },
            new(9, OpCode.Move, Memory(evidence.Total), sum) { NativeAddress = evidence.Body[6].IP },
            returned,
        ];

        ISIL.MemoryOperand Memory(FieldAnalysisContext field) => new(new Register(null, "rcx"), null, field.Offset);
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
                method.GetExtraData<Evidence>(EvidenceKey) is not { } admitted || Find(method) is not { } current ||
                admitted.Native != current.Native || !admitted.Body.SequenceEqual(current.Body) ||
                !admitted.Input.SequenceEqual(current.Input) ||
                !new[] { admitted.Pending, admitted.Current, admitted.Baseline, admitted.Total }
                    .SequenceEqual([current.Pending, current.Current, current.Baseline, current.Total]) ||
                !TryGraph(method, out var guard, out var update, out var returnBlock) ||
                method.ParameterLocals.Where(local => local.IsThis).ToArray() is not [var owner] ||
                owner.IsMethodInfo || owner.Register.Version != -1 || !ReferenceEquals(owner.Type, method.DeclaringType) ||
                method.ParameterOperands is not [Register incoming, _] || incoming.Name != "rcx" ||
                incoming.Number != owner.Register.Number || guard.Instructions is not
                    [var pendingRead, var comparison, var branch] || update.Instructions is not
                    [var currentRead, var baselineRead, var subtraction, var reset, var totalRead, var addition, var store] ||
                returnBlock.Instructions is not [var returned]) return false;
            var operations = new[] { pendingRead, comparison, branch, currentRead, baselineRead,
                subtraction, reset, totalRead, addition, store, returned };
            var indices = new[] { 0, 0, 1, 2, 3, 3, 4, 5, 5, 6, 7 };
            if (operations.Where((operation, index) => operation.NativeAddress != current.Body[indices[index]].IP ||
                    operation.CallSemantics != CallSemantics.Direct || operation.IntegerBitWidth != (index < 2 ? 8 : 0)).Any() ||
                !Read(pendingRead, current.Pending, out var pending) ||
                comparison is not { OpCode: OpCode.CheckEqual,
                    Operands: [LocalVariable disabled, LocalVariable compared, Immediate { Value: 0 }] } ||
                !ReferenceEquals(compared, pending) || !ReferenceEquals(disabled.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
                branch is not { OpCode: OpCode.ConditionalJump, Operands: [var target, LocalVariable condition] } ||
                !ReferenceEquals(condition, disabled) || !TargetsReturn(target, returnBlock) ||
                !Read(currentRead, current.Current, out var value) || !Read(baselineRead, current.Baseline, out var baseline) ||
                !Arithmetic(subtraction, true, value, baseline, out var delta) ||
                reset is not { OpCode: OpCode.Move, Operands: [FieldReference flag, Immediate { Value: 0 }] } ||
                !Field(flag, current.Pending) || !Read(totalRead, current.Total, out var total) ||
                !Arithmetic(addition, false, delta, total, out var sum) ||
                store is not { OpCode: OpCode.Move, Operands: [FieldReference destination, LocalVariable stored] } ||
                !Field(destination, current.Total) || !ReferenceEquals(stored, sum) ||
                returned.OpCode != OpCode.Return || returned.Operands.Count != 0 ||
                new[] { pending, disabled, value, baseline, delta, total, sum }.Distinct().Count() != 7 ||
                operations.Any(operation => operation.Operands.OfType<LocalVariable>().Any(method.ParameterLocals.Contains)))
                return false;
            return true;

            bool Field(FieldReference access, FieldAnalysisContext expected) =>
                ReferenceEquals(access.Field, expected) && access.Offset == expected.Offset && ReferenceEquals(access.Local, owner);

            bool Read(Instruction instruction, FieldAnalysisContext field, out LocalVariable result)
            {
                result = null!;
                if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable captured, FieldReference access] } ||
                    !Field(access, field) || !ReferenceEquals(captured.Type, field.FieldType)) return false;
                result = captured;
                return true;
            }

            bool Arithmetic(Instruction instruction, bool subtract, LocalVariable left, LocalVariable right,
                out LocalVariable result)
            {
                result = null!;
                if (!FloatAddSubtract.TryGet(instruction, out var arithmetic) || arithmetic != new FloatAddSubtract(64, subtract) ||
                    instruction.Operands is not [LocalVariable calculated, LocalVariable first, LocalVariable second, _] ||
                    !ReferenceEquals(first, left) || !ReferenceEquals(second, right) ||
                    !ReferenceEquals(calculated.Type, method.AppContext.SystemTypes.SystemDoubleType)) return false;
                result = calculated;
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryGraph(MethodAnalysisContext method, out Block guard, out Block update, out Block returned)
    {
        guard = update = returned = null!;
        if (method.ControlFlowGraph is not { } graph || graph.Blocks.Count != 5 ||
            graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.EntryBlock.Predecessors.Count != 0 || graph.ExitBlock.Successors.Count != 0 ||
            graph.EntryBlock.Successors is not [var condition] || condition.Successors.Count != 2 ||
            !ReferenceEquals(graph.Blocks.FirstOrDefault(block => !ReferenceEquals(block, graph.EntryBlock) &&
                !ReferenceEquals(block, graph.ExitBlock)), condition) ||
            condition.Instructions.LastOrDefault() is not { OpCode: OpCode.ConditionalJump, Operands: [var target, _] })
            return false;
        var exit = target is Block block ? block : target is Instruction instruction ? graph.FindBlockByInstruction(instruction) : null;
        if (exit == null || !TargetsReturn(target, exit) || condition.Successors.Count(candidate => ReferenceEquals(candidate, exit)) != 1 ||
            condition.Successors.Single(candidate => !ReferenceEquals(candidate, exit)) is not { } body ||
            condition.Predecessors is not [var entry] || !ReferenceEquals(entry, graph.EntryBlock) ||
            body.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, condition) ||
            body.Successors is not [var continuation] || !ReferenceEquals(continuation, exit) ||
            exit.Predecessors.Count != 2 || exit.Predecessors.Count(candidate => ReferenceEquals(candidate, condition)) != 1 ||
            exit.Predecessors.Count(candidate => ReferenceEquals(candidate, body)) != 1 ||
            exit.Successors is not [var sentinel] || !ReferenceEquals(sentinel, graph.ExitBlock) ||
            graph.ExitBlock.Predecessors is not [var final] || !ReferenceEquals(final, exit) ||
            new[] { graph.EntryBlock, condition, body, exit, graph.ExitBlock }.Distinct().Count() != 5 ||
            new[] { graph.EntryBlock, condition, body, exit, graph.ExitBlock }
                .Any(expected => graph.Blocks.Count(candidate => ReferenceEquals(candidate, expected)) != 1)) return false;
        guard = condition; update = body; returned = exit;
        return true;
    }

    private static bool TargetsReturn(IOperand target, Block block) => ReferenceEquals(target, block) ||
        target is Instruction instruction && ReferenceEquals(block.Instructions.FirstOrDefault(), instruction);

    private static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!Signature(method) || X64NativeInstructionReader.ReadFramelessBody(method, 8, 64) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null) return null;
            var pending = Field(shape.Pending, false, true);
            var current = Field(shape.Current, true, false);
            var baseline = Field(shape.Baseline, true, false);
            var total = Field(shape.Total, true, true);
            if (pending == null || current == null || baseline == null || total == null ||
                new[] { pending, current, baseline, total }.Distinct().Count() != 4) return null;
            return new(body, shape, pending, current, baseline, total, Snapshot(method));

            FieldAnalysisContext? Field(int offset, bool floating, bool written)
            {
                if (method.DeclaringType!.Fields.Where(candidate => candidate.Offset == offset).ToArray() is not [var field] ||
                    !ReferenceEquals(field.DeclaringType, method.DeclaringType) || field.Name != field.DefaultName ||
                    written && (field.Attributes & FieldAttributes.InitOnly) != 0 ||
                    !ReferenceEquals(field.FieldType, floating ? method.AppContext.SystemTypes.SystemDoubleType : method.AppContext.SystemTypes.SystemBooleanType))
                    return null;
                var access = new FieldReference(field, new LocalVariable("proved-owner", new Register(null, "rcx"), method.DeclaringType), offset);
                return (floating ? NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, 64) :
                    NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 8)) ? field : null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool Signature(MethodAnalysisContext method) =>
        X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) &&
        method.DeclaringType is { Definition: { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner &&
        NullCheckedCall.IsReferenceClass(owner) && owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
        owner.Fields.Count == owner.Definition.FieldCount && owner.Fields.All(field => field.BackingData?.Field.RawFieldType != null) &&
        method.Definition is { GenericContainer: null, parameterCount: 0, IsUnmanagedCallersOnly: false,
            InternalParameterData: [], RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) && method.Parameters.Count == 0 &&
        !method.IsStatic && !method.IsVirtual && method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.GenericParameters.Count == 0 && ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) && !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        new X64CallingConventionResolver().ResolveForParameters(method) is [Register { Name: "rcx" }, Register { Name: "rdx" }];

    private static object[] Snapshot(MethodAnalysisContext method)
    {
        var definition = method.Definition!;
        var owner = method.DeclaringType!;
        var type = owner.Definition!;
        var values = new List<object>
        {
            method.Name, method.Attributes, method.ImplAttributes, method.ReturnType, method.UnderlyingPointer,
            definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx.Value, definition.declaringTypeIdx.Value, definition.parameterStart.Value,
            definition.parameterCount, definition.genericContainerIndex.Value,
            owner, owner.DeclaringAssembly, owner.Name, owner.Namespace, owner.Attributes,
            owner.BaseType ?? (object)"no-base", owner.DeclaringType ?? (object)"no-enclosing-type",
            type.NameIndex, type.NamespaceIndex, type.Token, type.Flags, type.Bitfield, type.ByvalTypeIndex.Value,
            type.ParentIndex.Value, type.DeclaringTypeIndex.Value, type.GenericContainerIndex.Value,
            type.RawSizes.instance_size, type.FieldCount,
        };
        Raw(definition.RawReturnType!); Raw(type.RawType);
        if (type.RawBaseType is { } rawBase) Raw(rawBase);
        foreach (var field in owner.Fields)
        {
            var raw = field.BackingData!.Field;
            values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType,
                raw.nameIndex, raw.token, raw.typeIndex.Value]);
            Raw(raw.RawFieldType!);
        }
        return values.ToArray();

        void Raw(Il2CppType raw) => values.AddRange([raw.Bits, raw.Datapoint, raw.Data.Dummy,
            raw.Attrs, raw.Type, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType]);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 8 || body.Any(native => native.IsInvalid || native.CodeSize != CodeSize.Code64 ||
                native.HasLockPrefix || native.HasRepPrefix || native.HasRepnePrefix || native.SegmentPrefix != NativeRegister.None) ||
            body.Where((native, index) => index > 0 && native.IP != body[index - 1].NextIP).Any() ||
            body[0] is not { Code: Code.Cmp_rm8_imm8, Op1Kind: OpKind.Immediate8, Immediate8: 0 } || !Memory(body[0], 0, 1) ||
            body[1].Code is not (Code.Je_rel8_64 or Code.Je_rel32_64) || body[1].Op0Kind != OpKind.NearBranch64 ||
            body[1].NearBranchTarget != body[7].IP || body[1].OpCount != 1 ||
            body[2].Code != Code.Movsd_xmm_xmmm64 || !Memory(body[2], 1, 8) ||
            body[2].Op0Kind != OpKind.Register || body[2].Op0Register is < NativeRegister.XMM0 or > NativeRegister.XMM5 ||
            body[3].Code != Code.Subsd_xmm_xmmm64 || !Memory(body[3], 1, 8) ||
            body[3].Op0Kind != OpKind.Register || body[3].Op0Register != body[2].Op0Register ||
            body[4] is not { Code: Code.Mov_rm8_imm8, Op1Kind: OpKind.Immediate8, Immediate8: 0 } || !Memory(body[4], 0, 1) ||
            body[4].MemoryDisplacement64 != body[0].MemoryDisplacement64 ||
            body[5].Code != Code.Addsd_xmm_xmmm64 || !Memory(body[5], 1, 8) ||
            body[5].Op0Kind != OpKind.Register || body[5].Op0Register != body[2].Op0Register ||
            body[6].Code != Code.Movsd_xmmm64_xmm || !Memory(body[6], 0, 8) ||
            body[6].Op1Kind != OpKind.Register || body[6].Op1Register != body[2].Op0Register ||
            body[6].MemoryDisplacement64 != body[5].MemoryDisplacement64 ||
            body[7] is not { Code: Code.Retnq, OpCount: 0, Length: 1 } ||
            new[] { body[0].MemoryDisplacement64, body[2].MemoryDisplacement64,
                body[3].MemoryDisplacement64, body[5].MemoryDisplacement64 }.Distinct().Count() != 4)
            return null;
        return new((int)body[0].MemoryDisplacement64, (int)body[2].MemoryDisplacement64,
            (int)body[3].MemoryDisplacement64, (int)body[5].MemoryDisplacement64, body[2].Op0Register);

        static bool Memory(NativeInstruction native, int operand, int width) =>
            native.OpCount == 2 && native.GetOpKind(operand) == OpKind.Memory &&
            native.MemoryBase == NativeRegister.RCX && native.MemoryIndex == NativeRegister.None &&
            native.MemoryIndexScale == 1 && native.MemorySize.GetSize() == width &&
            native.MemoryDisplacement64 is >= 16 and < 4096 && native.MemoryDisplacement64 % (ulong)width == 0;
    }
}
