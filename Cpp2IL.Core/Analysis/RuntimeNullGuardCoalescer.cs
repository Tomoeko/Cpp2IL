using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Replaces an exact runtime null-check diamond with a managed operation that retains the check.
/// Runs in SSA after typed copy propagation. It does not reconstruct exception allocation.
/// </summary>
internal static class RuntimeNullGuardCoalescer
{
    private const string SetOptionAttribute = "Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute";

    internal sealed record FieldReadEvidence(Instruction Load, FieldReference Access, LocalVariable Receiver,
        FieldAnalysisContext Field, TypeAnalysisContext Owner, TypeAnalysisContext ValueType, int Offset,
        FieldAttributes Attributes, bool RequireNativeBinding)
    {
        internal bool IsValidFor(MethodAnalysisContext method)
        {
            if (method.ControlFlowGraph?.Instructions.Contains(Load) != true ||
                Load is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, FieldReference access] } ||
                !ReferenceEquals(access, Access) || !ReferenceEquals(access.Local, Receiver) ||
                !ReferenceEquals(access.Field, Field) || !ReferenceEquals(Receiver.Type, Owner) ||
                !ReferenceEquals(Field.DeclaringType, Owner) || !ReferenceEquals(Field.FieldType, ValueType) ||
                !ReferenceEquals(destination.Type, ValueType) || Field.Attributes != Attributes ||
                Field.IsStatic || access.Offset != Offset || Field.Offset != Offset ||
                RequireNativeBinding && !HasUnchangedNativeField(access))
                return false;
            if (!method.ParameterLocals.Contains(Receiver))
                return true;
            if (Receiver.IsThis)
                return !method.IsStatic && ReferenceEquals(method.DeclaringType, Owner);
            var skipThis = method.IsStatic ? 0 : 1;
            var matches = Enumerable.Range(0, method.Parameters.Count).Where(index =>
                index + skipThis < method.ParameterOperands.Count &&
                method.ParameterOperands[index + skipThis] is Register register &&
                register.Number == Receiver.Register.Number && Receiver.Register.Version == -1).ToArray();
            if (matches.Length != 1)
                return false;
            var parameter = method.Parameters[matches[0]];
            return !parameter.IsRef && ReferenceEquals(parameter.ParameterType, Owner) &&
                   ReferenceEquals(parameter.DefaultParameterType, Owner);
        }
    }

    public static int Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            method.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            method.AppContext.UnityVersion.ToString() != "2021.3.35f1" || HasOutputOptions(method))
            return 0;
        return RunCore(method, instruction => instruction is
        {
            OpCode: OpCode.RuntimeNullThrow, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [RuntimeNullThrowEvidence evidence],
        } && evidence.IsValidFor(method.AppContext), HasUnchangedNativeSignature,
            HasUnchangedNativeField, true);
    }

    // Graph-only tests supply an intrinsic predicate and explicit injected managed signatures.
    // Production additionally requires native helper evidence and original metadata binding.
    internal static int Run(MethodAnalysisContext method, Func<Instruction, bool> provesRuntimeNullThrow)
        => RunCore(method, provesRuntimeNullThrow, _ => true, _ => true, false);

    private static int RunCore(MethodAnalysisContext method, Func<Instruction, bool> provesRuntimeNullThrow,
        Func<MethodAnalysisContext, bool> provesNativeTarget,
        Func<FieldReference, bool> provesNativeField, bool requireNativeFieldBinding)
    {
        var graph = method.ControlFlowGraph!;
        var changed = 0;
        while (TryRewriteOne(method, graph, provesRuntimeNullThrow, provesNativeTarget,
                   provesNativeField, requireNativeFieldBinding))
        {
            changed++;
            graph.RemoveUnreachableBlocks();
            DeadCodeEliminator.Run(graph);
            method.DominatorInfo = new DominatorInfo(graph);
        }
        return changed;
    }

    private static bool TryRewriteOne(MethodAnalysisContext method, ISILControlFlowGraph graph,
        Func<Instruction, bool> provesRuntimeNullThrow, Func<MethodAnalysisContext, bool> provesNativeTarget,
        Func<FieldReference, bool> provesNativeField, bool requireNativeFieldBinding)
    {
        var definitions = new Dictionary<LocalVariable, (Block Block, Instruction Instruction)>();
        foreach (var block in graph.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Destination is LocalVariable destination)
                {
                    if (definitions.ContainsKey(destination) || method.ParameterLocals.Contains(destination))
                        return false; // Not SSA, or a parameter entry value was overwritten.
                    definitions.Add(destination, (block, instruction));
                }
        var dominators = new DominatorInfo(graph);
        var escapedSlots = new HashSet<int>(OperandEffects.LocalsWithMutableStorage(graph.Instructions)
            .Select(local => local.Register.Number));

        foreach (var guard in graph.Blocks.ToArray())
        {
            if (guard.Successors.Count != 2 || ReferenceEquals(guard.Successors[0], guard.Successors[1]) ||
                guard.Instructions.LastOrDefault() is not
                {
                    OpCode: OpCode.ConditionalJump, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [Block taken, LocalVariable condition],
                } branch || !guard.Successors.Contains(taken) ||
                !definitions.TryGetValue(condition, out var conditionDefinition) ||
                !Available(condition, guard, branch) || condition.Type != method.AppContext.SystemTypes.SystemBooleanType ||
                conditionDefinition.Instruction is not
                {
                    OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                    CallSemantics: CallSemantics.Direct, Operands.Count: 3,
                } comparison || !TryReceiver(comparison, out var receiver) ||
                receiver.Type == null || !NullCheckedCall.IsReferenceClass(receiver.Type) ||
                !Available(receiver, conditionDefinition.Block, comparison) ||
                !Available(receiver, guard, branch) || escapedSlots.Contains(receiver.Register.Number))
                continue;

            var other = guard.Successors.Single(successor => !ReferenceEquals(successor, taken));
            var nullEntry = comparison.OpCode == OpCode.CheckEqual ? taken : other;
            var callEntry = comparison.OpCode == OpCode.CheckEqual ? other : taken;
            if (!NullArmIsExclusive(nullEntry, guard) ||
                !TryGuardedOperation(callEntry, guard, receiver, out var operation, out var fieldAccess))
                continue;

            // Neither the operation nor its pure setup moves. The null input takes the same
            // path, where callvirt or ldfld performs the receiver check.
            if (fieldAccess == null)
                operation.CallSemantics = CallSemantics.NullCheckedInstance;
            else
                method.NullCheckedFieldReads.Add(new FieldReadEvidence(operation, fieldAccess, receiver,
                    fieldAccess.Field, fieldAccess.Field.DeclaringType, fieldAccess.Field.FieldType,
                    fieldAccess.Offset, fieldAccess.Field.Attributes, requireNativeFieldBinding));
            branch.OpCode = OpCode.Jump;
            branch.SetOperands(callEntry);
            guard.Successors.Remove(nullEntry);
            nullEntry.Predecessors.Remove(guard); // This arm has no phi nodes and exactly this edge.
            guard.CalculateBlockType();
            return true;
        }
        return false;

        bool Available(LocalVariable local, Block block, Instruction instruction)
        {
            if (escapedSlots.Contains(local.Register.Number))
                return false;
            if (!definitions.TryGetValue(local, out var definition))
                return method.ParameterLocals.Contains(local);
            return ReferenceEquals(definition.Block, block)
                ? block.Instructions.IndexOf(definition.Instruction) < block.Instructions.IndexOf(instruction)
                : dominators.Dominates(definition.Block, block);
        }

        bool NullArmIsExclusive(Block entry, Block predecessor)
        {
            var seen = new HashSet<Block> { predecessor };
            while (true)
            {
                if (entry == graph.EntryBlock || entry == graph.ExitBlock || !seen.Add(entry) ||
                    entry.Predecessors.Count != 1 || !ReferenceEquals(entry.Predecessors[0], predecessor))
                    return false;
                var active = entry.Instructions.Where(i => i.OpCode != OpCode.Nop || i.Operands.Count != 0 ||
                    i.IntegerBitWidth != 0 || i.CallSemantics != CallSemantics.Direct).ToArray();
                if (active.Length == 1 && active[0].OpCode == OpCode.RuntimeNullThrow && provesRuntimeNullThrow(active[0]))
                    return entry.Successors.Count == 1 && ReferenceEquals(entry.Successors[0], graph.ExitBlock);
                if (active is not [{ OpCode: OpCode.Jump, IntegerBitWidth: 0,
                        CallSemantics: CallSemantics.Direct, Operands: [Block next] }] ||
                    entry.Successors.Count != 1 || !ReferenceEquals(entry.Successors[0], next))
                    return false;
                predecessor = entry;
                entry = next;
            }
        }

        bool TryGuardedOperation(Block entry, Block predecessor, LocalVariable receiver,
            out Instruction operation, out FieldReference? fieldAccess)
        {
            operation = null!;
            fieldAccess = null;
            var seen = new HashSet<Block> { predecessor };
            while (true)
            {
                if (entry == graph.EntryBlock || entry == graph.ExitBlock || !seen.Add(entry) ||
                    entry.Predecessors.Count != 1 || !ReferenceEquals(entry.Predecessors[0], predecessor))
                    return false;
                foreach (var instruction in entry.Instructions)
                {
                    if (instruction.CallSemantics != CallSemantics.Direct)
                        return false;
                    if (instruction.IsCall)
                    {
                        if (!NullCheckedCall.TryGet(instruction, out var target, out var calledReceiver) ||
                            !provesNativeTarget(target) ||
                            !ReferenceEquals(target.AppContext, method.AppContext) ||
                            !ReferenceEquals(calledReceiver, receiver) ||
                            OperandEffects.ReadLocals(instruction).Any(local => !Available(local, entry, instruction)))
                            return false;
                        operation = instruction;
                        return true; // Instructions after this invocation are untouched.
                    }
                    if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                            Operands: [LocalVariable fieldDestination, FieldReference access] } &&
                        ReferenceEquals(access.Local, receiver) &&
                        receiver.Type != null && NullCheckedCall.IsReferenceClass(receiver.Type) &&
                        ReferenceEquals(access.Field.DeclaringType, receiver.Type) &&
                        !access.Field.IsStatic && access.Offset >= 0 && access.Offset == access.Field.Offset &&
                        ReferenceEquals(fieldDestination.Type, access.Field.FieldType) &&
                        provesNativeField(access))
                    {
                        operation = instruction;
                        fieldAccess = access;
                        return true; // The first effect is the managed field read and null check.
                    }
                    if (instruction.OpCode == OpCode.Nop && instruction.IntegerBitWidth == 0 && instruction.Operands.Count == 0)
                        continue;
                    if (instruction is { OpCode: OpCode.Jump, IntegerBitWidth: 0, Operands: [Block targetBlock] } &&
                        ReferenceEquals(instruction, entry.Instructions[^1]) && entry.Successors.Count == 1 &&
                        ReferenceEquals(entry.Successors[0], targetBlock))
                        continue;
                    if (instruction is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                            Operands: [LocalVariable destination, var source] } || destination.Type == null ||
                        escapedSlots.Contains(destination.Register.Number) ||
                        !NullCheckedCall.CanLoadWithoutEffects(source, destination.Type) ||
                        source is LocalVariable read && !Available(read, entry, instruction))
                        return false;
                }
                if (entry.Successors.Count != 1)
                    return false;
                predecessor = entry;
                entry = entry.Successors[0];
            }
        }
    }

    private static bool TryReceiver(Instruction comparison, out LocalVariable receiver)
    {
        receiver = null!;
        if (comparison.Operands is [_, LocalVariable left, Immediate { Value: 0 }])
            receiver = left;
        else if (comparison.Operands is [_, Immediate { Value: 0 }, LocalVariable right])
            receiver = right;
        return receiver != null;
    }

    internal static bool HasUnchangedNativeSignature(MethodAnalysisContext target)
    {
        if (target.Definition is not { GenericContainer: null } definition ||
            target.DeclaringType?.Definition is not { GenericContainer: null } owner ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            definition.parameterCount != target.Parameters.Count ||
            (definition.InternalParameterData?.Length ?? 0) != target.Parameters.Count ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            target.UnderlyingPointer == 0 ||
            !target.AppContext.MethodsByAddress.TryGetValue(target.UnderlyingPointer, out var binding) ||
            binding.Count != 1 || !ReferenceEquals(binding[0], target))
            return false;
        for (var index = 0; index < target.Parameters.Count; index++)
            if (!ReferenceEquals(target.Parameters[index].Definition, definition.InternalParameterData![index]) ||
                target.Parameters[index].Definition?.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
        return true;
    }

    private static bool HasUnchangedNativeField(FieldReference access)
    {
        var field = access.Field;
        var owner = field.DeclaringType;
        return field.Name == field.DefaultName &&
               ReferenceEquals(field.FieldType, owner.AppContext.SystemTypes.SystemInt32Type) &&
               owner.Fields.Contains(field) && NullCheckedCall.IsReferenceClass(owner) &&
               NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32);
    }

    private static bool HasOutputOptions(MethodAnalysisContext method)
    {
        if (HasOption(method) || method.DeclaringType is { } owner && HasOption(owner.DeclaringAssembly))
            return true;
        for (var type = method.DeclaringType; type != null; type = type.DeclaringType)
            if (HasOption(type))
                return true;
        return false;

        static bool HasOption(HasCustomAttributes context) => context.HasCustomAttributeWithFullName(SetOptionAttribute) ||
            context.AttributeTypes?.Any(type => type.Type == LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
                type.AsClass().FullName == SetOptionAttribute) == true;
    }
}
