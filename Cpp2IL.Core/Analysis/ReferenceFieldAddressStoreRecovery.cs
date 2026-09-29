using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Replaces one proved native field address with its managed field operand.</summary>
internal static class ReferenceFieldAddressStoreRecovery
{
    private const string EvidenceKey = "x64-reference-field-address-store";
    private sealed record Binding(X64ReferenceFieldAddressStoreProof.Proof Proof,
        Instruction Capture, Instruction[] Increments, Instruction Store, Instruction Return,
        LocalVariable Receiver, LocalVariable Value);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(EvidenceKey, null!);
        if (X64ReferenceFieldAddressStoreProof.Find(method) is not { } proof ||
            LinearBody(method) is not { } block ||
            Active(block) is not { Length: >= 4 } active ||
            active.Length != proof.Increments.Count + 4 ||
            !IncomingReceiver(method, out var receiver) ||
            !Prefix(active.AsSpan(0, active.Length - 3).ToArray(), method, proof, receiver, true, out var capture, out var increments) ||
            active[^3] is not { OpCode: OpCode.Add, IntegerBitWidth: 64, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable pointer, LocalVariable owner, Immediate offset] } setup ||
            !ReferenceEquals(owner, receiver) || offset.Value != proof.Native.DestinationOffset ||
            setup.NativeAddress != proof.Native.AddressSetup || pointer.Register.Copy() != new Register(null, "rcx") ||
            active[^2] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [MemoryOperand { Base: LocalVariable baseLocal, Index: null, Addend: 0 }, LocalVariable stored] } store ||
            !ReferenceEquals(baseLocal, pointer) || !ReferenceEquals(stored, capture.Destination) ||
            store.NativeAddress != proof.Native.StoreAddress ||
            method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, pointer)) != 1 ||
            method.ControlFlowGraph.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, pointer)) != 1 ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands.Count: 0 } ret || ret.NativeAddress != proof.Native.TailAddress ||
            Mutable(method, receiver, stored, pointer))
            return;
        // Field typing can clear a redundant native width. The closed proof has
        // bound each INC32 to an unchanged Int32 field; retain that width for final validation.
        foreach (var increment in increments)
            increment.IntegerBitWidth = 32;
        store.SetOperand(0, new FieldReference(proof.Destination, receiver, proof.Destination.Offset));
        setup.OpCode = OpCode.Nop;
        setup.IntegerBitWidth = 0;
        setup.SetOperands();
        method.PutExtraData(EvidenceKey, new Binding(proof, capture, increments, store, ret, receiver, stored));
    }

    internal static bool HasEvidence(MethodAnalysisContext method) => method.GetExtraData<Binding>(EvidenceKey) != null;

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (method.GetExtraData<Binding>(EvidenceKey) is not { } binding ||
            X64ReferenceFieldAddressStoreProof.Find(method) is not { } current || !SameProof(binding.Proof, current) ||
            LinearBody(method) is not { } block || Active(block) is not { } active ||
            active.Length != current.Increments.Count + 3 ||
            !IncomingReceiver(method, out var receiver) || !ReferenceEquals(receiver, binding.Receiver) ||
            !Prefix(active.AsSpan(0, active.Length - 2).ToArray(), method, current, receiver, false, out var capture, out var increments) ||
            !ReferenceEquals(capture, binding.Capture) || !increments.SequenceEqual(binding.Increments) ||
            active[^2] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [FieldReference destination, LocalVariable stored] } store ||
            !ReferenceEquals(store, binding.Store) || !ReferenceEquals(stored, binding.Value) ||
            !ReferenceEquals(stored, capture.Destination) || !ReferenceEquals(destination.Local, receiver) ||
            !ReferenceEquals(destination.Field, current.Destination) || destination.Offset != current.Destination.Offset ||
            store.NativeAddress != current.Native.StoreAddress ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands.Count: 0 } ret || !ReferenceEquals(ret, binding.Return) || ret.NativeAddress != current.Native.TailAddress ||
            Mutable(method, receiver, stored))
            return false;
        return true;
    }

    private static bool Prefix(Instruction[] operations, MethodAnalysisContext method,
        X64ReferenceFieldAddressStoreProof.Proof proof, LocalVariable receiver, bool allowUnspecifiedWidth,
        out Instruction capture, out Instruction[] increments)
    {
        capture = null!;
        increments = [];
        if (operations.Length != proof.Increments.Count + 1)
            return false;
        var expected = proof.Increments.Select(increment => increment.Address)
            .Append(proof.Native.SourceAddress).OrderBy(address => address).ToArray();
        if (!operations.Select(operation => operation.NativeAddress).SequenceEqual(expected.Select(address => (ulong?)address)))
            return false;
        var captures = operations.Where(operation => operation.NativeAddress == proof.Native.SourceAddress).ToArray();
        if (captures is not [{ OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable value, FieldReference source] } read] ||
            !ReferenceEquals(source.Local, receiver) || !ReferenceEquals(source.Field, proof.Source) ||
            source.Offset != proof.Source.Offset || !ReferenceEquals(value.Type, proof.Source.FieldType) ||
            method.ParameterLocals.Contains(value) || value.IsThis || value.IsMethodInfo ||
            value.Register.Copy() != new Register(null, X86Utils.GetRegisterName(proof.Native.ValueRegister)) ||
            method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, value)) != 1)
            return false;
        var writes = operations.Where(operation => !ReferenceEquals(operation, read)).ToArray();
        for (var index = 0; index < writes.Length; index++)
        {
            var increment = proof.Increments[index];
            if (writes[index] is not { OpCode: OpCode.Add, IntegerBitWidth: 0 or 32, CallSemantics: CallSemantics.Direct,
                    Operands: [FieldReference written, FieldReference readField, Immediate { Value: 1 }] } ||
                (!allowUnspecifiedWidth && writes[index].IntegerBitWidth != 32) ||
                !ReferenceEquals(written.Field, increment.Field) || !ReferenceEquals(readField.Field, increment.Field) ||
                !ReferenceEquals(written.Local, receiver) || !ReferenceEquals(readField.Local, receiver) ||
                written.Offset != increment.Field.Offset || readField.Offset != increment.Field.Offset ||
                writes[index].NativeAddress != increment.Address)
                return false;
        }
        capture = read;
        increments = writes;
        return true;
    }

    private static bool IncomingReceiver(MethodAnalysisContext method, out LocalVariable receiver)
    {
        receiver = null!;
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var locals = method.ParameterLocals.Where(local => local.IsThis).ToArray();
        if (method.IsStatic || method.Parameters.Count != 0 || method.ParameterOperands.Count == 0 ||
            abi.Length == 0 || abi[0] is not Register original || method.ParameterOperands[0] is not Register current ||
            original != current || locals is not [{ } local] || local.Register != original ||
            !ReferenceEquals(local.Type, method.DeclaringType) ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)))
            return false;
        receiver = local;
        return true;
    }

    private static Block? LinearBody(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph;
        if (graph == null || graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.Blocks.Where(block => block != graph.EntryBlock && block != graph.ExitBlock).ToArray() is not [var body] ||
            graph.EntryBlock.Successors is not [var entry] || !ReferenceEquals(entry, body) ||
            body.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, graph.EntryBlock) ||
            body.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock) ||
            graph.ExitBlock.Predecessors is not [var final] || !ReferenceEquals(final, body))
            return null;
        return body;
    }

    private static Instruction[]? Active(Block block) => block.Instructions.Any(instruction =>
            instruction.OpCode == OpCode.Nop && (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                                                instruction.CallSemantics != CallSemantics.Direct))
        ? null : block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();

    private static bool Mutable(MethodAnalysisContext method, params LocalVariable[] locals) =>
        OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph!.Instructions).Any(changed =>
            locals.Any(local => local.Register.Number == changed.Register.Number));

    private static bool SameProof(X64ReferenceFieldAddressStoreProof.Proof original,
        X64ReferenceFieldAddressStoreProof.Proof current) =>
        ReferenceEquals(original.Source, current.Source) && ReferenceEquals(original.Destination, current.Destination) &&
        (original.Native with { Increments = current.Native.Increments }) == current.Native &&
        original.Increments.SequenceEqual(current.Increments) &&
        original.Native.Increments.SequenceEqual(current.Native.Increments);
}
