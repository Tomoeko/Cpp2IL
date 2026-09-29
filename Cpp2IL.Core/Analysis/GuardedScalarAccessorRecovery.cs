using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Closes the final emitted operations over the original receiver and current accessor evidence.</summary>
internal static class GuardedScalarAccessorRecovery
{
    private const string BindingKey = "GuardedScalarAccessorRecovery";
    private sealed record Binding(X64GuardedScalarAccessorProof.Proof Proof, Instruction[] Operations,
        LocalVariable Incoming, LocalVariable? Capture, LocalVariable Result,
        Register IncomingRegister, Register? CaptureRegister, Register ResultRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64GuardedScalarAccessorProof.GetEvidence(method) is not { } proof ||
            X64GuardedScalarAccessorProof.Find(method) is not { } current || !proof.Matches(current) ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding with { Proof = proof });
    }
    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64GuardedScalarAccessorProof.WasLifted(method) || X64GuardedScalarAccessorProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null || method.ControlFlowGraph?.Instructions
            .SelectMany(OperandEffects.ReadLocals).Any(local => local.Register.Name is
                X64GuardedScalarAccessorProof.CaptureRegister or X64GuardedScalarAccessorProof.ResultRegister) == true;
    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        ReferenceEquals(X64GuardedScalarAccessorProof.GetEvidence(method), saved.Proof) &&
        X64GuardedScalarAccessorProof.Find(method) is { } current && saved.Proof.Matches(current) &&
        TryBind(method, current) is { } binding && binding.Operations.SequenceEqual(saved.Operations) &&
        ReferenceEquals(binding.Incoming, saved.Incoming) && ReferenceEquals(binding.Capture, saved.Capture) &&
        ReferenceEquals(binding.Result, saved.Result) && binding.IncomingRegister == saved.IncomingRegister &&
        binding.CaptureRegister == saved.CaptureRegister && binding.ResultRegister == saved.ResultRegister;

    internal static bool TryGetBoundCall(MethodAnalysisContext method, Instruction instruction,
        out MethodAnalysisContext target)
    {
        target = null!;
        if (method.GetExtraData<Binding>(BindingKey) is not { Proof.IsDirect: false } binding ||
            !ReferenceEquals(binding.Operations[1], instruction) || !IsValidFor(method))
            return false;
        target = binding.Proof.Getter;
        return true;
    }

    private static Binding? TryBind(MethodAnalysisContext method, X64GuardedScalarAccessorProof.Proof proof)
    {
        if (LinearBody(method) is not { } body ||
            body.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                 instruction.CallSemantics != CallSemantics.Direct)) ||
            body.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not { } active ||
            active.Length != (proof.IsDirect ? 2 : 3) || !IncomingThis(method, out var incoming) ||
            active[0] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable read, FieldReference field] } capture ||
            !ReferenceEquals(field.Local, incoming) ||
            !ReferenceEquals(field.Field, proof.IsDirect ? proof.Value : proof.Source) ||
            field.Offset != field.Field.Offset ||
            capture.NativeAddress != (proof.IsDirect ? proof.GetterNative.Load.IP : proof.Caller!.Capture.IP) ||
            !ReferenceEquals(read.Type, field.Field.FieldType) ||
            !PrivateLocal(method, read, proof.IsDirect ? X64GuardedScalarAccessorProof.ResultRegister :
                X64GuardedScalarAccessorProof.CaptureRegister) ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable result] } ret ||
            ret.NativeAddress != (proof.IsDirect ? proof.GetterNative.Return.IP : proof.Caller!.Return.IP) ||
            !ReferenceEquals(result.Type, proof.Value.FieldType) ||
            !PrivateLocal(method, result, X64GuardedScalarAccessorProof.ResultRegister) ||
            OperandEffects.LocalsWithMutableStorage(active).Count != 0 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, incoming)) != 1 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, read)) != 1)
            return null;
        if (proof.IsDirect)
        {
            if (!ReferenceEquals(result, read))
                return null;
        }
        else if (active[1] is not { OpCode: OpCode.Call, IntegerBitWidth: 0,
                     CallSemantics: CallSemantics.NullCheckedInstance } call ||
                 call.NativeAddress != proof.Caller!.Load.IP || call.Operands.Count is not (3 or 4) ||
                 !ReferenceEquals(call.Operands[0], proof.Getter) || !ReferenceEquals(call.Operands[1], result) ||
                 !ReferenceEquals(call.Operands[2], read) ||
                 call.Operands.Count == 4 && call.Operands[3] is not Immediate { Value: 0 } ||
                 !ReferenceEquals(proof.Getter.DeclaringType, read.Type) ||
                 active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, result)) != 1)
            return null;
        return new Binding(proof, active, incoming, proof.IsDirect ? null : read, result,
            incoming.Register, proof.IsDirect ? null : read.Register, result.Register);
    }
    private static bool PrivateLocal(MethodAnalysisContext method, LocalVariable local, string name) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) &&
        local.Register.Version >= 0 && local.Register.Copy() == new Register(null, name) &&
        !method.Locals.Any(item => !ReferenceEquals(item, local) && item.Register.Number == local.Register.Number) &&
        method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
        method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, local)) == 1;
    private static bool IncomingThis(MethodAnalysisContext method, out LocalVariable incoming)
    {
        incoming = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (expected is not [Register { Name: "rcx", Version: -1 } original, Register { Name: "rdx", Version: -1 }] ||
            expected.Length != method.ParameterOperands.Count || !expected.SequenceEqual(method.ParameterOperands) ||
            method.ParameterLocals.ToArray() is not [{ IsThis: true, IsMethodInfo: false } local] ||
            local.Register != original || !ReferenceEquals(local.Type, method.DeclaringType) ||
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
