using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Binds the final callvirt, result store and return to their complete native invocation proof.</summary>
internal static class ScalarVirtualDispatchRecovery
{
    internal const string BindingKey = "ScalarVirtualDispatchRecovery";
    private sealed record Binding(X64ScalarVirtualDispatchProof.Proof Proof, Instruction[] Operations,
        LocalVariable[] Incoming, Register[] IncomingRegisters, TypeAnalysisContext[] IncomingTypes,
        LocalVariable Result, Register ResultRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64ScalarVirtualDispatchProof.GetEvidence(method) is not { } saved ||
            X64ScalarVirtualDispatchProof.Find(method) is not { } current || !saved.Matches(current) ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding with { Proof = saved });
    }

    internal static bool HasEvidence(MethodAnalysisContext method) => X64ScalarVirtualDispatchProof.WasLifted(method) ||
        X64ScalarVirtualDispatchProof.GetEvidence(method) != null || method.GetExtraData<Binding>(BindingKey) != null ||
        method.ControlFlowGraph?.Instructions.Any(instruction => instruction.CallSemantics == CallSemantics.VirtualDispatch ||
            OperandEffects.ReadLocals(instruction).Any(local => local.Register.Name == X64ScalarVirtualDispatchProof.ResultRegister)) == true;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        ReferenceEquals(X64ScalarVirtualDispatchProof.GetEvidence(method), saved.Proof) &&
        X64ScalarVirtualDispatchProof.Find(method) is { } current && saved.Proof.Matches(current) &&
        TryBind(method, current) is { } binding && binding.Operations.SequenceEqual(saved.Operations) &&
        binding.Incoming.SequenceEqual(saved.Incoming) && binding.IncomingRegisters.SequenceEqual(saved.IncomingRegisters) &&
        binding.IncomingTypes.SequenceEqual(saved.IncomingTypes) && ReferenceEquals(binding.Result, saved.Result) &&
        binding.ResultRegister == saved.ResultRegister;

    internal static bool TryGetBoundCall(MethodAnalysisContext method, Instruction call, out MethodAnalysisContext target)
    {
        target = null!;
        if (method.GetExtraData<Binding>(BindingKey) is not { } binding ||
            !ReferenceEquals(binding.Operations[0], call) || !IsValidFor(method))
            return false;
        target = binding.Proof.Contract;
        return true;
    }

    private static Binding? TryBind(MethodAnalysisContext method, X64ScalarVirtualDispatchProof.Proof proof)
    {
        if (LinearBody(method) is not { } block || block.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 || instruction.CallSemantics != CallSemantics.Direct)) ||
            block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not { } active ||
            active.Length != (proof.StoredField == null ? 2 : 3) ||
            method.ParameterLocals.Count != method.Parameters.Count ||
            !Incoming(method, 0, out var receiver) || !Incoming(method, 1, out var argument) ||
            active[0] is not { OpCode: OpCode.Call, IntegerBitWidth: 0, CallSemantics: CallSemantics.VirtualDispatch,
                Operands.Count: 4 or 5 } call || !ReferenceEquals(call.Operands[0], proof.Contract) ||
            call.NativeAddress != proof.Native.Invoke.IP || !ReferenceEquals(call.Operands[2], receiver) ||
            !ReferenceEquals(call.Operands[3], argument) || call.Operands.Count == 5 && call.Operands[4] is not Immediate { Value: 0 } ||
            call.Operands[1] is not LocalVariable result || !PrivateResult(method, result) ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable returned] } ret || !ReferenceEquals(returned, result) || ret.NativeAddress != proof.Native.Exit.IP ||
            OperandEffects.LocalsWithMutableStorage(active).Count != 0)
            return null;
        var incoming = new[] { receiver, argument };
        if (proof.StoredField != null)
        {
            if (!Incoming(method, 2, out var marker) ||
                active[1] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [FieldReference field, LocalVariable stored] } store ||
                !ReferenceEquals(field.Field, proof.StoredField) || !ReferenceEquals(field.Local, marker) ||
                field.Offset != proof.StoredField.Offset || !ReferenceEquals(stored, result) ||
                store.NativeAddress != proof.Native.Store!.Value.IP)
                return null;
            incoming = [receiver, argument, marker];
        }
        var reads = active.SelectMany(OperandEffects.ReadLocals).ToArray();
        if (incoming.Any(local => reads.Count(read => ReferenceEquals(read, local)) != 1) ||
            reads.Count(read => ReferenceEquals(read, result)) != (proof.StoredField == null ? 1 : 2) ||
            reads.Any(local => !incoming.Contains(local) && !ReferenceEquals(local, result)))
            return null;
        return new Binding(proof, active, incoming, incoming.Select(local => local.Register).ToArray(),
            incoming.Select(local => local.Type!).ToArray(), result, result.Register);
    }

    private static bool Incoming(MethodAnalysisContext method, int ordinal, out LocalVariable local)
    {
        local = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (!expected.SequenceEqual(method.ParameterOperands) || expected[ordinal] is not Register { Version: -1 } register ||
            method.ParameterLocals.Where(candidate => candidate.Register == register).ToArray() is not [var incoming] ||
            incoming.IsThis || incoming.IsMethodInfo || !ReferenceEquals(incoming.Type, method.Parameters[ordinal].ParameterType) ||
            method.Locals.Count(item => ReferenceEquals(item, incoming)) > 1 ||
            method.Locals.Any(item => item.Register.Number == register.Number && !ReferenceEquals(item, incoming)) ||
            method.ControlFlowGraph!.Instructions.Any(operation => ReferenceEquals(operation.Destination, incoming)))
            return false;
        local = incoming;
        return true;
    }

    private static bool PrivateResult(MethodAnalysisContext method, LocalVariable local) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) && local.Register.Version >= 0 &&
        local.Register.Copy() == new Register(null, X64ScalarVirtualDispatchProof.ResultRegister) &&
        ReferenceEquals(local.Type, method.AppContext.SystemTypes.SystemInt32Type) &&
        method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
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
