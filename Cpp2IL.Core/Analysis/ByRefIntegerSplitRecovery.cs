using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Retains original argument provenance, explicit truncations, and both ordered byref writes.</summary>
internal static class ByRefIntegerSplitRecovery
{
    private const string BindingKey = "ByRefIntegerSplitRecovery";
    private sealed record Binding(X64ByRefIntegerSplitProof.Proof Proof, Instruction[] Operations,
        LocalVariable[] Incoming, Register[] IncomingRegisters, TypeAnalysisContext[] IncomingTypes,
        LocalVariable[] Results, Register[] ResultRegisters, TypeAnalysisContext[] ResultTypes);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64ByRefIntegerSplitProof.GetEvidence(method) is not { } initial ||
            X64ByRefIntegerSplitProof.Find(method) is not { } current || !initial.Matches(current))
            return;
        // The complete native SHR/SAR64 proof establishes this private producer's
        // width. It does not narrow the incoming managed I8/U8 argument.
        var shifts = method.ControlFlowGraph?.Instructions.Where(instruction =>
            instruction.NativeAddress == current.Native.Shift.IP &&
            instruction.OpCode == (current.Native.Arithmetic ? OpCode.ShiftRight : OpCode.ShiftRightUnsigned)).ToArray();
        if (shifts is not [{ Operands: [LocalVariable shifted, LocalVariable source, Immediate { Value: 32 }] } operation] ||
            operation.IntegerBitWidth != 64 || !X64ByRefIntegerSplitProof.CanonicalWide(source.Type, method) ||
            shifted.Type != null && !X64ByRefIntegerSplitProof.CanonicalWide(shifted.Type, method))
            return;
        shifted.Type = source.Type;
        if (TryBind(method, current) is { } binding)
            method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64ByRefIntegerSplitProof.WasLifted(method) || X64ByRefIntegerSplitProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null || method.Locals.Any(Owned) ||
        method.ControlFlowGraph?.Instructions.SelectMany(OperandEffects.ReadLocals).Any(Owned) == true;

    private static bool Owned(LocalVariable local) => local.Register.Name is X64ByRefIntegerSplitProof.LowRegister or
        X64ByRefIntegerSplitProof.ShiftRegister or X64ByRefIntegerSplitProof.HighRegister;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        X64ByRefIntegerSplitProof.GetEvidence(method) is { } initial && initial.Matches(saved.Proof) &&
        X64ByRefIntegerSplitProof.Find(method) is { } current && current.Matches(saved.Proof) &&
        TryBind(method, current) is { } actual && actual.Operations.SequenceEqual(saved.Operations) &&
        actual.Incoming.SequenceEqual(saved.Incoming) && actual.IncomingRegisters.SequenceEqual(saved.IncomingRegisters) &&
        actual.IncomingTypes.SequenceEqual(saved.IncomingTypes) && actual.Results.SequenceEqual(saved.Results) &&
        actual.ResultRegisters.SequenceEqual(saved.ResultRegisters) && actual.ResultTypes.SequenceEqual(saved.ResultTypes);

    private static Binding? TryBind(MethodAnalysisContext method, X64ByRefIntegerSplitProof.Proof proof)
    {
        if (LinearBody(method) is not { } block ||
            block.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 || instruction.CallSemantics != CallSemantics.Direct)) ||
            block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                [var lowConversion, var lowStore, var shift, var highConversion, var highStore, var ret] ||
            !Incoming(method, 0, out var source) || !Incoming(method, 1, out var lowPointer) ||
            !Incoming(method, 2, out var highPointer) ||
            lowConversion.Operands.FirstOrDefault() is not LocalVariable low ||
            highConversion.Operands.FirstOrDefault() is not LocalVariable high ||
            !Conversion(method, lowConversion, low, source, lowPointer, proof.Native.LowStore.IP) ||
            !Store(lowStore, lowPointer, low, proof.Native.LowStore.IP) ||
            shift is not { IntegerBitWidth: 64, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable shifted, LocalVariable shiftedSource, Immediate { Value: 32 }] } ||
            shift.OpCode != (proof.Native.Arithmetic ? OpCode.ShiftRight : OpCode.ShiftRightUnsigned) ||
            shift.NativeAddress != proof.Native.Shift.IP || !ReferenceEquals(shiftedSource, source) ||
            !ReferenceEquals(shifted.Type, source.Type) ||
            !Conversion(method, highConversion, high, shifted, highPointer, proof.Native.HighStore.IP) ||
            !Store(highStore, highPointer, high, proof.Native.HighStore.IP) ||
            !Private(method, low, X64ByRefIntegerSplitProof.LowRegister) ||
            !Private(method, shifted, X64ByRefIntegerSplitProof.ShiftRegister) ||
            !Private(method, high, X64ByRefIntegerSplitProof.HighRegister) ||
            ret is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct, Operands.Count: 0 } ||
            ret.NativeAddress != proof.Native.Return.IP || OperandEffects.LocalsWithMutableStorage(block.Instructions).Count != 0)
            return null;
        var expectedLocals = new[] { source, lowPointer, highPointer, low, shifted, high };
        var expectedReads = new[] { 2, 1, 1, 1, 1, 1 };
        var reads = block.Instructions.SelectMany(OperandEffects.ReadLocals).ToArray();
        if (reads.Any(local => !expectedLocals.Contains(local)) ||
            expectedLocals.Where((local, index) => reads.Count(read => ReferenceEquals(read, local)) != expectedReads[index]).Any())
            return null;
        var incoming = new[] { source, lowPointer, highPointer };
        var results = new[] { low, shifted, high };
        return new Binding(proof, [lowConversion, lowStore, shift, highConversion, highStore, ret], incoming,
            incoming.Select(local => local.Register).ToArray(), incoming.Select(local => local.Type!).ToArray(), results,
            results.Select(local => local.Register).ToArray(), results.Select(local => local.Type!).ToArray());
    }

    private static bool Conversion(MethodAnalysisContext method, Instruction operation, LocalVariable result,
        LocalVariable source, LocalVariable pointer, ulong address) =>
        pointer.Type is ByRefTypeAnalysisContext reference && ReferenceEquals(result.Type, reference.ElementType) &&
        operation is { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [LocalVariable destination, LocalVariable value, Immediate { Value: 32 }, Immediate { Value: 32 }, Immediate sign] } &&
        ReferenceEquals(destination, result) && ReferenceEquals(value, source) && operation.NativeAddress == address &&
        sign.Value == (ReferenceEquals(reference.ElementType, method.AppContext.SystemTypes.SystemInt32Type) ? 1 : 0) &&
        IntegerExtension.TryGet(operation, out var extension) && extension.HasCanonicalTypes(operation, method.AppContext);

    private static bool Store(Instruction operation, LocalVariable pointer, LocalVariable value, ulong address) =>
        operation is { OpCode: OpCode.Move, IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
            Operands: [MemoryOperand { Base: LocalVariable target, Index: null, Addend: 0, Scale: 0 }, LocalVariable stored] } &&
        ReferenceEquals(target, pointer) && ReferenceEquals(stored, value) && operation.NativeAddress == address;

    private static bool Incoming(MethodAnalysisContext method, int ordinal, out LocalVariable local)
    {
        local = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method)
            .Select(operand => operand is StackOffset slot
                ? (IOperand)new Register(null, StackAnalyzer.NameForSlot(slot)) : operand).ToArray();
        var index = ordinal + (method.IsStatic ? 0 : 1);
        if (!expected.SequenceEqual(method.ParameterOperands) || expected[index] is not Register register || register.Version != -1 ||
            method.ParameterLocals.Where(candidate => candidate.Register == register).ToArray() is not [var incoming] ||
            incoming.IsThis || incoming.IsMethodInfo || method.Locals.Count(item => ReferenceEquals(item, incoming)) > 1 ||
            method.Locals.Any(item => item.Register.Number == register.Number && !ReferenceEquals(item, incoming)) ||
            method.ControlFlowGraph!.Instructions.Any(operation => ReferenceEquals(operation.Destination, incoming)))
            return false;
        var type = method.Parameters[ordinal].ParameterType;
        if (ordinal == 0 ? !ReferenceEquals(incoming.Type, type) :
            incoming.Type is not ByRefTypeAnalysisContext current || type is not ByRefTypeAnalysisContext declared ||
            !ReferenceEquals(current.ElementType, declared.ElementType))
            return false;
        // RemoveUnused may omit pointer parameters from temporary Locals; the
        // sole original ParameterLocal still supplies their managed ldarg mapping.
        local = incoming;
        return true;
    }

    private static bool Private(MethodAnalysisContext method, LocalVariable local, string name) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) && local.Register.Version >= 0 &&
        local.Register.Copy() == new Register(null, name) && method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
        !method.Locals.Any(item => !ReferenceEquals(item, local) && item.Register.Number == local.Register.Number) &&
        method.ControlFlowGraph!.Instructions.Count(operation => ReferenceEquals(operation.Destination, local)) == 1;

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
}
