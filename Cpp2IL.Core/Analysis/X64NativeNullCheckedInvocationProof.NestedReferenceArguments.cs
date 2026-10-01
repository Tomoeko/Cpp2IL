using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private sealed record NestedReferenceFieldArgument(Origin Owner, Instruction Read,
        FieldAnalysisContext Field, int Offset, TypeAnalysisContext Type);

    private sealed record NestedReferenceGuard(NestedReferenceFieldArgument Argument,
        Instruction Comparison, Instruction Branch, Block Owner, Block NormalArm, Block NullArm,
        ulong ComparisonAddress, ulong BranchAddress, ValueKey ReceiverDeclarations);

    internal static bool IsNestedReferenceFieldArgumentCapture(MethodAnalysisContext caller,
        LocalVariable value, Instruction definition) => value.Type is { } type &&
        TryNestedReferenceFieldArgument(caller, value, definition, type, out _, definition);

    // Only one captured current-owner reference may own the nested read. This
    // does not extend reference receiver origins, incoming pointers or producers.
    private static bool TryNestedReferenceFieldArgument(MethodAnalysisContext caller, IOperand operand,
        Instruction use, TypeAnalysisContext type, out NestedReferenceFieldArgument argument,
        Instruction? capture = null)
    {
        argument = null!;
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable value && visited.Add(value))
        {
            if (!ReferenceEquals(value.Type, type) || !OrdinaryClass(type) || Escaped(caller, value)) return false;
            Instruction? definition = capture;
            if (capture == null && !ReachingDefinition(caller, value, use, out definition)) return false;
            capture = null;
            if (definition is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, LocalVariable copied] } && ReferenceEquals(destination, value))
            {
                operand = copied;
                use = definition;
                continue;
            }
            if (definition is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    NativeAddress: not null, Operands: [LocalVariable loaded, FieldReference access] } ||
                !ReferenceEquals(loaded, value) || access.Field.BackingData?.Field.RawFieldType is not { Data: not null } ||
                !ReferenceEquals(access.Field.FieldType, type) || access.Local.Type is not { } ownerType ||
                !TryReferenceFieldArgument(caller, access.Local, definition, ownerType, out var source) ||
                !ReferenceEquals(source.Origin.Type, ownerType) ||
                !ReferenceEquals(access.Field.DeclaringType, ownerType) || !AccessibleField(caller, access.Field) ||
                access.Offset != access.Field.Offset ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access)) return false;
            argument = new(source.Origin, definition, access.Field, access.Offset, type);
            return true;
        }
        return false;
    }

    private static bool BindNestedReferenceArgument(MethodAnalysisContext caller, NestedReferenceFieldArgument argument,
        NativeInstruction[] body, X64NativeInvocationValues values, ulong use, NativeRegister register)
    {
        if (argument.Read.NativeAddress is not { } address || !values.Dominates(address, use) ||
            argument.Read.Operands is not [LocalVariable loaded, FieldReference access] ||
            !ReferenceEquals(loaded.Type, argument.Type) || !ReferenceEquals(access.Field, argument.Field) ||
            access.Offset != argument.Offset || access.Offset != argument.Field.Offset ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ||
            !TryReferenceFieldArgument(caller, access.Local, argument.Read, argument.Owner.Type, out var source) ||
            source.Origin != argument.Owner || body.SingleOrDefault(native => native.IP == address) is not
                { Code: Code.Mov_r64_rm64, Op0Kind: OpKind.Register, Op1Kind: OpKind.Memory } load ||
            load.MemoryIndex != NativeRegister.None || load.MemorySize.GetSize() != 8 ||
            load.MemoryDisplacement64 != (ulong)argument.Offset ||
            !BindReceiver(caller, argument.Owner, body, values, address, load.MemoryBase)) return false;
        return values.Matches(use, register, 64, new(NativeRegister.None, address, load.Op0Register));
    }

    private static bool TryNestedReferenceDeclarations(MethodAnalysisContext target, int index,
        NestedReferenceFieldArgument argument, out ValueKey key)
    {
        key = null!;
        if (index >= target.Parameters.Count || argument.Owner.Field is not { } sourceField ||
            argument.Owner.Definition?.Destination is not LocalVariable source ||
            argument.Read.Destination is not LocalVariable payload || !ReferenceEquals(source.Type, argument.Owner.Type) ||
            !ReferenceEquals(payload.Type, argument.Type) || !OriginalParameter(target, index) ||
            target.Parameters[index].Definition is not { } parameter || parameter.RawType is not { Data: not null } ||
            !ReferenceEquals(target.Parameters[index].ParameterType, argument.Type) ||
            !ReferenceDeclarationKey(sourceField.DeclaringType, out var holder) ||
            !ReferenceDeclarationKey(argument.Owner.Type, out var owner) ||
            !ReferenceDeclarationKey(argument.Type, out var payloadType) ||
            !NestedFieldDeclaration(sourceField, out var sourceDeclaration) ||
            !NestedFieldDeclaration(argument.Field, out var payloadDeclaration)) return false;
        key = new("nested-reference-argument", index,
            [sourceDeclaration, payloadDeclaration, holder, owner, payloadType,
                new("parameter", parameter, [new("name-index", parameter.nameIndex, []),
                    new("type-index", parameter.typeIndex.Value, []), new("token", parameter.token, []),
                    new("attributes", target.Parameters[index].Attributes, []),
                    ReferenceRawTypeKey(parameter.RawType)])]);
        return true;
    }

    private static bool NestedFieldDeclaration(FieldAnalysisContext field, out ValueKey key)
    {
        key = null!;
        if (field.BackingData?.Field is not { RawFieldType.Data: not null } definition) return false;
        key = new("nested-field", field,
            [new("definition", definition, []), new("name-index", definition.nameIndex, []),
                new("type-index", definition.typeIndex.Value, []), new("token", definition.token, []),
                new("name", field.Name, []), new("attributes", field.Attributes, []),
                new("owner", field.DeclaringType, []), new("offset", field.Offset, []),
                new("type", field.FieldType, []), ReferenceRawTypeKey(definition.RawFieldType)]);
        return true;
    }

    private static bool TryNestedReferenceGuard(MethodAnalysisContext caller, Instruction invocation, Origin receiver,
        Argument[] arguments, Instruction consumerComparison, Instruction consumerBranch, Block consumerOwner,
        Block consumerNormal, NativeInstruction[] body, X64NativeInvocationValues values, ulong nullCall,
        RuntimeNullThrowEvidence helper, out NestedReferenceGuard? guard)
    {
        guard = null;
        var nested = arguments.Select(argument => argument.NestedReference).OfType<NestedReferenceFieldArgument>().ToArray();
        if (nested.Length == 0) return true;
        if (arguments.Length != 1 || nested.Length != 1 || caller.Parameters.Count != 0 ||
            caller.IsStatic || invocation.OpCode != OpCode.CallVoid || receiver.Field == null ||
            receiver.SourceEntry != -1 || receiver.Definition == null || body.Length != 13) return false;
        var argument = nested[0];
        var graph = caller.ControlFlowGraph!;
        var comparisons = graph.Instructions.Where(instruction => instruction is
            { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                Operands: [LocalVariable, LocalVariable, Immediate { Value: 0 }] } &&
            !ReferenceEquals(instruction, consumerComparison)).ToArray();
        if (comparisons is not [var comparison] || comparison.Operands[1] is not LocalVariable source ||
            !TryOrigin(caller, source, comparison, out var sourceOrigin) || sourceOrigin != argument.Owner ||
            graph.FindBlockByInstruction(comparison) is not { } sourceOwner ||
            sourceOwner.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop) is not
                { OpCode: OpCode.ConditionalJump } branch ||
            !ManagedGuard(caller, comparison, branch, source, out var owner, out var normal) ||
            comparison.NativeAddress is not { } comparisonIp || branch.NativeAddress is not { } branchIp ||
            !TryGuard(caller, body, values, argument.Owner, comparisonIp, branchIp, argument.Read,
                out var sourceNullCall, out var sourceHelper) || sourceNullCall != nullCall ||
            sourceHelper.NativeTarget != helper.NativeTarget ||
            !NestedNativeShape(caller, argument, receiver, body, values, invocation, comparisonIp, branchIp,
                consumerComparison.NativeAddress!.Value, consumerBranch.NativeAddress!.Value, nullCall)) return false;
        var nullArm = owner.Successors.Single(successor => !ReferenceEquals(successor, normal));
        if (!NestedFieldDeclaration(receiver.Field, out var receiverField) ||
            !ReferenceDeclarationKey(receiver.Type, out var receiverType)) return false;
        guard = new(argument, comparison, branch, owner, normal, nullArm, comparisonIp, branchIp,
            new("nested-consumer", receiver.Field, [receiverField, receiverType]));
        return NestedManagedShape(caller, guard, invocation, receiver, consumerBranch, consumerOwner, consumerNormal,
            consumerComparison.NativeAddress!.Value, false);
    }

    // Both independently proved guards share one nonreturn null arm. The only
    // read between them is the current-owner target capture. No user effect can
    // be postponed across the source check when its ldfld takes over that check.
    private static bool NestedNativeShape(MethodAnalysisContext caller, NestedReferenceFieldArgument argument,
        Origin receiver, NativeInstruction[] body, X64NativeInvocationValues values, Instruction invocation,
        ulong sourceComparison, ulong sourceBranch, ulong consumerComparison, ulong consumerBranch, ulong nullCall) =>
        body.Length == 13 && body[0] is { Code: Code.Sub_rm64_imm8, Op0Register: NativeRegister.RSP, Immediate8: 40 } &&
        body[1].IP == argument.Owner.Definition!.NativeAddress && body[1].Code == Code.Mov_r64_rm64 &&
        body[2].IP == sourceComparison && body[2].Code == Code.Test_rm64_r64 &&
        body[3].IP == sourceBranch && body[3].Mnemonic == Mnemonic.Je && body[3].NearBranchTarget == nullCall &&
        body[4].IP == receiver.Definition!.NativeAddress && body[4].Code == Code.Mov_r64_rm64 &&
        body[5].IP == consumerComparison && body[5].Code == Code.Test_rm64_r64 &&
        body[6].IP == consumerBranch && body[6].Mnemonic == Mnemonic.Je && body[6].NearBranchTarget == nullCall &&
        body[7].IP == argument.Read.NativeAddress && body[7].Code == Code.Mov_r64_rm64 &&
        body[8] is { Code: Code.Xor_r32_rm32, Op0Register: NativeRegister.R8D, Op1Register: NativeRegister.R8D } &&
        body[9] is { Code: Code.Add_rm64_imm8, Op0Register: NativeRegister.RSP, Immediate8: 40 } &&
        body[10].IP == invocation.NativeAddress && body[10].Code == Code.Jmp_rel32_64 &&
        body[11].IP == nullCall && body[11].Code == Code.Call_rel32_64 && body[12].Code == Code.Int3 &&
        BindReceiver(caller, argument.Owner, body, values, sourceComparison, body[2].Op0Register) &&
        BindReceiver(caller, receiver, body, values, consumerComparison, body[5].Op0Register) &&
        BindNestedReferenceArgument(caller, argument, body, values, invocation.NativeAddress!.Value, NativeRegister.RDX);

    private static bool NestedManagedShape(MethodAnalysisContext caller, NestedReferenceGuard guard,
        Instruction invocation, Origin receiver, Instruction consumerBranch, Block consumerOwner,
        Block consumerNormal, ulong consumerComparison, bool rewritten)
    {
        var graph = caller.ControlFlowGraph!;
        var callBlock = graph.FindBlockByInstruction(invocation);
        if (callBlock == null || !ReferenceEquals(callBlock, consumerNormal) ||
            !ReferenceEquals(guard.NormalArm, consumerOwner) || ReferenceEquals(guard.Owner, consumerOwner) ||
            ReferenceEquals(consumerOwner, callBlock) ||
            graph.EntryBlock.Successors is not [var entry] || !ReferenceEquals(entry, guard.Owner) ||
            guard.Owner.Predecessors is not [var sourcePredecessor] || !ReferenceEquals(sourcePredecessor, graph.EntryBlock) ||
            callBlock.Predecessors is not [var callPredecessor] || !ReferenceEquals(callPredecessor, consumerOwner) ||
            !ReferenceEquals(graph.Blocks.FirstOrDefault(block => block != graph.EntryBlock && block != graph.ExitBlock), guard.Owner) ||
            graph.FindBlockByInstruction(guard.Argument.Owner.Definition!) != guard.Owner ||
            graph.FindBlockByInstruction(receiver.Definition!) != consumerOwner ||
            graph.FindBlockByInstruction(guard.Argument.Read) != callBlock ||
            callBlock.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock)) return false;
        var allowed = new HashSet<Instruction> { guard.Argument.Owner.Definition!, receiver.Definition!,
            guard.Argument.Read, guard.Comparison, guard.Branch, consumerBranch, invocation };
        foreach (var operation in graph.Instructions)
        {
            if (allowed.Contains(operation)) continue;
            if (operation.OpCode == OpCode.Nop && operation.Operands.Count == 0 && operation.CallSemantics == CallSemantics.Direct)
                continue;
            if (operation is { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands.Count: 0 } && operation.NativeAddress == invocation.NativeAddress &&
                ReferenceEquals(callBlock.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop), operation))
                continue;
            if (!rewritten && operation is { OpCode: OpCode.RuntimeNullThrow } &&
                ReferenceEquals(graph.FindBlockByInstruction(operation), guard.NullArm)) continue;
            if (operation is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                    CallSemantics: CallSemantics.Direct, Operands: [LocalVariable, LocalVariable, Immediate { Value: 0 }] } &&
                operation.NativeAddress == consumerComparison && operation.Operands[1] is LocalVariable checkedReceiver &&
                TryOrigin(caller, checkedReceiver, operation, out var checkedOrigin) && checkedOrigin == receiver) continue;
            if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, LocalVariable source] } &&
                source.Type is { } sourceType && ReferenceEquals(sourceType, destination.Type) && OrdinaryClass(sourceType)) continue;
            return false;
        }
        return graph.Blocks.All(block => block == graph.EntryBlock || block == graph.ExitBlock ||
            block == guard.Owner || block == consumerOwner || block == callBlock || !rewritten && block == guard.NullArm);
    }

    private static void RewriteNestedReferenceGuard(NestedReferenceGuard guard, List<Action> undo)
    {
        var operands = guard.Branch.Operands.ToArray();
        var code = guard.Branch.OpCode;
        var type = guard.Owner.BlockType;
        var successors = guard.Owner.Successors.ToArray();
        var predecessors = guard.NullArm.Predecessors.ToArray();
        undo.Add(() =>
        {
            guard.Branch.OpCode = code;
            guard.Branch.SetOperands(operands.ToList());
            guard.Owner.BlockType = type;
            guard.Owner.Successors.Clear(); guard.Owner.Successors.AddRange(successors);
            guard.NullArm.Predecessors.Clear(); guard.NullArm.Predecessors.AddRange(predecessors);
        });
        guard.Branch.OpCode = OpCode.Jump;
        guard.Branch.SetOperands(guard.NormalArm);
        guard.Owner.Successors.Remove(guard.NullArm);
        guard.NullArm.Predecessors.Remove(guard.Owner);
        guard.Owner.CalculateBlockType();
    }

    private static bool NestedReferenceGuardRetained(MethodAnalysisContext caller, Site site,
        NativeInstruction[] body, X64NativeInvocationValues values)
    {
        if (site.NestedGuard is not { } guard) return site.Arguments.All(argument => argument.NestedReference == null);
        var graph = caller.ControlFlowGraph!;
        if (site.Arguments is not [{ NestedReference: { } argument }] || argument != guard.Argument ||
            !NestedFieldDeclaration(site.Receiver.Field!, out var receiverField) ||
            !ReferenceDeclarationKey(site.Receiver.Type, out var receiverType) ||
            !SameKey(guard.ReceiverDeclarations, new("nested-consumer", site.Receiver.Field, [receiverField, receiverType])) ||
            guard.Branch is not { OpCode: OpCode.Jump, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [var target] } || guard.Branch.NativeAddress != guard.BranchAddress ||
            graph.FindBlockByInstruction(guard.Branch) != guard.Owner || TargetBlock(graph, target) != guard.NormalArm ||
            guard.Owner.Successors is not [var successor] || successor != guard.NormalArm ||
            guard.NormalArm.Predecessors is not [var predecessor] || predecessor != guard.Owner ||
            !ReferenceEquals(guard.Owner.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop), guard.Branch) ||
            !TryGuard(caller, body, values, argument.Owner, guard.ComparisonAddress, guard.BranchAddress,
                argument.Read, out var nullCall, out _, site.Helper, site.NullCall) || nullCall != site.NullCall ||
            !NestedNativeShape(caller, argument, site.Receiver, body, values, site.Invocation,
                guard.ComparisonAddress, guard.BranchAddress, site.Comparison, site.Branch, site.NullCall)) return false;
        if (graph.Instructions.Contains(guard.Comparison) && guard.Comparison.OpCode != OpCode.Nop &&
            (guard.Comparison is not { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                CallSemantics: CallSemantics.Direct, Operands: [LocalVariable condition, LocalVariable checkedSource,
                    Immediate { Value: 0 }] } || !ReferenceEquals(condition.Type, caller.AppContext.SystemTypes.SystemBooleanType) ||
             guard.Comparison.NativeAddress != guard.ComparisonAddress ||
             !TryOrigin(caller, checkedSource, guard.Comparison, out var checkedOrigin) || checkedOrigin != argument.Owner))
            return false;
        return NestedManagedShape(caller, guard, site.Invocation, site.Receiver, site.GuardBranch,
            site.GuardOwner, site.NormalArm, site.Comparison, true);
    }

    private static bool NestedReferenceArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        foreach (var site in sites.Where(site => site.NestedGuard != null))
        {
            var guard = site.NestedGuard!;
            if (guard.Argument.Owner.Definition!.Destination is not LocalVariable source ||
                guard.Argument.Read.Destination is not LocalVariable payload) return false;
            var copies = new Dictionary<LocalVariable, bool> { [source] = false, [payload] = true };
            foreach (var operation in caller.ControlFlowGraph!.Instructions)
            {
                if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable destination, LocalVariable original] } && copies.TryGetValue(original, out var leaf))
                {
                    if (!ReferenceEquals(destination.Type, original.Type) || Escaped(caller, destination)) return false;
                    copies[destination] = leaf;
                    continue;
                }
                foreach (var read in OperandEffects.ReadLocals(operation))
                    if (copies.TryGetValue(read, out var isPayload) &&
                        !(isPayload ? ReferenceEquals(operation, site.Invocation) && ReferenceEquals(operation.Operands[2], read) :
                            ReferenceEquals(operation, guard.Argument.Read) || ReferenceEquals(operation, guard.Comparison)))
                        return false;
            }
        }
        return true;
    }
}
