using System;
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
    private sealed record BooleanToggleArgument(Instruction Comparison, Instruction Capture, Instruction Store,
        FieldAnalysisContext Field, int Offset, ulong PredicateAddress, ValueKey Declaration)
    {
        public bool Equals(BooleanToggleArgument? other) => other != null &&
            ReferenceEquals(Comparison, other.Comparison) && ReferenceEquals(Capture, other.Capture) &&
            ReferenceEquals(Store, other.Store) && ReferenceEquals(Field, other.Field) &&
            Offset == other.Offset && PredicateAddress == other.PredicateAddress && SameKey(Declaration, other.Declaration);

        public override int GetHashCode() => Comparison.GetHashCode();
    }

    // This origin is a single captured owner byte compared with zero. The same
    // 0/1 predicate is stored back before the target is loaded or checked, and
    // remains the call argument even if the field is subsequently changed.
    private static bool TryBooleanToggleArgument(MethodAnalysisContext caller, LocalVariable value,
        Instruction comparison, TypeAnalysisContext type, out BooleanToggleArgument argument)
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
            ReadBody(caller, out var body, out _) is not { } values)
            return false;
        var stores = caller.ControlFlowGraph!.Instructions.Where(operation => operation is
            { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                NativeAddress: not null, Operands: [FieldReference destination, LocalVariable stored] } &&
            ReferenceEquals(destination.Field, access.Field) && destination.Offset == access.Offset &&
            CurrentOwnerBooleanField(caller, destination, operation) &&
            ReachesTogglePredicate(caller, stored, operation, comparison)).ToArray();
        if (stores is not [{ } store] || !TogglePrecedes(caller, comparison, store) ||
            !BindToggleDefinition(comparison, capture, store, access,
                body, values, out var predicateAddress, out _)) return false;
        argument = new(comparison, capture, store, access.Field, access.Offset, predicateAddress,
            ToggleFieldDeclaration(access));
        return true;
    }

    private static bool CurrentOwnerBooleanField(MethodAnalysisContext caller, FieldReference access, Instruction use) =>
        ReferenceEquals(access.Field.FieldType, caller.AppContext.SystemTypes.SystemBooleanType) &&
        ReferenceEquals(access.Field.DeclaringType, caller.DeclaringType) && AccessibleField(caller, access.Field) &&
        NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 8) &&
        TryOrigin(caller, access.Local, use, out var owner) && owner.Definition == null && owner.Entry == -1 &&
        ReferenceEquals(owner.Type, caller.DeclaringType);

    private static bool ReachesTogglePredicate(MethodAnalysisContext caller, LocalVariable value,
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

    private static bool BindToggleDefinition(Instruction comparison, Instruction capture, Instruction store,
        FieldReference access, NativeInstruction[] body,
        X64NativeInvocationValues values, out ulong predicateAddress, out NativeSource source)
    {
        predicateAddress = 0;
        source = default;
        if (comparison.NativeAddress is not { } address || capture.NativeAddress != address ||
            store.NativeAddress is not { } storedAt ||
            body.SingleOrDefault(native => native.IP == address) is not
                { Code: Code.Cmp_rm8_imm8, Op0Kind: OpKind.Memory, Op1Kind: OpKind.Immediate8, Immediate8: 0 } compare ||
            compare.MemoryIndex != NativeRegister.None || compare.MemorySize.GetSize() != 1 ||
            compare.MemoryDisplacement64 != (ulong)access.Offset ||
            !values.Matches(address, compare.MemoryBase, 64, new(NativeRegister.RCX)) ||
            body.SingleOrDefault(native => native.IP == compare.NextIP) is not
                { Code: Code.Sete_rm8, Op0Kind: OpKind.Register } predicate ||
            predicate.Op0Register.GetSize() != 1 ||
            predicate.Op0Register is NativeRegister.AH or NativeRegister.BH or NativeRegister.CH or NativeRegister.DH ||
            predicate.NextIP != storedAt ||
            body.SingleOrDefault(native => native.IP == storedAt) is not
                { Code: Code.Mov_rm8_r8, Op0Kind: OpKind.Memory, Op1Kind: OpKind.Register } write ||
            write.Op1Register != predicate.Op0Register || write.MemoryIndex != NativeRegister.None ||
            write.MemorySize.GetSize() != 1 || write.MemoryDisplacement64 != (ulong)access.Offset ||
            !values.Matches(storedAt, write.MemoryBase, 64, new(NativeRegister.RCX)) ||
            !values.Dominates(address, predicate.IP) || !values.Dominates(predicate.IP, storedAt))
            return false;
        predicateAddress = predicate.IP;
        source = new(NativeRegister.None, predicate.IP, predicate.Op0Register.GetFullRegister());
        return true;
    }

    private static bool BindBooleanToggleArgument(MethodAnalysisContext caller, BooleanToggleArgument argument,
        Origin receiver, NativeInstruction[] body, X64NativeInvocationValues values, ulong use, out NativeSource source)
    {
        source = default;
        if (argument.Comparison.Destination is not LocalVariable predicate ||
            !TryBooleanToggleArgument(caller, predicate, argument.Comparison,
                caller.AppContext.SystemTypes.SystemBooleanType, out var current) || current != argument ||
            receiver.Field == null || receiver.SourceEntry != -1 || receiver.Definition?.NativeAddress is not { } loadedAt ||
            !ReferenceEquals(receiver.Field.DeclaringType, caller.DeclaringType) ||
            argument.Store.NativeAddress is not { } storedAt || storedAt >= loadedAt ||
            !TogglePrecedes(caller, argument.Store, receiver.Definition) ||
            !values.Dominates(storedAt, loadedAt) || !values.Dominates(loadedAt, use) ||
            argument.Capture.Operands is not [LocalVariable, FieldReference access] ||
            !BindToggleDefinition(argument.Comparison, argument.Capture, argument.Store, access,
                body, values, out var predicateAddress, out source) || predicateAddress != argument.PredicateAddress)
            return false;
        return true;
    }

    private static bool TogglePrecedes(MethodAnalysisContext caller, Instruction before, Instruction after) =>
        caller.ControlFlowGraph is { } graph && graph.FindBlockByInstruction(before) is { } beforeBlock &&
        graph.FindBlockByInstruction(after) is { } afterBlock &&
        (ReferenceEquals(beforeBlock, afterBlock)
            ? beforeBlock.Instructions.IndexOf(before) < beforeBlock.Instructions.IndexOf(after)
            : new DominatorInfo(graph).Dominates(beforeBlock, afterBlock));

    private static ValueKey ToggleFieldDeclaration(FieldReference access)
    {
        var field = access.Field;
        var definition = field.BackingData!.Field;
        return new("toggle-field", field,
            [new("definition", definition, []), new("name-index", definition.nameIndex, []),
                new("type-index", definition.typeIndex.Value, []), new("token", definition.token, []),
                new("name", field.Name, []), new("attributes", field.Attributes, []),
                new("type", field.FieldType, []), new("offset", access.Offset, []),
                ReferenceRawTypeKey(definition.RawFieldType), ReferenceDeclarationFacts(field.DeclaringType)]);
    }

    private static bool BooleanToggleArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var toggles = sites.SelectMany(site => site.Arguments).Select(argument => argument.Toggle)
            .OfType<BooleanToggleArgument>().Distinct().ToArray();
        foreach (var toggle in toggles)
        {
            var copies = new HashSet<LocalVariable> { (LocalVariable)toggle.Comparison.Destination! };
            var operations = caller.ControlFlowGraph!.Instructions.ToArray();
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
                    if (ReferenceEquals(operation, toggle.Store) &&
                        ReachesTogglePredicate(caller, read, operation, toggle.Comparison)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, operation) &&
                        site.Arguments.Any(argument => argument.Toggle == toggle) &&
                        ReachesTogglePredicate(caller, read, operation, toggle.Comparison))) continue;
                    return false;
                }
        }
        return true;
    }
}
