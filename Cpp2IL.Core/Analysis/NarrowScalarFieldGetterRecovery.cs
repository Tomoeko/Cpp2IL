using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Binds the final field capture and return to the complete native getter.</summary>
internal static class NarrowScalarFieldGetterRecovery
{
    private const string BindingKey = "NarrowScalarFieldGetterRecovery";
    private sealed record Binding(X64NarrowScalarFieldGetterProof.Proof Proof, Instruction[] Operations,
        LocalVariable Receiver, LocalVariable Capture, LocalVariable Result,
        Register CaptureRegister, Register ResultRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64NarrowScalarFieldGetterProof.GetEvidence(method) is not { } proof ||
            X64NarrowScalarFieldGetterProof.Find(method) is not { } current || current != proof ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64NarrowScalarFieldGetterProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null ||
        method.ControlFlowGraph?.Instructions.SelectMany(OperandEffects.ReadLocals).Any(local =>
            local.Register.Name == X64NarrowScalarFieldGetterProof.CaptureRegister ||
            local.Register.Name == X64NarrowScalarFieldGetterProof.ResultRegister) == true;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        X64NarrowScalarFieldGetterProof.GetEvidence(method) == saved.Proof &&
        X64NarrowScalarFieldGetterProof.Find(method) is { } current && current == saved.Proof &&
        TryBind(method, current) is { } binding && binding.Operations.SequenceEqual(saved.Operations) &&
        ReferenceEquals(binding.Receiver, saved.Receiver) && ReferenceEquals(binding.Capture, saved.Capture) &&
        ReferenceEquals(binding.Result, saved.Result) && binding.CaptureRegister == saved.CaptureRegister &&
        binding.ResultRegister == saved.ResultRegister;

    private static Binding? TryBind(MethodAnalysisContext method, X64NarrowScalarFieldGetterProof.Proof proof)
    {
        if (LinearBody(method) is not { } block ||
            block.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                 instruction.CallSemantics != CallSemantics.Direct)) ||
            block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not { } active ||
            active.Length != (proof.Widen ? 3 : 2) || !IncomingReceiver(method, out var receiver) ||
            active[0] is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable capture, FieldReference field] } read ||
            read.NativeAddress != proof.Native.Load.IP || !ReferenceEquals(field.Local, receiver) ||
            !ReferenceEquals(field.Field, proof.Field) || field.Offset != proof.Native.Offset ||
            !ReferenceEquals(capture.Type, proof.Field.FieldType) ||
            !PrivateLocal(method, capture, X64NarrowScalarFieldGetterProof.CaptureRegister) ||
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
                !PrivateLocal(method, result, X64NarrowScalarFieldGetterProof.ResultRegister) ||
                !IntegerExtension.TryGet(extension, out var conversion) ||
                !conversion.HasCanonicalTypes(extension, method.AppContext))
                return null;
        }
        else if (!ReferenceEquals(result, capture))
            return null;
        if (OperandEffects.LocalsWithMutableStorage(active).Count != 0 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, receiver)) != 1 ||
            active.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, capture)) != 1)
            return null;
        return new Binding(proof, active, receiver, capture, result, capture.Register, result.Register);
    }

    private static bool PrivateLocal(MethodAnalysisContext method, LocalVariable local, string name) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) &&
        local.Register.Copy() == new Register(null, name) && method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
        method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, local)) == 1;

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
