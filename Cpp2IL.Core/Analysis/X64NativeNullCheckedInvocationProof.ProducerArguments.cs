using System.Collections.Generic;
using System.Linq;
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
    private sealed record ScalarProducerArgument(Instruction Definition, ulong Address,
        MethodAnalysisContext Target, TypeAnalysisContext Type, Origin Receiver);

    private static bool TryScalarProducerArgument(MethodAnalysisContext caller, LocalVariable value,
        Instruction definition, TypeAnalysisContext type, out ScalarProducerArgument argument)
    {
        argument = null!;
        if (!ReferenceEquals(type, caller.AppContext.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(value.Type, type) || Escaped(caller, value) ||
            definition is not { OpCode: OpCode.Call, IntegerBitWidth: 0, NativeAddress: { } address,
                Operands: [MethodAnalysisContext target, LocalVariable captured, LocalVariable receiver, ..] } ||
            !ReferenceEquals(captured, value) ||
            definition.CallSemantics is not (CallSemantics.Direct or CallSemantics.NullCheckedInstance) ||
            definition.Operands.Count != 3 && !(definition.Operands.Count == 4 &&
                definition.Operands[3] is Immediate { Value: 0 }) ||
            target.Parameters.Count != 0 || !ReferenceEquals(target.ReturnType, type) ||
            !TryOrigin(caller, receiver, definition, out var origin) ||
            origin is not { Field: not null, Definition: not null, SourceEntry: -1 } ||
            !EligibleTarget(caller, target, origin.Type))
            return false;
        argument = new(definition, address, target, type, origin);
        return true;
    }

    // Recording the producer's guard first is necessary even when its ordinary
    // typed signature would otherwise qualify for the legacy target-only route.
    internal static bool IsScalarProducerForInvocation(MethodAnalysisContext caller, Instruction operation)
    {
        if (operation is not { OpCode: OpCode.Call, Destination: LocalVariable produced } ||
            !TryScalarProducerArgument(caller, produced, operation,
                caller.AppContext.SystemTypes.SystemInt32Type, out var producer) ||
            ReadBody(caller, out var body, out _) is not { } values) return false;
        foreach (var invocation in caller.ControlFlowGraph!.Instructions.Where(instruction =>
                     instruction is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext, ..] }))
            if (invocation.Operands[0] is MethodAnalysisContext target && !target.IsStatic && target.IsVoid &&
                target.Parameters.Count == 1 && invocation.Operands.Count >= 3 &&
                ReferenceEquals(target.Parameters[0].ParameterType, producer.Type) &&
                invocation.Operands[1] is LocalVariable receiver &&
                TryOrigin(caller, receiver, invocation, out var origin) &&
                origin is { Field: not null, Definition: { NativeAddress: { } capture }, SourceEntry: -1 } &&
                EligibleTarget(caller, target, origin.Type) &&
                InvocationOperationPrecedes(caller, origin.Definition, operation) &&
                values.Dominates(capture, producer.Address) && invocation.NativeAddress is { } use &&
                body.SingleOrDefault(native => native.IP == use) is { Code: Code.Jmp_rel32_64 } native &&
                native.NearBranchTarget == target.UnderlyingPointer &&
                values.Matches(use, NativeRegister.RDX, 32,
                    new(NativeRegister.None, producer.Address, NativeRegister.RAX)) &&
                TryArgument(caller, invocation.Operands[2], invocation, producer.Type, out _, out var argument) &&
                argument.Producer == producer)
                return true;
        return false;
    }

    private static bool BindScalarProducerArgument(MethodAnalysisContext caller, Instruction invocation,
        Origin receiver, ScalarProducerArgument argument, NativeInstruction[] body, X64NativeInvocationValues values,
        ulong use, ulong? comparison, out NativeSource source)
    {
        source = default;
        if (argument.Definition.Destination is not LocalVariable produced ||
            !TryScalarProducerArgument(caller, produced, argument.Definition, argument.Type, out var current) ||
            current != argument || argument.Definition.CallSemantics != CallSemantics.NullCheckedInstance ||
            invocation is not { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext target, ..] } ||
            !target.IsVoid || target.Parameters.Count != 1 ||
            !ReferenceEquals(target.Parameters[0].ParameterType, argument.Type) ||
            receiver is not { Field: not null, Definition: { NativeAddress: { } capture }, SourceEntry: -1 } ||
            !values.Dominates(capture, argument.Address) ||
            !InvocationOperationPrecedes(caller, receiver.Definition, argument.Definition) ||
            comparison is not { } check || !values.Dominates(argument.Address, check) ||
            body.SingleOrDefault(native => native.IP == use).Code != Code.Jmp_rel32_64 ||
            body.SingleOrDefault(native => native.IP == argument.Address).Code != Code.Call_rel32_64 ||
            !values.Dominates(argument.Address, use) ||
            !InvocationOperationPrecedes(caller, argument.Definition, invocation) ||
            caller.GetExtraData<List<Site>>(EvidenceKey)?.Where(site =>
                ReferenceEquals(site.Invocation, argument.Definition)).ToArray() is not [{ } site] ||
            !ReferenceEquals(site.Target, argument.Target) || site.Receiver != argument.Receiver ||
            site.Arguments.Length != 0 || !RewrittenGuard(caller, site) ||
            !TryGuard(caller, body, values, argument.Receiver, site.Comparison, site.Branch,
                argument.Definition, out var nullCall, out var helper, site.Helper, site.NullCall) ||
            nullCall != site.NullCall || helper.NativeTarget != site.Helper.NativeTarget ||
            !BindInvocation(caller, argument.Definition, argument.Target, argument.Receiver,
                body, values, out var producerArguments, site.Comparison) || producerArguments.Length != 0)
            return false;
        source = new(NativeRegister.None, argument.Address, NativeRegister.RAX);
        return true;
    }

    private static bool ScalarProducerArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var producers = sites.SelectMany(site => site.Arguments).Select(argument => argument.Producer)
            .OfType<ScalarProducerArgument>().Distinct().ToArray();
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        foreach (var producer in producers)
        {
            if (producer.Definition.Destination is not LocalVariable captured) return false;
            var copies = new HashSet<LocalVariable> { captured };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var operation in operations)
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } && copies.Contains(source))
                    {
                        if (!ReferenceEquals(destination.Type, producer.Type)) return false;
                        changed |= copies.Add(destination);
                    }
            }
            foreach (var operation in operations)
                foreach (var read in OperandEffects.ReadLocals(operation).Where(copies.Contains))
                {
                    if (operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } &&
                        ReferenceEquals(read, source) && ReferenceEquals(destination.Type, producer.Type)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, operation) &&
                        site.Arguments.Any(argument => argument.Producer == producer) &&
                        TryArgument(caller, read, operation, producer.Type, out _, out var argument) &&
                        argument.Producer == producer)) continue;
                    return false;
                }
        }
        return true;
    }
}
