using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private static bool TryFieldReferenceProducer(MethodAnalysisContext caller, Instruction operation,
        out Origin receiver)
    {
        receiver = null!;
        return operation is { OpCode: OpCode.Call, IntegerBitWidth: 0, NativeAddress: not null,
                   Operands: [MethodAnalysisContext target, LocalVariable result, LocalVariable provider, ..] } &&
               operation.CallSemantics is CallSemantics.Direct or CallSemantics.NullCheckedInstance &&
               (operation.Operands.Count == 3 || operation.Operands.Count == 4 &&
                   operation.Operands[3] is Immediate { Value: 0 }) &&
               target.Parameters.Count == 0 && ReferenceEquals(result.Type, target.ReturnType) &&
               OrdinaryClass(target.ReturnType) && !Escaped(caller, result) &&
               TryOrigin(caller, provider, operation, out receiver) &&
               receiver is { Field: not null, Definition: not null, SourceEntry: -1 } &&
               EligibleTarget(caller, target, receiver.Type);
    }

    // The producer's removed null guard needs its own retained Site. Its target
    // signature alone cannot authenticate the captured provider or returned object.
    internal static bool IsReferenceProducerForInvocation(MethodAnalysisContext caller, Instruction operation)
    {
        if (!TryFieldReferenceProducer(caller, operation, out var provider) ||
            operation.NativeAddress is not { } produced || provider.Definition?.NativeAddress is not { } capture ||
            ReadBody(caller, out var body, out _) is not { } values || !values.Dominates(capture, produced) ||
            !InvocationOperationPrecedes(caller, provider.Definition, operation) ||
            !BindInvocation(caller, operation, (MethodAnalysisContext)operation.Operands[0], provider,
                body, values, out var producerArguments) || producerArguments.Length != 0)
            return false;
        foreach (var invocation in caller.ControlFlowGraph!.Instructions)
            if (invocation is { OpCode: OpCode.CallVoid, NativeAddress: { } use,
                    Operands: [MethodAnalysisContext target, LocalVariable receiver, var operand, ..] } &&
                target.IsVoid && target.Parameters.Count == 1 &&
                ReferenceEquals(target.Parameters[0].ParameterType, caller.AppContext.SystemTypes.SystemBooleanType) &&
                TryOrigin(caller, receiver, invocation, out var result) &&
                ReferenceEquals(result.Definition, operation) && ReferenceEquals(result.Producer, operation.Operands[0]) &&
                EligibleTarget(caller, target, result.Type) &&
                body.SingleOrDefault(native => native.IP == use) is { Code: Code.Jmp_rel32_64 } native &&
                native.NearBranchTarget == target.UnderlyingPointer && values.HasCallFrame(use, true) &&
                values.Dominates(produced, use) && InvocationOperationPrecedes(caller, operation, invocation) &&
                values.Matches(use, NativeRegister.RCX, 64, new(NativeRegister.None, produced, NativeRegister.RAX)) &&
                TryArgument(caller, operand, invocation, target.Parameters[0].ParameterType, out var source, out var argument) &&
                (argument.Entry != null || argument.Literal != null) &&
                values.Matches(use, NativeRegister.RDX, 8, source))
                return true;
        return false;
    }

    private static bool BindFieldReferenceProducer(MethodAnalysisContext caller, Origin result, Origin provider,
        NativeInstruction[] body, X64NativeInvocationValues values, ulong use, NativeRegister register)
    {
        var operation = result.Definition!;
        var target = result.Producer!;
        if (operation.NativeAddress is not { } produced ||
            operation.CallSemantics != CallSemantics.NullCheckedInstance ||
            !ReferenceEquals(operation.Operands[0], target) || !ReferenceEquals(target.ReturnType, result.Type) ||
            provider.Definition?.NativeAddress is not { } capture || !values.Dominates(capture, produced) ||
            !InvocationOperationPrecedes(caller, provider.Definition, operation) ||
            body.SingleOrDefault(native => native.IP == produced) is not { Code: Code.Call_rel32_64 } native ||
            native.NearBranchTarget != target.UnderlyingPointer ||
            caller.GetExtraData<List<Site>>(EvidenceKey)?.Where(site =>
                ReferenceEquals(site.Invocation, operation)).ToArray() is not [{ } retained] ||
            !ReferenceEquals(retained.Target, target) || retained.Receiver != provider || retained.Arguments.Length != 0 ||
            !body.SequenceEqual(retained.Body) || !RewrittenGuard(caller, retained) ||
            !ReceiverDeclarationsRetained(caller, provider, target, retained.ReceiverDeclarations) ||
            !TryGuard(caller, body, values, provider, retained.Comparison, retained.Branch, operation,
                out var nullCall, out var helper, retained.Helper, retained.NullCall) ||
            nullCall != retained.NullCall || helper.NativeTarget != retained.Helper.NativeTarget ||
            !BindInvocation(caller, operation, target, provider, body, values, out var arguments, retained.Comparison) ||
            arguments.Length != 0 || !values.Dominates(produced, use))
            return false;
        return values.Matches(use, register, 64, new(NativeRegister.None, produced, NativeRegister.RAX));
    }

    private static bool TryReferenceProducerDeclarations(Origin result, Origin provider,
        MethodAnalysisContext target, out ValueKey? declarations)
    {
        declarations = null;
        if (result.Producer is not { } producer || provider.Field?.BackingData?.Field is not { } field ||
            target.DeclaringType is not { } owner || !ReferenceHierarchyKey(result.Type, owner, out var returned) ||
            !ReferenceHierarchyKey(provider.Type, producer.DeclaringType!, out var source) ||
            !ReferenceDeclarationKey(provider.Field.DeclaringType, out var fieldOwner)) return false;
        declarations = new("field-reference-producer", result.Definition,
            [MethodKey(producer), ReferenceRawTypeKey(producer.Definition?.RawReturnType), returned, source, fieldOwner,
                new("field", field, [new("name-index", field.nameIndex, []),
                    new("type-index", field.typeIndex.Value, []), new("token", field.token, []),
                    new("type", provider.Field.FieldType, []), new("offset", provider.Field.Offset, []),
                    ReferenceRawTypeKey(field.RawFieldType)])]);
        return true;
    }

    private static bool ReferenceProducerUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var returned = sites.Select(site => site.Receiver).Where(receiver => receiver.Producer != null &&
            receiver.Definition != null && TryFieldReferenceProducer(caller, receiver.Definition, out _)).Distinct().ToArray();
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        foreach (var origin in returned)
        {
            if (origin.Definition!.Destination is not LocalVariable result) return false;
            var copies = new HashSet<LocalVariable> { result };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var operation in operations)
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } && copies.Contains(source))
                    {
                        if (!ReferenceEquals(destination.Type, origin.Type)) return false;
                        changed |= copies.Add(destination);
                    }
            }
            foreach (var operation in operations)
                foreach (var read in OperandEffects.ReadLocals(operation).Where(copies.Contains))
                {
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } &&
                        ReferenceEquals(read, source) && ReferenceEquals(destination.Type, origin.Type)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, operation) && site.Receiver == origin &&
                        ReferenceEquals(operation.Operands[operation.OpCode == OpCode.Call ? 2 : 1], read) &&
                        TryOrigin(caller, read, operation, out var current) && current == origin)) continue;
                    if (operation is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                            Operands: [LocalVariable, LocalVariable compared, Immediate { Value: 0 }] } &&
                        ReferenceEquals(read, compared) && sites.Any(site => site.Receiver == origin &&
                            site.Comparison == operation.NativeAddress)) continue;
                    return false;
                }
        }
        return true;
    }
}
