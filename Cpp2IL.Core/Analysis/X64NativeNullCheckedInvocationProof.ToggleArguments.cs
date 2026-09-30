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
        if (!TryBooleanPredicateArgument(caller, value, comparison, type, out var predicate) ||
            predicate.Capture.Operands is not [LocalVariable, FieldReference access] ||
            ReadBody(caller, out var body, out _) is not { } values)
            return false;
        var capture = predicate.Capture;
        var stores = caller.ControlFlowGraph!.Instructions.Where(operation => operation is
            { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                NativeAddress: not null, Operands: [FieldReference destination, LocalVariable stored] } &&
            ReferenceEquals(destination.Field, access.Field) && destination.Offset == access.Offset &&
            CurrentOwnerBooleanField(caller, destination, operation) &&
            ReachesBooleanPredicate(caller, stored, operation, comparison)).ToArray();
        if (stores is not [{ } store] || !InvocationOperationPrecedes(caller, comparison, store) ||
            !BindToggleDefinition(comparison, capture, store, access,
                body, values, out var predicateAddress, out _)) return false;
        argument = new(comparison, capture, store, access.Field, access.Offset, predicateAddress,
            BooleanFieldDeclaration(access));
        return true;
    }

    private static bool BindToggleDefinition(Instruction comparison, Instruction capture, Instruction store,
        FieldReference access, NativeInstruction[] body,
        X64NativeInvocationValues values, out ulong predicateAddress, out NativeSource source)
    {
        predicateAddress = 0;
        source = default;
        if (store.NativeAddress is not { } storedAt ||
            !BindBooleanFieldPredicate(comparison, capture, access, body, values, out var predicate, out source) ||
            predicate.NextIP != storedAt ||
            body.SingleOrDefault(native => native.IP == storedAt) is not
                { Code: Code.Mov_rm8_r8, Op0Kind: OpKind.Memory, Op1Kind: OpKind.Register } write ||
            write.Op1Register != predicate.Op0Register || write.MemoryIndex != NativeRegister.None ||
            write.MemorySize.GetSize() != 1 || write.MemoryDisplacement64 != (ulong)access.Offset ||
            !values.Matches(storedAt, write.MemoryBase, 64, new(NativeRegister.RCX)) ||
            !values.Dominates(predicate.IP, storedAt))
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
            !InvocationOperationPrecedes(caller, argument.Store, receiver.Definition) ||
            !values.Dominates(storedAt, loadedAt) || !values.Dominates(loadedAt, use) ||
            argument.Capture.Operands is not [LocalVariable, FieldReference access] ||
            !BindToggleDefinition(argument.Comparison, argument.Capture, argument.Store, access,
                body, values, out var predicateAddress, out source) || predicateAddress != argument.PredicateAddress)
            return false;
        return true;
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
                        ReachesBooleanPredicate(caller, read, operation, toggle.Comparison)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, operation) &&
                        site.Arguments.Any(argument => argument.Toggle == toggle) &&
                        ReachesBooleanPredicate(caller, read, operation, toggle.Comparison))) continue;
                    return false;
                }
        }
        return true;
    }
}
