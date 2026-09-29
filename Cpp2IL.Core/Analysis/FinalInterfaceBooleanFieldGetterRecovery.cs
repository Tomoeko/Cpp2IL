using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Retains the exact typed capture and return established by the final-interface leaf proof.</summary>
internal static class FinalInterfaceBooleanFieldGetterRecovery
{
    private const string BindingKey = "FinalInterfaceBooleanFieldGetterRecovery";
    private sealed record Binding(X64FinalInterfaceBooleanFieldGetterProof.Proof Proof,
        Instruction Read, Instruction Return, LocalVariable Receiver, LocalVariable Capture, Register CaptureRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64FinalInterfaceBooleanFieldGetterProof.GetEvidence(method) is not { } saved ||
            X64FinalInterfaceBooleanFieldGetterProof.Find(method) is not { } current || !saved.Matches(current) ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64FinalInterfaceBooleanFieldGetterProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null ||
        method.ControlFlowGraph?.Instructions.SelectMany(OperandEffects.ReadLocals).Any(local =>
            local.Register.Name == X64FinalInterfaceBooleanFieldGetterProof.CaptureRegister) == true;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        X64FinalInterfaceBooleanFieldGetterProof.GetEvidence(method) is { } evidence && saved.Proof.Matches(evidence) &&
        X64FinalInterfaceBooleanFieldGetterProof.Find(method) is { } current && saved.Proof.Matches(current) &&
        TryBind(method, current) is { } binding && ReferenceEquals(binding.Read, saved.Read) &&
        ReferenceEquals(binding.Return, saved.Return) && ReferenceEquals(binding.Receiver, saved.Receiver) &&
        ReferenceEquals(binding.Capture, saved.Capture) && binding.CaptureRegister == saved.CaptureRegister;

    private static Binding? TryBind(MethodAnalysisContext method, X64FinalInterfaceBooleanFieldGetterProof.Proof proof)
    {
        if (LinearBody(method) is not { } body ||
            body.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                 instruction.CallSemantics != CallSemantics.Direct)) ||
            body.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                [{ OpCode: OpCode.Move, IntegerBitWidth: 8, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable capture, FieldReference field] } read,
                 { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable result] } ret] ||
            !IncomingReceiver(method, out var receiver) || read.NativeAddress != proof.LoadIp ||
            ret.NativeAddress != proof.ReturnIp || !ReferenceEquals(field.Local, receiver) ||
            !ReferenceEquals(field.Field, proof.Field) || field.Offset != proof.Field.Offset ||
            !ReferenceEquals(capture, result) || !ReferenceEquals(capture.Type, method.ReturnType) ||
            capture.IsThis || capture.IsMethodInfo || method.ParameterLocals.Contains(capture) ||
            capture.Register.Copy() != new Register(null, X64FinalInterfaceBooleanFieldGetterProof.CaptureRegister) ||
            method.Locals.Count(local => ReferenceEquals(local, capture)) != 1 ||
            method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, capture)) != 1 ||
            OperandEffects.LocalsWithMutableStorage(body.Instructions).Count != 0 ||
            body.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, receiver)) != 1 ||
            body.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, capture)) != 1)
            return null;
        return new Binding(proof, read, ret, receiver, capture, capture.Register);
    }

    private static bool IncomingReceiver(MethodAnalysisContext method, out LocalVariable receiver)
    {
        receiver = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (expected.Length != method.ParameterOperands.Count || expected.Length == 0 ||
            expected[0] is not Register { Name: "rcx", Version: -1 } original ||
            !expected.SequenceEqual(method.ParameterOperands) ||
            method.ParameterLocals.ToArray() is not [{ IsThis: true, IsMethodInfo: false } local] ||
            local.Register != original || !ReferenceEquals(local.Type, method.DeclaringType) ||
            method.Locals.Any(item => item.Register.Number == original.Number && !ReferenceEquals(item, local)) ||
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
}
