using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Binds the managed field projection to its unchanged incoming aggregate and native leaf.</summary>
internal static class SmallAggregateFieldGetterRecovery
{
    private const string BindingKey = "SmallAggregateFieldGetterRecovery";
    private sealed record Binding(X64SmallAggregateFieldGetterProof.Proof Proof, Instruction[] Operations,
        LocalVariable Incoming, LocalVariable Capture, LocalVariable Result,
        Register CaptureRegister, Register ResultRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64SmallAggregateFieldGetterProof.GetEvidence(method) is not { } proof ||
            X64SmallAggregateFieldGetterProof.Find(method) is not { } current || !proof.Matches(current) ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding with { Proof = proof });
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64SmallAggregateFieldGetterProof.WasLifted(method) ||
        X64SmallAggregateFieldGetterProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null ||
        method.ControlFlowGraph?.Instructions.SelectMany(OperandEffects.ReadLocals).Any(local =>
            local.Register.Name is X64SmallAggregateFieldGetterProof.CaptureRegister or
                X64SmallAggregateFieldGetterProof.ResultRegister) == true;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        ReferenceEquals(X64SmallAggregateFieldGetterProof.GetEvidence(method), saved.Proof) &&
        X64SmallAggregateFieldGetterProof.Find(method) is { } current && saved.Proof.Matches(current) &&
        TryBind(method, current) is { } binding && binding.Operations.SequenceEqual(saved.Operations) &&
        ReferenceEquals(binding.Incoming, saved.Incoming) && ReferenceEquals(binding.Capture, saved.Capture) &&
        ReferenceEquals(binding.Result, saved.Result) && binding.CaptureRegister == saved.CaptureRegister &&
        binding.ResultRegister == saved.ResultRegister;

    private static Binding? TryBind(MethodAnalysisContext method, X64SmallAggregateFieldGetterProof.Proof proof)
    {
        if (LinearBody(method) is not { } body ||
            body.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                 instruction.CallSemantics != CallSemantics.Direct)) ||
            body.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not { } active ||
            active.Length != (proof.Widen ? 3 : 2) || !IncomingParameter(method, proof, out var incoming) ||
            active[0] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable capture, FieldReference field] } read ||
            read.NativeAddress != proof.Native.Load.IP || !ReferenceEquals(field.Local, incoming) ||
            !ReferenceEquals(field.Field, proof.Field) || field.Offset != 0 ||
            !ReferenceEquals(capture.Type, proof.Field.FieldType) ||
            !PrivateLocal(method, capture, X64SmallAggregateFieldGetterProof.CaptureRegister) ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable result] } ret || ret.NativeAddress != proof.Native.Return.IP ||
            !ReferenceEquals(result.Type, method.ReturnType))
            return null;
        if (proof.Widen)
        {
            if (active[1] is not { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, Operands: [LocalVariable destination,
                        LocalVariable source, Immediate width, Immediate { Value: 32 }, Immediate signed] } extension ||
                extension.NativeAddress != proof.Native.Load.IP || !ReferenceEquals(destination, result) ||
                !ReferenceEquals(source, capture) || width.Value != proof.Native.Width ||
                signed.Value != (proof.Native.Signed ? 1 : 0) ||
                !PrivateLocal(method, result, X64SmallAggregateFieldGetterProof.ResultRegister) ||
                !IntegerExtension.TryGet(extension, out var conversion) ||
                !conversion.HasCanonicalTypes(extension, method.AppContext))
                return null;
        }
        else if (!ReferenceEquals(result, capture))
            return null;
        if (OperandEffects.LocalsWithMutableStorage(active).Count != 0 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, incoming)) != 1 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, capture)) != 1)
            return null;
        return new Binding(proof, active, incoming, capture, result, capture.Register, result.Register);
    }

    private static bool PrivateLocal(MethodAnalysisContext method, LocalVariable local, string name) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) &&
        local.Register.Copy() == new Register(null, name) && method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
        method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, local)) == 1;

    private static bool IncomingParameter(MethodAnalysisContext method, X64SmallAggregateFieldGetterProof.Proof proof,
        out LocalVariable incoming)
    {
        incoming = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (expected is not [Register { Name: "rcx", Version: -1 } original,
                Register { Name: "rdx", Version: -1 }] ||
            expected.Length != method.ParameterOperands.Count || !expected.SequenceEqual(method.ParameterOperands) ||
            method.ParameterLocals.ToArray() is not [{ IsThis: false, IsMethodInfo: false } local] ||
            local.Register != original || !ReferenceEquals(local.Type, proof.Parameter.ParameterType) ||
            LocalVariables.GetIncomingParameterIndex(method, local) != 0 ||
            // RemoveUnused may omit a parameter read only through FieldReference.
            // Emission binds it through the independently authenticated ParameterLocals entry.
            method.Locals.Count(item => ReferenceEquals(item, local)) > 1 ||
            method.Locals.Any(item => item.Register.Number == original.Number && !ReferenceEquals(item, local)) ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)))
            return false;
        incoming = local;
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
}
