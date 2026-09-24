using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    /// <summary>
    /// The native proof replaces a null-helper arm and a tail GC card marker with
    /// one managed reference-field store. Confirm that analysis retained every
    /// preceding field effect, the captured value, and the original store order.
    /// </summary>
    internal static void ValidateComposedReferenceFieldStore(MethodAnalysisContext method)
    {
        if (method.ComposedReferenceFieldStoreEvidence is not { } evidence)
            return;

        // The cached evidence is only a candidate. A metadata or native change
        // between lifting and emission must not keep authorizing the substitution.
        var current = X64ComposedReferenceFieldStoreProof.Find(method,
            X86Utils.Iterate(method).ToArray());
        if (current == null ||
            !ReferenceEquals(current.MarkerField, evidence.MarkerField) ||
            !ReferenceEquals(current.SourceField, evidence.SourceField) ||
            !ReferenceEquals(current.DestinationField, evidence.DestinationField) ||
            current.MarkerIncrementIp != evidence.MarkerIncrementIp ||
            current.SourceReadIp != evidence.SourceReadIp ||
            current.DestinationStoreIp != evidence.DestinationStoreIp ||
            current.BarrierTailIp != evidence.BarrierTailIp ||
            current.NullHelperCallIp != evidence.NullHelperCallIp ||
            current.NativeEndExclusiveIp != evidence.NativeEndExclusiveIp)
            throw ComposedStoreFailure("the complete native store proof changed");

        var graph = method.ControlFlowGraph;
        if (graph == null || graph.EntryBlock.Instructions.Count != 0 ||
            graph.ExitBlock.Instructions.Count != 0 ||
            LinearInstructions(graph) is not { } linear)
            throw ComposedStoreFailure("the final control flow is not a single proved path");

        // No additional operation may move the implicit fault, change the stored
        // value, or introduce an unproved observable effect. Removed NOPs carry no
        // operands, width, or call semantics.
        if (linear.Any(instruction => instruction.OpCode == OpCode.Nop &&
                (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                 instruction.CallSemantics != CallSemantics.Direct)))
            throw ComposedStoreFailure("a removed instruction retains unproved semantics");
        var active = linear.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        if (active is not [var increment, var capture, var store, var ret] ||
            increment is not
            {
                OpCode: OpCode.Add, IntegerBitWidth: 32,
                CallSemantics: CallSemantics.Direct,
                Operands: [FieldReference markerWrite, FieldReference markerRead,
                    Immediate { Value: 1 }]
            } ||
            capture is not
            {
                OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable capturedValue, FieldReference sourceRead]
            } ||
            store is not
            {
                OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [FieldReference destinationWrite, LocalVariable storedValue]
            } ||
            ret is not
            {
                OpCode: OpCode.Return, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands.Count: 0
            } ||
            increment.NativeAddress != evidence.MarkerIncrementIp ||
            capture.NativeAddress != evidence.SourceReadIp ||
            store.NativeAddress != evidence.DestinationStoreIp ||
            ret.NativeAddress != evidence.BarrierTailIp)
            throw ComposedStoreFailure("the final operations lost their proved native order or identity");

        var thisLocals = method.ParameterLocals.Where(local => local.IsThis).ToArray();
        var argumentLocals = method.ParameterLocals
            .Where(local => !local.IsThis && !local.IsMethodInfo).ToArray();
        if (method.IsStatic || !method.IsVoid || method.Parameters.Count != 1 ||
            method.ParameterOperands.Count < 2 ||
            method.ParameterOperands[0] is not Register thisRegister ||
            method.ParameterOperands[1] is not Register holderRegister ||
            thisLocals is not [{ } receiver] ||
            argumentLocals is not [{ } holder] ||
            receiver.Register.Number != thisRegister.Number || receiver.Register.Version != -1 ||
            holder.Register.Number != holderRegister.Number || holder.Register.Version != -1 ||
            !ReferenceEquals(receiver.Type, method.DeclaringType) ||
            !ReferenceEquals(holder.Type, method.Parameters[0].ParameterType) ||
            !ReferenceEquals(holder.Type, evidence.DestinationField.DeclaringType) ||
            ReferenceEquals(receiver, holder))
            throw ComposedStoreFailure("the native receivers no longer bind to the managed arguments");

        if (!ReferenceEquals(markerWrite.Field, evidence.MarkerField) ||
            !ReferenceEquals(markerRead.Field, evidence.MarkerField) ||
            !ReferenceEquals(markerWrite.Local, receiver) ||
            !ReferenceEquals(markerRead.Local, receiver) ||
            !ReferenceEquals(sourceRead.Field, evidence.SourceField) ||
            !ReferenceEquals(sourceRead.Local, receiver) ||
            !ReferenceEquals(destinationWrite.Field, evidence.DestinationField) ||
            !ReferenceEquals(destinationWrite.Local, holder) ||
            !ReferenceEquals(evidence.MarkerField.DeclaringType, method.DeclaringType) ||
            !ReferenceEquals(evidence.SourceField.DeclaringType, method.DeclaringType) ||
            markerWrite.Offset != evidence.MarkerField.Offset ||
            markerRead.Offset != evidence.MarkerField.Offset ||
            sourceRead.Offset != evidence.SourceField.Offset ||
            destinationWrite.Offset != evidence.DestinationField.Offset ||
            !ReferenceEquals(evidence.MarkerField.FieldType,
                method.AppContext.SystemTypes.SystemInt32Type) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(markerWrite, 32) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(markerRead, 32) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(sourceRead) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(destinationWrite))
            throw ComposedStoreFailure("a field no longer matches its proved owner, type, width, or layout");

        if (!NullCheckedCall.SameOrdinaryType(evidence.SourceField.FieldType,
                evidence.DestinationField.FieldType) ||
            !NullCheckedCall.SameOrdinaryType(capturedValue.Type,
                evidence.SourceField.FieldType) ||
            !NullCheckedCall.SameOrdinaryType(storedValue.Type,
                evidence.DestinationField.FieldType) ||
            !ReachesDefinition(linear, storedValue, linear.IndexOf(store), capture) ||
            graph.Instructions.Any(instruction =>
                ReferenceEquals(instruction.Destination, receiver) ||
                ReferenceEquals(instruction.Destination, holder)))
            throw ComposedStoreFailure("the destination lost its captured source value or argument identity");
    }

    private static DecompilerException ComposedStoreFailure(string detail) =>
        new("Composed reference-field store proof no longer matches final managed operations: " + detail);
}
