using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Rebinds the original full64 field capture and explicit low32 return at final emission.</summary>
internal static class WideFieldLow32ReadRecovery
{
    private const string BindingKey = "WideFieldLow32ReadRecovery";
    private sealed record Binding(X64WideFieldLow32ReadProof.Proof Proof, Instruction[] Operations,
        FieldReference Access, LocalVariable Receiver, LocalVariable Capture, LocalVariable Result,
        Register CaptureRegister, Register ResultRegister);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64WideFieldLow32ReadProof.GetEvidence(method) is not { } proof ||
            X64WideFieldLow32ReadProof.Find(method) is not { } current || !proof.Matches(current) ||
            TryBind(method, current) is not { } binding)
            return;
        method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64WideFieldLow32ReadProof.WasLifted(method) || X64WideFieldLow32ReadProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null ||
        method.Locals.Any(OwnedLocal) || method.ControlFlowGraph?.Instructions
            .SelectMany(instruction => instruction.Operands.OfType<LocalVariable>()).Any(OwnedLocal) == true;

    private static bool OwnedLocal(LocalVariable local) => local.Register.Name is
        X64WideFieldLow32ReadProof.CaptureRegister or X64WideFieldLow32ReadProof.ResultRegister;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        X64WideFieldLow32ReadProof.GetEvidence(method) is { } initial && initial.Matches(saved.Proof) &&
        X64WideFieldLow32ReadProof.Find(method) is { } current && current.Matches(saved.Proof) &&
        TryBind(method, current) is { } binding && binding.Operations.SequenceEqual(saved.Operations) &&
        ReferenceEquals(binding.Access, saved.Access) && ReferenceEquals(binding.Receiver, saved.Receiver) &&
        ReferenceEquals(binding.Capture, saved.Capture) && ReferenceEquals(binding.Result, saved.Result) &&
        binding.CaptureRegister == saved.CaptureRegister && binding.ResultRegister == saved.ResultRegister;

    private static Binding? TryBind(MethodAnalysisContext method, X64WideFieldLow32ReadProof.Proof proof)
    {
        if (LinearBody(method) is not { } block ||
            block.Instructions.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 || instruction.CallSemantics != CallSemantics.Direct)) ||
            block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                [var read, var conversion, var ret] || !IncomingReceiver(method, out var receiver) ||
            read is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable capture, FieldReference field] } ||
            read.NativeAddress != proof.Native.Load.IP || !ReferenceEquals(field.Local, receiver) ||
            !ReferenceEquals(field.Field, proof.Field) || field.Offset != proof.Native.Offset ||
            !ReferenceEquals(capture.Type, proof.Field.FieldType) ||
            !PrivateLocal(method, capture, X64WideFieldLow32ReadProof.CaptureRegister) ||
            conversion is not { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable result, LocalVariable source, Immediate { Value: 32 }, Immediate { Value: 32 }, Immediate signed] } ||
            conversion.NativeAddress != proof.Native.Load.IP || !ReferenceEquals(source, capture) ||
            signed.Value != (ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type) ? 1 : 0) ||
            !ReferenceEquals(result.Type, method.ReturnType) ||
            !PrivateLocal(method, result, X64WideFieldLow32ReadProof.ResultRegister) ||
            !IntegerExtension.TryGet(conversion, out var extension) || !extension.HasCanonicalTypes(conversion, method.AppContext) ||
            ret is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable returned] } || !ReferenceEquals(returned, result) ||
            ret.NativeAddress != proof.Native.Return.IP || OperandEffects.LocalsWithMutableStorage(block.Instructions).Count != 0 ||
            block.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, receiver)) != 1 ||
            block.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, capture)) != 1 ||
            block.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, result)) != 1)
            return null;
        return new Binding(proof, [read, conversion, ret], field, receiver, capture, result, capture.Register, result.Register);
    }

    private static bool PrivateLocal(MethodAnalysisContext method, LocalVariable local, string name) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) &&
        local.Register.Copy() == new Register(null, name) && local.Register.Version >= 0 &&
        method.Locals.Count(item => ReferenceEquals(item, local)) == 1 &&
        !method.Locals.Any(item => !ReferenceEquals(item, local) && item.Register.Number == local.Register.Number) &&
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
            // RemoveUnused can omit this from Locals; emission still maps the
            // unique ParameterLocal to the original managed instance argument.
            method.Locals.Count(item => ReferenceEquals(item, local)) > 1 ||
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
