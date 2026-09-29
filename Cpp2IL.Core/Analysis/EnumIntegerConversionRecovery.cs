using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Binds native enum storage and both explicit managed conversion stages.</summary>
internal static class EnumIntegerConversionRecovery
{
    private const string BindingKey = "EnumIntegerConversionRecovery";
    private sealed record Binding(X64EnumIntegerConversionProof.Proof Proof, Instruction[] Operations,
        LocalVariable Incoming, Register IncomingRegister, TypeAnalysisContext IncomingType,
        LocalVariable[] Results, Register[] ResultRegisters, TypeAnalysisContext[] ResultTypes);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Binding>(BindingKey, null!);
        if (X64EnumIntegerConversionProof.GetEvidence(method) is { } original &&
            X64EnumIntegerConversionProof.Find(method) is { } current && original.Matches(current) &&
            TryBind(method, current) is { } binding)
            method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        X64EnumIntegerConversionProof.WasLifted(method) || X64EnumIntegerConversionProof.GetEvidence(method) != null ||
        method.GetExtraData<Binding>(BindingKey) != null || method.Locals.Any(Owned) ||
        method.ControlFlowGraph?.Instructions.SelectMany(OperandEffects.ReadLocals).Any(Owned) == true;

    private static bool Owned(LocalVariable local) => local.Register.Name is
        X64EnumIntegerConversionProof.CaptureRegister or X64EnumIntegerConversionProof.ResultRegister;

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        method.GetExtraData<Binding>(BindingKey) is { } saved &&
        X64EnumIntegerConversionProof.GetEvidence(method) is { } original && original.Matches(saved.Proof) &&
        X64EnumIntegerConversionProof.Find(method) is { } current && current.Matches(saved.Proof) &&
        TryBind(method, current) is { } actual && actual.Operations.SequenceEqual(saved.Operations) &&
        ReferenceEquals(actual.Incoming, saved.Incoming) && actual.IncomingRegister == saved.IncomingRegister &&
        ReferenceEquals(actual.IncomingType, saved.IncomingType) && actual.Results.SequenceEqual(saved.Results) &&
        actual.ResultRegisters.SequenceEqual(saved.ResultRegisters) && actual.ResultTypes.SequenceEqual(saved.ResultTypes);

    private static Binding? TryBind(MethodAnalysisContext method, X64EnumIntegerConversionProof.Proof proof)
    {
        if (LinearBody(method) is not { } body ||
            body.Instructions.Any(operation => operation.OpCode == OpCode.Nop &&
                (operation.Operands.Count != 0 || operation.IntegerBitWidth != 0 || operation.CallSemantics != CallSemantics.Direct)) ||
            body.Instructions.Where(operation => operation.OpCode != OpCode.Nop).ToArray() is not { } active ||
            active.Length != (proof.ConvertSign ? 3 : 2) || !Incoming(method, proof, out var source) ||
            active[0].Operands.FirstOrDefault() is not LocalVariable capture ||
            !Conversion(method, active[0], capture, source, proof.Native.Width, proof.Native.Signed,
                proof.Native.Extension.IP) || !Private(method, capture, X64EnumIntegerConversionProof.CaptureRegister) ||
            !ReferenceEquals(capture.Type, proof.Native.Signed ? method.AppContext.SystemTypes.SystemInt32Type :
                method.AppContext.SystemTypes.SystemUInt32Type) ||
            active[^1] is not { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable returned] } ret || ret.NativeAddress != proof.Native.Return.IP ||
            !ReferenceEquals(returned.Type, method.ReturnType))
            return null;
        var results = new[] { capture };
        if (proof.ConvertSign)
        {
            if (!Conversion(method, active[1], returned, capture, 32, proof.ReturnSigned, proof.Native.Extension.IP) ||
                !Private(method, returned, X64EnumIntegerConversionProof.ResultRegister))
                return null;
            results = [capture, returned];
        }
        else if (!ReferenceEquals(returned, capture))
            return null;
        var expected = new[] { source }.Concat(results).ToArray();
        var reads = body.Instructions.SelectMany(OperandEffects.ReadLocals).ToArray();
        if (reads.Any(local => !expected.Contains(local)) ||
            expected.Any(local => reads.Count(value => ReferenceEquals(value, local)) != 1) ||
            OperandEffects.LocalsWithMutableStorage(body.Instructions).Count != 0)
            return null;
        return new Binding(proof, active, source, source.Register, source.Type!, results,
            results.Select(local => local.Register).ToArray(), results.Select(local => local.Type!).ToArray());
    }

    private static bool Conversion(MethodAnalysisContext method, Instruction operation, LocalVariable destination,
        LocalVariable source, int width, bool signed, ulong address) =>
        operation is { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [LocalVariable result, LocalVariable value, Immediate sourceBits,
                Immediate { Value: 32 }, Immediate sign] } &&
        ReferenceEquals(result, destination) && ReferenceEquals(value, source) && sourceBits.Value == width &&
        sign.Value == (signed ? 1 : 0) && operation.NativeAddress == address &&
        IntegerExtension.TryGet(operation, out var extension) && extension.HasCanonicalTypes(operation, method.AppContext);

    private static bool Incoming(MethodAnalysisContext method, X64EnumIntegerConversionProof.Proof proof,
        out LocalVariable source)
    {
        source = null!;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (!expected.SequenceEqual(method.ParameterOperands) || expected is not
                [Register { Name: "rcx", Version: -1 } register, Register { Name: "rdx", Version: -1 }] ||
            method.ParameterLocals.ToArray() is not [var incoming] || incoming.IsThis || incoming.IsMethodInfo ||
            incoming.Register != register || !ReferenceEquals(incoming.Type, proof.Storage.Enum) ||
            !ReferenceEquals(incoming.Type, method.Parameters[0].ParameterType) ||
            method.Locals.Count(local => ReferenceEquals(local, incoming)) > 1 ||
            method.Locals.Any(local => local.Register.Number == register.Number && !ReferenceEquals(local, incoming)) ||
            method.ControlFlowGraph!.Instructions.Any(operation => ReferenceEquals(operation.Destination, incoming)))
            return false;
        // RemoveUnused can omit an incoming value from temporary Locals; the
        // sole original ParameterLocal still supplies the managed ldarg binding.
        source = incoming;
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
