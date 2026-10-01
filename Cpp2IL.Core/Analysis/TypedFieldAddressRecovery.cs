using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>Projects an authenticated pointer calculation onto managed field storage.</summary>
internal static class TypedFieldAddressRecovery
{
    internal const string EvidenceKey = "x64-typed-field-address";
    private sealed record Binding(X64TypedFieldAddressProof.Proof Proof, object[] Graph,
        NativeEntryValueValidator.Result EntryValues);

    internal static bool HasEvidence(MethodAnalysisContext method)
        => NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Binding>(EvidenceKey) != null;

    internal static void Run(MethodAnalysisContext method)
    {
        if (method.GetExtraData<Binding>(EvidenceKey) != null ||
            X64TypedFieldAddressProof.Find(method, false) is not { } original)
            return;
        var operations = method.ControlFlowGraph!.Instructions.ToArray();
        var saved = operations.Select(operation => new
        {
            Operation = operation, operation.OpCode, operation.IntegerBitWidth,
            Operands = operation.Operands.ToArray()
        }).ToArray();
        var types = original.Sites.Select(site => (site.Pointer, site.Pointer.Type)).ToArray();
        var entryValues = method.GetExtraData<NativeEntryValueValidator.Result>(NativeEntryValueValidator.EvidenceKey);
        var resolvingRawCall = original.Sites.SelectMany(site => site.Calls)
            .Any(call => call.Operands[0] is not MethodAnalysisContext);
        var committed = false;
        try
        {
            foreach (var site in original.Sites)
            {
                site.Operation.OpCode = OpCode.Move;
                site.Operation.IntegerBitWidth = 0;
                site.Operation.SetOperands(site.Pointer,
                    new AddressOf(new FieldReference(site.Field, site.Owner, site.Field.Offset)));
                site.Pointer.Type = new ByRefTypeAnalysisContext(site.Field.FieldType);
                for (var index = 0; index < site.Calls.Length; index++)
                {
                    var call = site.Calls[index];
                    var target = site.Targets[index];
                    if (X64TypedFieldAddressProof.HasUnusedEnumResult(method, target, call, false))
                    {
                        call.OpCode = OpCode.CallVoid;
                        call.SetOperands(call.Operands.Where((_, operandIndex) => operandIndex != 1).ToList());
                        continue;
                    }
                    if (call.Operands[0] is MethodAnalysisContext) continue;
                    if (target.IsVoid)
                        call.SetOperands(target, site.Pointer);
                    else
                    {
                        var unresolved = operations.Where(operation => operation.OpCode == OpCode.UnresolvedValue).ToArray();
                        if (unresolved is not [{ Operands: [LocalVariable result, StringLiteral] } placeholder] ||
                            !ReferenceEquals(result.Type, target.ReturnType) ||
                            operations.Count(operation => ReferenceEquals(operation.Destination, result)) != 1 ||
                            operations.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, result)) != 1 ||
                            operations.SingleOrDefault(operation => operation.OpCode == OpCode.Return &&
                                operation.Operands is [LocalVariable value] && ReferenceEquals(value, result)) == null)
                            return;
                        call.OpCode = OpCode.Call;
                        call.SetOperands(target, result, site.Pointer);
                        placeholder.OpCode = OpCode.Nop;
                        placeholder.IntegerBitWidth = 0;
                        placeholder.SetOperands();
                    }
                }
            }
            if (X64TypedFieldAddressProof.Find(method, true) is not { } projected ||
                !original.Input.Matches(projected.Input) || !CompleteEntryReads(method))
                return;
            if (entryValues == null || entryValues.UnprovedValueCount != 0 && !resolvingRawCall)
                return;
            // Earlier SSA validation observed the raw ambiguous call's unused
            // ABI placeholders. Replace its result only after the complete
            // projected proof binds every actual call input and remaining read.
            // This is not a generic post-SSA entry-value inference pass.
            var retainedEntry = resolvingRawCall ? new NativeEntryValueValidator.Result(0) : entryValues;
            method.PutExtraData(NativeEntryValueValidator.EvidenceKey, retainedEntry);
            method.PutExtraData(EvidenceKey, new Binding(projected, GraphFacts(method), retainedEntry));
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                foreach (var state in saved)
                {
                    state.Operation.OpCode = state.OpCode;
                    state.Operation.IntegerBitWidth = state.IntegerBitWidth;
                    state.Operation.SetOperands(state.Operands.ToList());
                }
                foreach (var (pointer, type) in types) pointer.Type = type;
                method.PutExtraData(NativeEntryValueValidator.EvidenceKey, entryValues!);
            }
        }
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Binding>(EvidenceKey) is not { } binding ||
            X64TypedFieldAddressProof.Find(method, true) is not { } current ||
            !binding.Proof.Input.Matches(current.Input) ||
            !ReferenceEquals(binding.EntryValues,
                method.GetExtraData<NativeEntryValueValidator.Result>(NativeEntryValueValidator.EvidenceKey)) ||
            binding.EntryValues.UnprovedValueCount != 0 || !CompleteEntryReads(method) ||
            current.Sites.Length != binding.Proof.Sites.Length ||
            !binding.Graph.SequenceEqual(GraphFacts(method)))
            return false;
        for (var index = 0; index < current.Sites.Length; index++)
        {
            var first = binding.Proof.Sites[index];
            var second = current.Sites[index];
            if (!ReferenceEquals(first.Operation, second.Operation) || !ReferenceEquals(first.Pointer, second.Pointer) ||
                !ReferenceEquals(first.Owner, second.Owner) || !ReferenceEquals(first.Field, second.Field) ||
                first.Native != second.Native || !first.Calls.SequenceEqual(second.Calls) ||
                !first.Targets.SequenceEqual(second.Targets))
                return false;
        }
        return true;
    }

    internal static bool IsAdmittedAddress(MethodAnalysisContext method, FieldReference field)
        => method.GetExtraData<Binding>(EvidenceKey) is { } binding &&
           binding.Proof.Sites.Any(site => site.Operation.Operands is [_, AddressOf { Target: FieldReference candidate }] &&
               ReferenceEquals(field, candidate) && ReferenceEquals(field.Field, site.Field) &&
               ReferenceEquals(field.Local, site.Owner) && field.Offset == site.Field.Offset);

    // The closed native/managed proof already binds each permitted definition.
    // Require every surviving local read to reach one of those definitions or
    // the exact declared ABI parameter; no phantom incoming value is supplied.
    private static bool CompleteEntryReads(MethodAnalysisContext method)
    {
        var operations = method.ControlFlowGraph!.Instructions.ToArray();
        foreach (var local in operations.SelectMany(OperandEffects.ReadLocals).Distinct())
        {
            if (method.ParameterLocals.Contains(local))
            {
                if (local.IsThis && ReferenceEquals(local.Type, method.DeclaringType)) continue;
                if (LocalVariables.GetIncomingParameterIndex(method, local) is { } index &&
                    ReferenceEquals(local.Type, method.Parameters[index].ParameterType)) continue;
                return false;
            }
            if (operations.Count(operation => ReferenceEquals(operation.Destination, local)) != 1)
                return false;
        }
        return true;
    }

    private static object[] GraphFacts(MethodAnalysisContext method)
    {
        var facts = new List<object>();
        if (method.ControlFlowGraph is not { } graph) return [];
        facts.Add(graph);
        facts.Add(graph.EntryBlock);
        facts.Add(graph.ExitBlock);
        facts.Add(graph.Blocks.Count);
        foreach (var block in graph.Blocks)
        {
            facts.Add(block);
            facts.Add(block.BlockType);
            facts.Add(block.Predecessors.Count);
            facts.AddRange(block.Predecessors);
            facts.Add(block.Successors.Count);
            facts.AddRange(block.Successors);
            facts.Add(block.Instructions.Count);
            foreach (var operation in block.Instructions)
            {
                facts.Add(operation);
                facts.Add(operation.OpCode);
                facts.Add(operation.IntegerBitWidth);
                facts.Add(operation.CallSemantics);
                facts.Add(operation.NativeAddress ?? 0);
                facts.Add(operation.Operands.Count);
                foreach (var operand in operation.Operands) Operand(operand);
            }
        }
        return facts.ToArray();

        void Operand(IOperand operand)
        {
            // GenerateIl changes Block labels to that block's first ISIL
            // instruction. Those two representations denote the same target.
            if (operand is Block { Instructions.Count: > 0 } target)
                operand = target.Instructions[0];
            facts.Add(operand);
            switch (operand)
            {
                case LocalVariable local:
                    facts.Add(local.Register);
                    facts.Add(local.Type!);
                    facts.Add(local.IsThis);
                    facts.Add(local.IsMethodInfo);
                    facts.Add(local.IsReturn);
                    break;
                case FieldReference field:
                    facts.Add(field.Field);
                    facts.Add(field.Offset);
                    Operand(field.Local);
                    break;
                case AddressOf address:
                    Operand(address.Target);
                    break;
                case Immediate immediate:
                    facts.Add(immediate.Value);
                    break;
                case MemoryOperand memory:
                    facts.Add(memory.Addend);
                    facts.Add(memory.Scale);
                    if (memory.Base != null) Operand(memory.Base);
                    if (memory.Index != null) Operand(memory.Index);
                    break;
            }
        }
    }
}
