using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using NativeSource = Cpp2IL.Core.Analysis.X64NativeInvocationValues.Source;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private sealed record BooleanPredicateArgument(Instruction Comparison, Instruction Capture,
        FieldAnalysisContext Field, int Offset, ulong PredicateAddress, ValueKey Declaration)
    {
        public bool Equals(BooleanPredicateArgument? other) => other != null &&
            ReferenceEquals(Comparison, other.Comparison) && ReferenceEquals(Capture, other.Capture) &&
            ReferenceEquals(Field, other.Field) && Offset == other.Offset &&
            PredicateAddress == other.PredicateAddress && SameKey(Declaration, other.Declaration);

        public override int GetHashCode() => Comparison.GetHashCode();
    }

    // Keep the captured byte, its zero comparison and the SETE value distinct.
    // A later owner read can observe an earlier call through an aliased receiver;
    // the ordered effect snapshots bind each capture to its original call side.
    private static bool TryBooleanPredicateArgument(MethodAnalysisContext caller, LocalVariable value,
        Instruction comparison, TypeAnalysisContext type, out BooleanPredicateArgument argument)
    {
        argument = null!;
        if (!ReferenceEquals(type, caller.AppContext.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(value.Type, type) || Escaped(caller, value) ||
            comparison is not { OpCode: OpCode.CheckEqual, IntegerBitWidth: 8, CallSemantics: CallSemantics.Direct,
                NativeAddress: { } address, Operands: [LocalVariable predicate, LocalVariable captured, Immediate { Value: 0 }] } ||
            !ReferenceEquals(predicate, value) || !ReferenceEquals(captured.Type, type) || Escaped(caller, captured) ||
            !ReachingDefinition(caller, captured, comparison, out var capture) ||
            capture is not { OpCode: OpCode.Move, IntegerBitWidth: 8, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable loaded, FieldReference access] } ||
            !ReferenceEquals(loaded, captured) || capture.NativeAddress != address ||
            !CurrentOwnerBooleanField(caller, access, capture) ||
            ReadBody(caller, out var body, out _) is not { } values ||
            !BindBooleanFieldPredicate(comparison, capture, access, body, values, out var native, out _))
            return false;
        argument = new(comparison, capture, access.Field, access.Offset, native.IP, BooleanFieldDeclaration(access));
        return true;
    }

    private static bool CurrentOwnerBooleanField(MethodAnalysisContext caller, FieldReference access, Instruction use) =>
        ReferenceEquals(access.Field.FieldType, caller.AppContext.SystemTypes.SystemBooleanType) &&
        ReferenceEquals(access.Field.DeclaringType, caller.DeclaringType) && AccessibleField(caller, access.Field) &&
        NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 8) &&
        TryOrigin(caller, access.Local, use, out var owner) && owner.Definition == null && owner.Entry == -1 &&
        ReferenceEquals(owner.Type, caller.DeclaringType);

    private static bool ReachesBooleanPredicate(MethodAnalysisContext caller, LocalVariable value,
        Instruction use, Instruction comparison)
    {
        var seen = new HashSet<LocalVariable>();
        while (seen.Add(value))
        {
            if (!ReferenceEquals(value.Type, caller.AppContext.SystemTypes.SystemBooleanType) || Escaped(caller, value) ||
                !ReachingDefinition(caller, value, use, out var definition)) return false;
            if (ReferenceEquals(definition, comparison)) return ReferenceEquals(comparison.Destination, value);
            if (definition is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, LocalVariable source] } ||
                !ReferenceEquals(destination, value)) return false;
            value = source;
            use = definition;
        }
        return false;
    }

    private static bool InvocationOperationPrecedes(MethodAnalysisContext caller, Instruction before, Instruction after) =>
        caller.ControlFlowGraph is { } graph && graph.FindBlockByInstruction(before) is { } beforeBlock &&
        graph.FindBlockByInstruction(after) is { } afterBlock &&
        (ReferenceEquals(beforeBlock, afterBlock)
            ? beforeBlock.Instructions.IndexOf(before) < beforeBlock.Instructions.IndexOf(after)
            : new DominatorInfo(graph).Dominates(beforeBlock, afterBlock));

    private static ValueKey BooleanFieldDeclaration(FieldReference access)
    {
        var field = access.Field;
        var definition = field.BackingData!.Field;
        return new("boolean-field", field,
            [new("definition", definition, []), new("name-index", definition.nameIndex, []),
                new("type-index", definition.typeIndex.Value, []), new("token", definition.token, []),
                new("name", field.Name, []), new("attributes", field.Attributes, []),
                new("type", field.FieldType, []), new("offset", access.Offset, []),
                ReferenceRawTypeKey(definition.RawFieldType), ReferenceDeclarationFacts(field.DeclaringType)]);
    }

    private static bool BindBooleanFieldPredicate(Instruction comparison, Instruction capture, FieldReference access,
        NativeInstruction[] body, X64NativeInvocationValues values, out NativeInstruction predicate,
        out NativeSource source)
    {
        predicate = default;
        source = default;
        if (comparison.NativeAddress is not { } address || capture.NativeAddress != address ||
            body.SingleOrDefault(native => native.IP == address) is not
                { Code: Code.Cmp_rm8_imm8, Op0Kind: OpKind.Memory, Op1Kind: OpKind.Immediate8, Immediate8: 0 } compare ||
            compare.MemoryIndex != NativeRegister.None || compare.MemorySize.GetSize() != 1 ||
            compare.MemoryDisplacement64 != (ulong)access.Offset ||
            !values.Matches(address, compare.MemoryBase, 64, new(NativeRegister.RCX)))
            return false;
        var next = compare.NextIP;
        for (var count = 0; count < 8; count++)
        {
            var native = body.SingleOrDefault(instruction => instruction.IP == next);
            if (native is { Code: Code.Sete_rm8, Op0Kind: OpKind.Register })
            {
                if (native.Op0Register.GetSize() != 1 ||
                    native.Op0Register is NativeRegister.AH or NativeRegister.BH or NativeRegister.CH or NativeRegister.DH ||
                    !values.Dominates(address, native.IP)) return false;
                predicate = native;
                source = new(NativeRegister.None, native.IP, native.Op0Register.GetFullRegister());
                return true;
            }
            // Only register copies may sit between the comparison and SETE.
            // They cannot alter flags, read a new field, branch or call.
            if (native.Op0Kind != OpKind.Register || native.Op1Kind != OpKind.Register ||
                native.Code is not (Code.Mov_r8_rm8 or Code.Mov_rm8_r8 or Code.Mov_r16_rm16 or Code.Mov_rm16_r16 or
                    Code.Mov_r32_rm32 or Code.Mov_rm32_r32 or Code.Mov_r64_rm64 or Code.Mov_rm64_r64))
                return false;
            next = native.NextIP;
        }
        return false;
    }

    private static bool BindBooleanPredicateArgument(MethodAnalysisContext caller, BooleanPredicateArgument argument,
        Origin receiver, NativeInstruction[] body, X64NativeInvocationValues values, ulong use, out NativeSource source)
    {
        source = default;
        return receiver.Field != null && receiver.SourceEntry == -1 &&
               ReferenceEquals(receiver.Field.DeclaringType, caller.DeclaringType) &&
               receiver.Definition?.NativeAddress is { } receiverAddress &&
               argument.Capture.NativeAddress is { } captureAddress &&
               values.Dominates(receiverAddress, captureAddress) &&
               InvocationOperationPrecedes(caller, receiver.Definition, argument.Capture) &&
               argument.Comparison.Destination is LocalVariable predicate &&
               TryBooleanPredicateArgument(caller, predicate, argument.Comparison,
                   caller.AppContext.SystemTypes.SystemBooleanType, out var current) && current == argument &&
               argument.Capture.Operands is [LocalVariable, FieldReference access] &&
               BindBooleanFieldPredicate(argument.Comparison, argument.Capture, access, body, values,
                   out var native, out source) && native.IP == argument.PredicateAddress && values.Dominates(native.IP, use);
    }

    // Guard traversal may retain these two setup operations only when the same
    // checked receiver and a complete native invocation consume their exact
    // typed predicate. TryRecord still authenticates the guard and every effect.
    internal static bool IsBooleanPredicateArgumentSetup(MethodAnalysisContext caller, Instruction operation,
        LocalVariable checkedReceiver)
    {
        if (operation is not { OpCode: OpCode.Move, IntegerBitWidth: 8,
                Operands: [LocalVariable, FieldReference] } &&
            operation is not { OpCode: OpCode.CheckEqual, IntegerBitWidth: 8,
                Operands: [LocalVariable, LocalVariable, Immediate { Value: 0 }] } ||
            ReadBody(caller, out var body, out _) is not { } values) return false;
        foreach (var comparison in caller.ControlFlowGraph!.Instructions.Where(instruction =>
                     instruction is { OpCode: OpCode.CheckEqual, IntegerBitWidth: 8, Destination: LocalVariable }))
        {
            if (!TryBooleanPredicateArgument(caller, (LocalVariable)comparison.Destination!, comparison,
                    caller.AppContext.SystemTypes.SystemBooleanType, out var predicate) ||
                !ReferenceEquals(operation, predicate.Capture) && !ReferenceEquals(operation, comparison))
                continue;
            foreach (var invocation in caller.ControlFlowGraph.Instructions.Where(instruction =>
                         instruction.IsCall && instruction.CallSemantics == CallSemantics.Direct))
                if (TryGetCandidate(caller, invocation, out var target, out var receiver) &&
                    ReferenceEquals(receiver, checkedReceiver) && target.Parameters.Count == 1 &&
                    ReferenceEquals(target.Parameters[0].ParameterType, caller.AppContext.SystemTypes.SystemBooleanType) &&
                    TryOrigin(caller, receiver, invocation, out var origin) &&
                    BindInvocation(caller, invocation, target, origin, body, values, out var arguments) &&
                    arguments is [{ Predicate: { } bound }] && bound == predicate)
                    return true;
        }
        return false;
    }

    private static bool BooleanPredicateArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var predicates = sites.SelectMany(site => site.Arguments).Select(argument => argument.Predicate)
            .OfType<BooleanPredicateArgument>().Distinct().ToArray();
        foreach (var predicate in predicates)
        {
            var operations = caller.ControlFlowGraph!.Instructions.ToArray();
            if (predicate.Capture.Destination is not LocalVariable captured ||
                operations.Any(operation => !ReferenceEquals(operation, predicate.Comparison) &&
                    OperandEffects.ReadLocals(operation).Any(read => ReferenceEquals(read, captured))))
                return false;
            var copies = new HashSet<LocalVariable> { (LocalVariable)predicate.Comparison.Destination! };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var operation in operations)
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } && copies.Contains(source))
                    {
                        if (!ReferenceEquals(destination.Type, caller.AppContext.SystemTypes.SystemBooleanType)) return false;
                        changed |= copies.Add(destination);
                    }
            }
            foreach (var operation in operations)
                foreach (var read in OperandEffects.ReadLocals(operation).Where(copies.Contains))
                {
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } &&
                        ReferenceEquals(read, source) && ReferenceEquals(destination.Type, read.Type)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, operation) &&
                        site.Arguments.Any(argument => argument.Predicate == predicate) &&
                        ReachesBooleanPredicate(caller, read, operation, predicate.Comparison))) continue;
                    return false;
                }
        }
        return true;
    }
}
