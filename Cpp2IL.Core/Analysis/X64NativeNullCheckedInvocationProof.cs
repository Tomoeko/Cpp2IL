using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using MemoryOperand = Cpp2IL.Core.ISIL.MemoryOperand;
using NativeSource = Cpp2IL.Core.Analysis.X64NativeInvocationValues.Source;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Authenticates a null-checked invocation at its original native site, without
/// replacing its producer, snapshot or surrounding effects. The managed and
/// native reaching definitions must agree on every path to both check and call.
/// </summary>
internal static partial class X64NativeNullCheckedInvocationProof
{
    internal const string EvidenceKey = "X64NativeNullCheckedInvocationProof";

    private sealed record Origin(int Entry, TypeAnalysisContext Type, Instruction? Definition = null,
        MethodAnalysisContext? Producer = null, FieldAnalysisContext? Field = null, int SourceEntry = -2,
        int Offset = 0);
    private readonly record struct Argument(int? Entry, long? Literal);
    private sealed record ValueKey(string Kind, object? Value, ValueKey[] Children);
    private sealed record OrderedEffect(Instruction Operation, ulong Address, bool Before, bool After,
        CallSemantics Semantics, ValueKey Value);
    private sealed record Control(Instruction Operation, ulong Address, OpCode Code, Block Owner,
        Block Taken, Block? Other, ValueKey? Condition);
    private sealed record Site(Instruction Invocation, MethodAnalysisContext Target, Origin Receiver,
        Argument[] Arguments, ulong Comparison, ulong Branch, ulong NullCall, RuntimeNullThrowEvidence Helper,
        Instruction GuardBranch, Block GuardOwner, Block NormalArm, NativeInstruction[] Body,
        OrderedEffect[] Effects, Control[] Controls, ReferenceStore[] Stores);

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<List<Site>>(EvidenceKey) != null;

    internal static bool HasTwoScalarParameters(MethodAnalysisContext target) =>
        target.Parameters.Count == 2 && target.Parameters.All(parameter => Scalar(parameter.ParameterType));

    internal static bool TryRecord(MethodAnalysisContext caller, Instruction comparison, Instruction branch,
        LocalVariable receiver, Instruction invocation, MethodAnalysisContext target)
    {
        try
        {
            if (comparison.NativeAddress is not { } comparisonIp || branch.NativeAddress is not { } branchIp ||
                comparison.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) ||
                comparison.IntegerBitWidth != 64 || comparison.Operands.Count != 3 ||
                !comparison.Operands.Contains(receiver) ||
                !comparison.Operands.OfType<Immediate>().Any(value => value.Value == 0) ||
                branch.OpCode != OpCode.ConditionalJump || branch.IntegerBitWidth != 0 ||
                invocation.CallSemantics != CallSemantics.Direct ||
                caller.ControlFlowGraph is not { } graph ||
                graph.FindBlockByInstruction(comparison) != graph.FindBlockByInstruction(branch) ||
                !ManagedGuard(caller, comparison, branch, receiver, out var guardOwner, out var normalArm) ||
                !TryGetCandidate(caller, invocation, out var called, out var calledReceiver) ||
                !ReferenceEquals(called, target) || !ReferenceEquals(calledReceiver, receiver) ||
                !TryOrigin(caller, receiver, invocation, out var origin) ||
                ReadBody(caller, out var body, out _) is not { } values)
                return false;
            var undo = new List<Action>();
            var admitted = false;
            try
            {
                if (!NormalizeReferenceStores(caller, body, values, undo, out var stores) ||
                    !TryGuard(caller, body, values, origin, comparisonIp, branchIp, invocation,
                        out var nullCall, out var helper) ||
                    graph.Instructions.Where(instruction => instruction.OpCode == OpCode.RuntimeNullThrow).ToArray() is not
                        [{ NativeAddress: { } onlyNullCall }] || onlyNullCall != nullCall ||
                    !BindInvocation(caller, invocation, target, origin, body, values, out var arguments) ||
                    !TryEffects(caller, invocation, out var effects) ||
                    !TryControls(caller, branch, body, out var controls)) return false;
                var sites = caller.GetExtraData<List<Site>>(EvidenceKey) ?? [];
                if (sites.Any(site => ReferenceEquals(site.Invocation, invocation))) return false;
                sites.Add(new(invocation, target, origin, arguments, comparisonIp, branchIp, nullCall, helper,
                    branch, guardOwner, normalArm, body, effects, controls, stores));
                var argumentIndex = invocation.OpCode == OpCode.Call ? 3 : 2;
                for (var index = 0; index < arguments.Length; index++)
                    invocation.SetOperand(argumentIndex + index, CanonicalArgument(caller, arguments[index]));
                caller.PutExtraData(EvidenceKey, sites);
                NativeRecoveryProofTracker.Mark(caller, EvidenceKey);
                admitted = true;
                return true;
            }
            finally
            {
                if (!admitted) for (var index = undo.Count - 1; index >= 0; index--) undo[index]();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    // A Boolean ABI parameter can be zero-extended into an integer temporary.
    // Eligibility uses its original typed value only after tracing the complete
    // pure definition chain. TryRecord still authenticates those native bits.
    internal static bool TryGetCandidate(MethodAnalysisContext caller, Instruction call,
        out MethodAnalysisContext target, out LocalVariable receiver)
    {
        if (NullCheckedCall.TryGet(call, out target, out receiver)) return true;
        target = null!;
        receiver = null!;
        if (!call.IsCall || call.Operands.Count == 0 || call.Operands[0] is not MethodAnalysisContext candidate ||
            candidate.IsStatic || candidate.Parameters.Count is < 1 or > 2) return false;
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        if (call.Operands.Count < argumentIndex + candidate.Parameters.Count) return false;
        var operands = call.Operands.ToList();
        for (var index = 0; index < candidate.Parameters.Count; index++)
        {
            var type = candidate.Parameters[index].ParameterType;
            if (!OriginalParameter(candidate, index) || !Scalar(type) ||
                !TryArgument(caller, call.Operands[argumentIndex + index], call, type, out _, out var argument))
                return false;
            operands[argumentIndex + index] = CanonicalArgument(caller, argument);
        }
        return NullCheckedCall.TryGet(new(-1, call.OpCode, operands)
            { IntegerBitWidth = call.IntegerBitWidth }, out target, out receiver);
    }

    private static IOperand CanonicalArgument(MethodAnalysisContext caller, Argument argument) =>
        argument.Literal is { } literal ? new Immediate(literal) : caller.ParameterLocals.Single(local =>
            !local.IsThis && LocalVariables.GetIncomingParameterIndex(caller, local) == argument.Entry);

    internal static bool IsValidFor(MethodAnalysisContext caller)
    {
        try
        {
            if (!NativeRecoveryProofTracker.Has(caller, EvidenceKey) ||
                caller.GetExtraData<List<Site>>(EvidenceKey) is not { Count: > 0 } sites ||
                caller.ControlFlowGraph?.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow) != false ||
                ReadBody(caller, out var body, out _) is not { } values)
                return false;
            foreach (var site in sites)
            {
                var call = site.Invocation;
                if (call.CallSemantics != CallSemantics.NullCheckedInstance ||
                    !body.SequenceEqual(site.Body) || !site.Helper.IsValidFor(caller.AppContext) ||
                    !NullCheckedCall.TryGet(call, out var target, out var receiver) ||
                    !ReferenceEquals(target, site.Target) ||
                    !RewrittenGuard(caller, site) ||
                    !TryOrigin(caller, receiver, call, out var origin) || origin != site.Receiver ||
                    !TryGuard(caller, body, values, origin, site.Comparison, site.Branch, call,
                        out var nullCall, out var helper, site.Helper, site.NullCall) ||
                    nullCall != site.NullCall || helper.NativeTarget != site.Helper.NativeTarget ||
                    !BindInvocation(caller, call, target, origin, body, values, out var arguments) ||
                    !arguments.SequenceEqual(site.Arguments) || !EffectsRetained(caller, call, site.Effects) ||
                    !ControlsRetained(caller, body, site.Controls) ||
                    site.Stores.Any(store => !ReferenceStoreRetained(caller, body, values, store)))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static X64NativeInvocationValues? ReadBody(MethodAnalysisContext caller,
        out NativeInstruction[] body, out HashSet<ulong> noReturn)
    {
        body = [];
        noReturn = [];
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(caller.AppContext) || !OrdinaryCallerGroup(caller) ||
            caller.ImplAttributes.HasFlag(MethodImplAttributes.InternalCall) ||
            caller.ControlFlowGraph is not { } graph || !CallerParameters(caller) ||
            graph.Instructions.Any(instruction => instruction.OpCode is OpCode.NotImplemented or
                OpCode.UnresolvedValue or OpCode.IndirectCall or OpCode.IndirectJump or OpCode.Invalid or OpCode.Interrupt))
            return null;
        if (caller.RawBytes.Length == 0) caller.EnsureRawBytes();
        if (X64NativeInstructionReader.ReadRootBody(caller) is not { Length: > 0 and <= 512 } read)
            return null;
        body = read;
        foreach (var intrinsic in graph.Instructions)
            if (intrinsic is { OpCode: OpCode.RuntimeNullThrow, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, NativeAddress: { } address,
                    Operands: [RuntimeNullThrowEvidence helper] } && helper.IsValidFor(caller.AppContext) &&
                body.Any(native => native.IP == address && native.Code == Code.Call_rel32_64 &&
                    native.NearBranchTarget == helper.NativeTarget))
                noReturn.Add(address);
        // Rewritten null arms may be unreachable in the current IR. Their exact
        // prior helpers still authenticate the corresponding native exits.
        if (caller.GetExtraData<List<Site>>(EvidenceKey) is { } saved)
            foreach (var site in saved)
                if (site.Helper.IsValidFor(caller.AppContext) && body.Any(native => native.IP == site.NullCall &&
                        native.Code == Code.Call_rel32_64 && native.NearBranchTarget == site.Helper.NativeTarget))
                    noReturn.Add(site.NullCall);
        for (var index = 0; index + 1 < body.Length; index++)
            if (body[index].Code == Code.Call_rel32_64 && body[index + 1].Code == Code.Int3 &&
                !noReturn.Contains(body[index].IP) &&
                X86RuntimeNullThrowProof.TryIdentify(caller.AppContext, body[index].NearBranchTarget) != null)
                noReturn.Add(body[index].IP);
        if (X86CallerExceptionRegionProof.Check(caller, body, noReturn) != null ||
            X64NativeInvocationValues.Create(body, noReturn) is not { } values ||
            !X64NativeInvocationFrameProof.IsValid(caller, body, values)) return null;
        return values;
    }

    private static bool TryGuard(MethodAnalysisContext caller, NativeInstruction[] body,
        X64NativeInvocationValues values, Origin origin, ulong comparisonIp, ulong branchIp, Instruction invocation,
        out ulong nullCall, out RuntimeNullThrowEvidence helper, RuntimeNullThrowEvidence? savedHelper = null,
        ulong? savedNullCall = null)
    {
        nullCall = 0;
        helper = null!;
        if (invocation.NativeAddress is not { } callIp ||
            body.SingleOrDefault(native => native.IP == comparisonIp) is not { Code: Code.Test_rm64_r64,
                Op0Kind: OpKind.Register, Op1Kind: OpKind.Register } compare ||
            compare.Op0Register != compare.Op1Register || compare.NextIP != branchIp ||
            !BindReceiver(caller, origin, body, values, comparisonIp, compare.Op0Register) ||
            body.SingleOrDefault(native => native.IP == branchIp) is not { Mnemonic: Mnemonic.Je or Mnemonic.Jne,
                Op0Kind: OpKind.NearBranch64 } branch || !values.Dominates(branchIp, callIp))
            return false;
        var nullIp = branch.Mnemonic == Mnemonic.Je ? branch.NearBranchTarget : branch.NextIP;
        var normalIp = branch.Mnemonic == Mnemonic.Je ? branch.NextIP : branch.NearBranchTarget;
        if (!values.Dominates(normalIp, callIp)) return false;
        var visited = new HashSet<ulong>();
        while (visited.Add(nullIp))
        {
            var native = body.SingleOrDefault(instruction => instruction.IP == nullIp);
            if (native.Code == Code.Call_rel32_64)
            {
                var intrinsic = caller.ControlFlowGraph!.Instructions.SingleOrDefault(instruction =>
                    instruction.NativeAddress == nullIp && instruction.OpCode == OpCode.RuntimeNullThrow);
                helper = savedNullCall == nullIp ? savedHelper! :
                    intrinsic?.Operands.Count == 1 ? intrinsic.Operands[0] as RuntimeNullThrowEvidence ?? null! : null!;
                if (helper == null || !helper.IsValidFor(caller.AppContext) ||
                    helper.NativeTarget != native.NearBranchTarget)
                    return false;
                nullCall = nullIp;
                return true;
            }
            if (native.Mnemonic == Mnemonic.Nop) { nullIp = native.NextIP; continue; }
            if (native.Code is Code.Jmp_rel8_64 or Code.Jmp_rel32_64 && native.Op0Kind == OpKind.NearBranch64)
            { nullIp = native.NearBranchTarget; continue; }
            return false;
        }
        return false;
    }

    private static bool BindInvocation(MethodAnalysisContext caller, Instruction call, MethodAnalysisContext target,
        Origin receiver, NativeInstruction[] body, X64NativeInvocationValues values, out Argument[] arguments)
    {
        arguments = [];
        if (call.NativeAddress is not { } address || caller.ControlFlowGraph?.Instructions.Contains(call) != true ||
            !EligibleTarget(caller, target, receiver.Type) || target.Parameters.Count > 2 ||
            body.SingleOrDefault(native => native.IP == address) is not { Op0Kind: OpKind.NearBranch64 } native ||
            native.Code is not (Code.Call_rel32_64 or Code.Jmp_rel32_64) ||
            native.NearBranchTarget != target.UnderlyingPointer ||
            !values.HasCallFrame(address, native.Code == Code.Jmp_rel32_64) ||
            !BindReceiver(caller, receiver, body, values, address, NativeRegister.RCX))
            return false;
        var methodInfo = target.Parameters.Count switch
        {
            0 => NativeRegister.RDX, 1 => NativeRegister.R8, _ => NativeRegister.R9,
        };
        if (!values.Matches(address, methodInfo, 64, new(NativeRegister.None, Literal: 0))) return false;
        if (native.Code == Code.Jmp_rel32_64 &&
            (native.NearBranchTarget >= body[0].IP && native.NearBranchTarget < body[^1].NextIP ||
             caller.ReturnType != target.ReturnType))
            return false;
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        if (call.Operands.Count < argumentIndex + target.Parameters.Count) return false;
        var bound = new Argument[target.Parameters.Count];
        for (var index = 0; index < target.Parameters.Count; index++)
        {
            var parameter = target.Parameters[index];
            if (!Scalar(parameter.ParameterType) || parameter.Definition?.RawType?.Type != parameter.ParameterType.Type ||
                !TryArgument(caller, call.Operands[argumentIndex + index], call,
                    parameter.ParameterType, out var source, out var managed) ||
                !values.Matches(address, index == 0 ? NativeRegister.RDX : NativeRegister.R8,
                    parameter.ParameterType.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ? 8 : 32, source))
                return false;
            bound[index] = managed;
        }
        arguments = bound;
        return true;
    }

    private static bool BindReceiver(MethodAnalysisContext caller, Origin origin, NativeInstruction[] body,
        X64NativeInvocationValues values, ulong use, NativeRegister register)
    {
        if (origin.Definition == null)
            return Incoming(caller, origin.Entry, origin.Type, out var incoming) &&
                   values.Matches(use, register, 64, new(incoming));
        if (origin.Definition.NativeAddress is not { } address || !values.Dominates(address, use))
            return false;
        if (origin.Producer is { } producer)
        {
            var call = origin.Definition;
            if (!OrdinaryMethod(producer) || producer.IsVirtual || producer.Parameters.Count != 0 ||
                !ReferenceEquals(producer.ReturnType, origin.Type) || !OrdinaryClass(origin.Type) ||
                body.SingleOrDefault(native => native.IP == address) is not { Code: Code.Call_rel32_64 } native ||
                native.NearBranchTarget != producer.UnderlyingPointer ||
                !values.HasCallFrame(address, false) ||
                !values.Matches(address, producer.IsStatic ? NativeRegister.RCX : NativeRegister.RDX,
                    64, new(NativeRegister.None, Literal: 0)))
                return false;
            var expected = producer.IsStatic ? 2 : 3;
            if (call.OpCode != OpCode.Call || call.IntegerBitWidth != 0 ||
                call.Operands.Count != expected && !(call.Operands.Count == expected + 1 &&
                    call.Operands[expected] is Immediate { Value: 0 }) ||
                !ReferenceEquals(call.Operands[0], producer) || call.Destination is not LocalVariable destination ||
                !ReferenceEquals(destination.Type, origin.Type) ||
                !Referenced(caller, producer.DeclaringType!) ||
                !X64GuardedEnumParameterCallProof.AccessibleTarget(caller.DeclaringType!, producer))
                return false;
            if (producer.IsStatic)
            {
                if (!RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(producer)) return false;
            }
            else if (call.Operands[2] is not LocalVariable producerReceiver ||
                     !TryOrigin(caller, producerReceiver, call, out var producerOrigin) || producerOrigin.Definition != null ||
                     !EligibleTarget(caller, producer, producerOrigin.Type) ||
                     !Incoming(caller, producerOrigin.Entry, producerOrigin.Type, out var incoming) ||
                     !values.Matches(address, NativeRegister.RCX, 64, new(incoming)))
                return false;
            return values.Matches(use, register, 64, new(NativeRegister.None, address, NativeRegister.RAX));
        }
        if (origin.Field is not { } field || origin.Definition.Operands is not
                [LocalVariable loaded, FieldReference access] || !ReferenceEquals(access.Field, field) ||
            !ReferenceEquals(loaded.Type, origin.Type) || access.Offset != origin.Offset || field.Offset != origin.Offset ||
            !TryOrigin(caller, access.Local, origin.Definition, out var sourceOrigin) || sourceOrigin.Definition != null ||
            sourceOrigin.Entry != origin.SourceEntry || !AccessibleField(caller, field) ||
            !ReferenceEquals(field.FieldType, origin.Type) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ||
            !Incoming(caller, sourceOrigin.Entry, sourceOrigin.Type, out var sourceRegister) ||
            body.SingleOrDefault(native => native.IP == address) is not { Code: Code.Mov_r64_rm64,
                Op0Kind: OpKind.Register, Op1Kind: OpKind.Memory } load ||
            load.MemoryIndex != NativeRegister.None || load.MemorySize.GetSize() != 8 ||
            load.MemoryDisplacement64 != (ulong)origin.Offset ||
            !values.Matches(address, load.MemoryBase, 64, new(sourceRegister)))
            return false;
        return values.Matches(use, register, 64, new(NativeRegister.None, address, load.Op0Register));
    }

    private static bool TryOrigin(MethodAnalysisContext method, LocalVariable value, Instruction use, out Origin origin)
    {
        origin = null!;
        var visited = new HashSet<(LocalVariable, Instruction)>();
        while (visited.Add((value, use)))
        {
            if (value.Type is not { } type || !OrdinaryClass(type) || value.IsMethodInfo ||
                Escaped(method, value) || !ReachingDefinition(method, value, use, out var definition))
                return false;
            if (definition == null)
            {
                if (!EntryIndex(method, value, out var index) || !Incoming(method, index, type, out _)) return false;
                origin = new(index, type);
                return true;
            }
            if (definition is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, LocalVariable source] } &&
                ReferenceEquals(destination, value) && ReferenceEquals(source.Type, type))
            { value = source; use = definition; continue; }
            if (definition is { OpCode: OpCode.Call, IntegerBitWidth: 0,
                    Operands: [MethodAnalysisContext producer, LocalVariable result, ..] } &&
                ReferenceEquals(result, value) && ReferenceEquals(producer.ReturnType, type))
            { origin = new(-2, type, definition, producer); return true; }
            if (definition is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable fieldValue, FieldReference field] } && ReferenceEquals(fieldValue, value) &&
                ReferenceEquals(field.Field.FieldType, type) && EntryIndex(method, field.Local, out var sourceEntry))
            { origin = new(-2, type, definition, Field: field.Field, SourceEntry: sourceEntry, Offset: field.Offset); return true; }
            return false;
        }
        return false;
    }

    private static bool TryArgument(MethodAnalysisContext method, IOperand operand, Instruction use,
        TypeAnalysisContext type, out NativeSource source, out Argument argument)
    {
        source = default;
        argument = default;
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (Escaped(method, local) ||
                !ReachingDefinition(method, local, use, out var definition)) return false;
            if (definition == null)
            {
                if (!ReferenceEquals(local.Type, type) || !EntryIndex(method, local, out var index) ||
                    !Incoming(method, index, type, out var register)) return false;
                source = new(register);
                argument = new(index, null);
                return true;
            }
            if (type.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN &&
                definition is { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable extended, LocalVariable original, Immediate { Value: 8 },
                        Immediate { Value: 32 }, Immediate { Value: 0 }] } && ReferenceEquals(extended, local) &&
                (ReferenceEquals(local.Type, method.AppContext.SystemTypes.SystemUInt32Type) || ReferenceEquals(local.Type, type)))
                operand = original;
            else if (type.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 && ReferenceEquals(local.Type, type) &&
                     definition is { OpCode: OpCode.Add or OpCode.Subtract, IntegerBitWidth: 32,
                         CallSemantics: CallSemantics.Direct, Operands: [LocalVariable arithmetic,
                             Immediate { Value: 0 }, Immediate { Value: >= int.MinValue and <= int.MaxValue } constant] } &&
                     ReferenceEquals(arithmetic, local))
                operand = new Immediate(definition.OpCode == OpCode.Add ? constant.Value : unchecked(-(int)constant.Value));
            else if (ReferenceEquals(local.Type, type) && definition is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                         CallSemantics: CallSemantics.Direct, Operands: [LocalVariable destination, var copied] } &&
                     ReferenceEquals(destination, local)) operand = copied;
            else return false;
            use = definition;
        }
        if (operand is not Immediate literal || type.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN && literal.Value is not (0 or 1) ||
            type.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 && literal.Value is < int.MinValue or > int.MaxValue)
            return false;
        source = new(NativeRegister.None, Literal: literal.UnsignedValue);
        argument = new(null, literal.Value);
        return true;
    }

    private static bool ReachingDefinition(MethodAnalysisContext method, LocalVariable value, Instruction use,
        out Instruction? definition)
    {
        definition = null;
        if (method.ControlFlowGraph is not { } graph || graph.FindBlockByInstruction(use) is not { } block) return false;
        var visiting = new HashSet<Block>();
        return Before(block, block.Instructions.IndexOf(use), out definition);

        bool Before(Block current, int limit, out Instruction? found)
        {
            found = null;
            for (var index = limit - 1; index >= 0; index--)
                if (ReferenceEquals(current.Instructions[index].Destination, value))
                { found = current.Instructions[index]; return true; }
            if (current == graph.EntryBlock) return method.ParameterLocals.Contains(value);
            if (!visiting.Add(current) || current.Predecessors.Count == 0) return false;
            var first = true;
            foreach (var previous in current.Predecessors)
            {
                if (!Before(previous, previous.Instructions.Count, out var input) || !first && !ReferenceEquals(input, found))
                { visiting.Remove(current); return false; }
                found = input;
                first = false;
            }
            visiting.Remove(current);
            return true;
        }
    }

    private static bool EntryIndex(MethodAnalysisContext method, LocalVariable local, out int index)
    {
        index = -2;
        if (!method.ParameterLocals.Contains(local) || local.IsMethodInfo) return false;
        if (local.IsThis) { index = -1; return !method.IsStatic; }
        if (LocalVariables.GetIncomingParameterIndex(method, local) is not { } parameter) return false;
        index = parameter;
        return true;
    }

    private static bool Incoming(MethodAnalysisContext method, int index, TypeAnalysisContext type, out NativeRegister register)
    {
        register = NativeRegister.None;
        if (index == -1)
        {
            if (method.IsStatic || !ReferenceEquals(type, method.DeclaringType)) return false;
            register = NativeRegister.RCX;
            return true;
        }
        if (index < 0 || index >= method.Parameters.Count || !OriginalParameter(method, index) ||
            !ReferenceEquals(method.Parameters[index].ParameterType, type)) return false;
        var slot = index + (method.IsStatic ? 0 : 1);
        register = slot switch { 0 => NativeRegister.RCX, 1 => NativeRegister.RDX, 2 => NativeRegister.R8,
            3 => NativeRegister.R9, _ => NativeRegister.None };
        return register != NativeRegister.None;
    }

    private static bool CallerParameters(MethodAnalysisContext caller) =>
        caller.Parameters.Count + (caller.IsStatic ? 0 : 1) < 4 &&
        caller.Parameters.Select((_, index) => OriginalParameter(caller, index)).All(valid => valid);

    private static bool OrdinaryCallerGroup(MethodAnalysisContext caller)
    {
        if (caller.DeclaringType?.DeclaringAssembly is not { } assembly ||
            !caller.AppContext.MethodsByAddress.TryGetValue(caller.UnderlyingPointer, out var bindings) || bindings.Count == 0)
            return false;
        var distinct = new HashSet<MethodAnalysisContext>();
        var definitions = new HashSet<object>();
        foreach (var method in bindings)
            if (!distinct.Add(method) || method.Definition == null || !definitions.Add(method.Definition) ||
                !ReferenceEquals(method.AppContext, caller.AppContext) ||
                method.UnderlyingPointer != caller.UnderlyingPointer ||
                !ReferenceEquals(method.DeclaringType?.DeclaringAssembly, assembly) ||
                method.ImplAttributes.HasFlag(MethodImplAttributes.InternalCall) ||
                !OrdinaryMethod(method) || !CallerParameters(method)) return false;
        return distinct.Contains(caller);
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method) =>
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        !method.IsAbstract && (method.Attributes & MethodAttributes.PinvokeImpl) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.Synchronized)) == 0 && NullCheckedCall.HasEligibleImplementation(method) &&
        method.GenericParameters.Count == 0 && method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        method.Definition is { } definition &&
        owner.Definition!.Methods is { } nativeMethods &&
        nativeMethods.Count(candidate => ReferenceEquals(candidate, definition)) == 1 &&
        owner.Methods.Count(candidate => ReferenceEquals(candidate, method)) == 1 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        ReturnType(method) && RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, false) &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer].Count(candidate => ReferenceEquals(candidate, method)) == 1 &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer].All(candidate =>
            candidate.UnderlyingPointer == method.UnderlyingPointer && ReferenceEquals(candidate.AppContext, method.AppContext)) &&
        method.Parameters.Select((_, index) => OriginalParameter(method, index)).All(valid => valid) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool ReturnType(MethodAnalysisContext method) => method.Definition?.RawReturnType is
        { NumMods: 0, Byref: 0, Pinned: 0 } raw && raw.Type == method.ReturnType.Type &&
        (method.IsVoid && ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ||
         Scalar(method.ReturnType) || OrdinaryClass(method.ReturnType));

    private static bool OriginalParameter(MethodAnalysisContext method, int index)
    {
        var parameter = method.Parameters[index];
        var type = parameter.ParameterType;
        return parameter.ParameterIndex == index && ReferenceEquals(parameter.DeclaringMethod, method) &&
               !parameter.IsRef && parameter.OverrideParameterType == null && parameter.Attributes == parameter.DefaultAttributes &&
               ReferenceEquals(type, parameter.DefaultParameterType) && (Scalar(type) || OrdinaryClass(type)) &&
               parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } raw && raw.Type == type.Type;
    }

    private static bool OrdinaryClass(TypeAnalysisContext type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.DeclaringType)
            if (!visited.Add(current) || !NullCheckedCall.IsReferenceClass(current) ||
                current.Name != current.DefaultName || current.Namespace != current.DefaultNamespace ||
                current.Definition is not { RawType: { NumMods: 0, Byref: 0, Pinned: 0,
                    Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING } } definition ||
                definition.RawBaseType is { } rawBase && (rawBase.NumMods != 0 || rawBase.Byref != 0 || rawBase.Pinned != 0 ||
                    rawBase.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)) ||
                !ReferenceEquals(definition.DeclaringType, current.DeclaringType?.Definition) ||
                current.DeclaringType != null && current.DeclaringType.NestedTypes.Count(candidate => ReferenceEquals(candidate, current)) != 1)
                return false;
        return true;
    }

    private static bool Scalar(TypeAnalysisContext type) =>
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemBooleanType) ||
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemInt32Type);

    private static bool EligibleTarget(MethodAnalysisContext caller, MethodAnalysisContext target, TypeAnalysisContext receiver)
    {
        if (!OrdinaryMethod(target) || target.IsStatic || target.IsVirtual ||
            !ReferenceEquals(caller.AppContext, target.AppContext) ||
            !CallResultNullGuardProof.HasUnambiguousTarget(target, receiver) ||
            !Referenced(caller, target.DeclaringType!) ||
            !X64GuardedEnumParameterCallProof.AccessibleTarget(caller.DeclaringType!, target)) return false;
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null; current = current.BaseType)
            if (!visited.Add(current) || !OrdinaryClass(current)) return false;
        return true;
    }

    private static bool Referenced(MethodAnalysisContext caller, TypeAnalysisContext owner) =>
        X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringType!.DeclaringAssembly, owner.DeclaringAssembly);

    private static bool AccessibleField(MethodAnalysisContext caller, FieldAnalysisContext field) =>
        field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes && !field.IsStatic &&
        field.DeclaringType.Fields.Count(candidate => ReferenceEquals(candidate, field)) == 1 &&
        OrdinaryClass(field.DeclaringType) && Referenced(caller, field.DeclaringType) &&
        (ReferenceEquals(field.DeclaringType, caller.DeclaringType) ||
         field.Visibility == FieldAttributes.Public && (field.DeclaringType.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public) &&
        field.BackingData?.Field.RawFieldType is { NumMods: 0, Byref: 0, Pinned: 0 } raw && raw.Type == field.FieldType.Type;

    private static bool Escaped(MethodAnalysisContext method, LocalVariable value) =>
        OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph!.Instructions)
            .Any(local => local.Register.Number == value.Register.Number);

    private static bool TryEffects(MethodAnalysisContext method, Instruction invocation, out OrderedEffect[] effects)
    {
        effects = [];
        if (method.ControlFlowGraph is not { } graph || graph.FindBlockByInstruction(invocation) is not { } callBlock)
            return false;
        var dominators = new DominatorInfo(graph);
        var retained = new List<OrderedEffect>();
        foreach (var operation in graph.Instructions.Where(Observable))
        {
            if (operation.NativeAddress is not { } address || graph.FindBlockByInstruction(operation) is not { } block)
                return false;
            var before = block == callBlock ? block.Instructions.IndexOf(operation) < block.Instructions.IndexOf(invocation) :
                dominators.Dominates(block, callBlock);
            var after = block == callBlock ? block.Instructions.IndexOf(operation) > block.Instructions.IndexOf(invocation) :
                dominators.Dominates(callBlock, block);
            if (OperationKey(method, operation, new HashSet<Instruction>(), out var key) == false) return false;
            retained.Add(new(operation, address, before, after, operation.CallSemantics, key));
        }
        effects = retained.ToArray();
        return true;
    }

    private static bool EffectsRetained(MethodAnalysisContext method, Instruction invocation, OrderedEffect[] saved) =>
        TryEffects(method, invocation, out var current) && current.Length == saved.Length && saved.Zip(current,
            (effect, other) => ReferenceEquals(other.Operation, effect.Operation) && other.Address == effect.Address &&
                other.Before == effect.Before && other.After == effect.After &&
                (other.Semantics == effect.Semantics || effect.Semantics == CallSemantics.Direct &&
                    other.Semantics == CallSemantics.NullCheckedInstance &&
                    method.GetExtraData<List<Site>>(EvidenceKey)?.Any(site => ReferenceEquals(site.Invocation, other.Operation)) == true) &&
                SameKey(effect.Value, other.Value)).All(equal => equal);

    private static bool SameKey(ValueKey left, ValueKey right) => left.Kind == right.Kind && Equals(left.Value, right.Value) &&
        left.Children.Length == right.Children.Length && left.Children.Zip(right.Children, SameKey).All(equal => equal);

    private static bool ManagedGuard(MethodAnalysisContext method, Instruction comparison, Instruction branch,
        LocalVariable receiver, out Block owner, out Block normal)
    {
        owner = normal = null!;
        if (method.ControlFlowGraph is not { } graph || graph.FindBlockByInstruction(branch) is not { } block ||
            branch.Operands is not [Block taken, LocalVariable condition] ||
            comparison.Destination is not LocalVariable comparedCondition || !ReferenceEquals(condition, comparedCondition) ||
            !ReferenceEquals(comparison.Operands[1], receiver) || comparison.Operands[2] is not Immediate { Value: 0 } ||
            comparison.CallSemantics != CallSemantics.Direct || branch.CallSemantics != CallSemantics.Direct ||
            block.Successors.Count != 2 || !block.Successors.Contains(taken) ||
            ReferenceEquals(block.Successors[0], block.Successors[1]) ||
            !ReferenceEquals(block.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop), branch))
            return false;
        var other = block.Successors.Single(successor => !ReferenceEquals(successor, taken));
        owner = block;
        normal = comparison.OpCode == OpCode.CheckEqual ? other : taken;
        return graph.Blocks.Contains(normal);
    }

    private static bool RewrittenGuard(MethodAnalysisContext method, Site site) =>
        method.ControlFlowGraph is { } graph && graph.Instructions.Contains(site.GuardBranch) &&
        graph.Blocks.Contains(site.NormalArm) && ReferenceEquals(graph.FindBlockByInstruction(site.GuardBranch), site.GuardOwner) &&
        site.GuardBranch is { OpCode: OpCode.Jump, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [var target] } && site.GuardBranch.NativeAddress == site.Branch &&
        ReferenceEquals(TargetBlock(graph, target), site.NormalArm) &&
        ReferenceEquals(site.GuardOwner.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop), site.GuardBranch) &&
        site.GuardOwner.Successors.Count == 1 && ReferenceEquals(site.GuardOwner.Successors[0], site.NormalArm) &&
        site.NormalArm.Predecessors.Count(block => ReferenceEquals(block, site.GuardOwner)) == 1;

    private static Block? TargetBlock(ISILControlFlowGraph graph, IOperand operand) => operand switch
    {
        Block block when graph.Blocks.Contains(block) => block,
        Instruction instruction when graph.FindBlockByInstruction(instruction) is { } block &&
            ReferenceEquals(block.Instructions.FirstOrDefault(), instruction) => block,
        _ => null,
    };

    // Guard deletion is the sole control transformation owned by this proof.
    // Other retained conditions and destinations must keep the same semantic
    // reaching definitions, even when pure register copies are coalesced.
    private static bool TryControls(MethodAnalysisContext method, Instruction? pendingGuard,
        NativeInstruction[] body, out Control[] controls)
    {
        controls = [];
        if (method.ControlFlowGraph is not { } graph) return false;
        var provedGuards = new HashSet<Instruction>(method.GetExtraData<List<Site>>(EvidenceKey)?.Select(site => site.GuardBranch)
            ?? Enumerable.Empty<Instruction>());
        if (pendingGuard != null) provedGuards.Add(pendingGuard);
        var results = new List<Control>();
        foreach (var operation in graph.Instructions.Where(instruction =>
                     instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump && !provedGuards.Contains(instruction)))
        {
            if (operation.NativeAddress is not { } address || operation.IntegerBitWidth != 0 ||
                operation.CallSemantics != CallSemantics.Direct || graph.FindBlockByInstruction(operation) is not { } owner ||
                !ReferenceEquals(owner.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop), operation) ||
                operation.Operands.Count == 0 || TargetBlock(graph, operation.Operands[0]) is not { } taken)
                return false;
            var native = body.SingleOrDefault(instruction => instruction.IP == address);
            if (native.Op0Kind != OpKind.NearBranch64 || operation.OpCode == OpCode.Jump &&
                native.Code is not (Code.Jmp_rel8_64 or Code.Jmp_rel32_64) || operation.OpCode == OpCode.ConditionalJump &&
                native.FlowControl != FlowControl.ConditionalBranch) return false;
            Block? other = null;
            ValueKey? condition = null;
            if (operation.OpCode == OpCode.Jump)
            {
                if (operation.Operands.Count != 1 || owner.Successors.Count != 1 || !ReferenceEquals(owner.Successors[0], taken))
                    return false;
            }
            else
            {
                if (operation.Operands.Count != 2 || owner.Successors.Count != 2 || !owner.Successors.Contains(taken) ||
                    ReferenceEquals(owner.Successors[0], owner.Successors[1]) ||
                    !OperandKey(method, operation.Operands[1], operation, new HashSet<Instruction>(), out condition))
                    return false;
                other = owner.Successors.Single(successor => !ReferenceEquals(successor, taken));
            }
            results.Add(new(operation, address, operation.OpCode, owner, taken, other, condition));
        }
        controls = results.ToArray();
        return true;
    }

    private static bool ControlsRetained(MethodAnalysisContext method, NativeInstruction[] body, Control[] captured)
    {
        if (!TryControls(method, null, body, out var current)) return false;
        var guards = new HashSet<Instruction>(method.GetExtraData<List<Site>>(EvidenceKey)!.Select(site => site.GuardBranch));
        var saved = captured.Where(control => !guards.Contains(control.Operation)).ToArray();
        return saved.Length == current.Length && saved.Zip(current, (left, right) =>
            ReferenceEquals(left.Operation, right.Operation) && left.Address == right.Address && left.Code == right.Code &&
            ReferenceEquals(left.Owner, right.Owner) && ReferenceEquals(left.Taken, right.Taken) &&
            ReferenceEquals(left.Other, right.Other) && (left.Condition == null ? right.Condition == null :
                right.Condition != null && SameKey(left.Condition, right.Condition))).All(equal => equal);
    }

    private static bool OperationKey(MethodAnalysisContext method, Instruction operation, HashSet<Instruction> seen,
        out ValueKey key)
    {
        key = null!;
        if (!seen.Add(operation) || seen.Count > 128) return false;
        var children = new List<ValueKey> { new("opcode", operation.OpCode, []), new("width", operation.IntegerBitWidth, []) };
        if (operation.IsCall && operation.Operands[0] is MethodAnalysisContext target)
        {
            children.Add(MethodKey(target));
            var start = operation.OpCode == OpCode.Call ? 2 : 1;
            var end = start + (target.IsStatic ? 0 : 1) + target.Parameters.Count;
            if (operation.Operands.Count < end) return false;
            for (var index = start; index < end; index++)
            {
                var parameterIndex = index - start - (target.IsStatic ? 0 : 1);
                if (parameterIndex >= 0 && Scalar(target.Parameters[parameterIndex].ParameterType) &&
                    TryArgument(method, operation.Operands[index], operation, target.Parameters[parameterIndex].ParameterType,
                        out _, out var scalar)) children.Add(new("argument", scalar, []));
                else if (OperandKey(method, operation.Operands[index], operation, seen, out var value)) children.Add(value);
                else return false;
            }
        }
        else
            for (var index = 0; index < operation.Operands.Count; index++)
            {
                if (index == 0 && operation.Destination is LocalVariable) continue;
                if (!OperandKey(method, operation.Operands[index], operation, seen, out var value)) return false;
                children.Add(value);
            }
        seen.Remove(operation);
        key = new("operation", operation, children.ToArray());
        return true;
    }

    private static ValueKey MethodKey(MethodAnalysisContext method) => new("method", method,
        [new("name", method.Name, []), new("attributes", method.Attributes, []), new("implementation", method.ImplAttributes, []),
            new("owner", method.DeclaringType, []), new("return", method.ReturnType, []),
            new("native-pointer", method.UnderlyingPointer, []),
            RawTypeKey(method.Definition?.RawReturnType),
            new("parameters", null, method.Parameters.Select(parameter => new ValueKey("parameter", parameter,
                [new("type", parameter.ParameterType, []), new("attributes", parameter.Attributes, []),
                    RawTypeKey(parameter.Definition?.RawType)])).ToArray())]);

    private static ValueKey RawTypeKey(Il2CppType? type) => new("raw-type", type?.Type,
        [new("modifiers", type?.NumMods, []), new("byref", type?.Byref, []), new("pinned", type?.Pinned, [])]);

    private static bool OperandKey(MethodAnalysisContext method, IOperand operand, Instruction use,
        HashSet<Instruction> seen, out ValueKey key)
    {
        key = null!;
        switch (operand)
        {
            case Immediate immediate: key = new("literal", immediate.Value, []); return true;
            case LocalVariable local:
                if (Escaped(method, local) || !ReachingDefinition(method, local, use, out var definition)) return false;
                if (definition == null)
                {
                    if (!EntryIndex(method, local, out var entry)) return false;
                    key = new("entry", entry, [new("type", local.Type, [])]);
                    return true;
                }
                if (definition is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                        Operands: [LocalVariable, LocalVariable or Immediate] })
                {
                    if (!seen.Add(definition) || seen.Count > 128) return false;
                    var copied = OperandKey(method, definition.Operands[1], definition, seen, out key);
                    seen.Remove(definition);
                    return copied;
                }
                if (!OperationKey(method, definition, seen, out var produced)) return false;
                key = new("produced", local.Type, [produced]);
                return true;
            case FieldReference field:
                if (!OperandKey(method, field.Local, use, seen, out var receiver)) return false;
                key = new("field", field.Field,
                    [new("name", field.Field.Name, []), new("attributes", field.Field.Attributes, []),
                        new("owner", field.Field.DeclaringType, []), new("type", field.Field.FieldType, []),
                        new("offset", field.Offset, []), RawTypeKey(field.Field.BackingData?.Field.RawFieldType), receiver]);
                return true;
            case MemoryOperand memory:
                var components = new List<ValueKey> { new("addend", memory.Addend, []), new("scale", memory.Scale, []) };
                foreach (var component in new[] { memory.Base, memory.Index })
                    if (component == null) components.Add(new("absent", null, []));
                    else if (OperandKey(method, component, use, seen, out var part)) components.Add(part);
                    else return false;
                key = new("memory", null, components.ToArray());
                return true;
            case MethodAnalysisContext target: key = MethodKey(target); return true;
            default: return false;
        }
    }

    private static bool Observable(Instruction instruction)
    {
        if (instruction.OpCode is OpCode.Nop or OpCode.Jump or OpCode.ConditionalJump or OpCode.RuntimeNullThrow)
            return false; // Control and the exact removed null arm have separate evidence.
        var pure = instruction.OpCode switch
        {
            OpCode.Move or OpCode.Phi or OpCode.Add or OpCode.Subtract or OpCode.Multiply or
                OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.ShiftRightUnsigned or OpCode.And or OpCode.Or or
                OpCode.Xor or OpCode.Not or OpCode.Negate => true,
            var comparison when comparison.IsComparison() => true,
            OpCode.IntegerExtend => IntegerExtension.IsPureAndValid(instruction),
            OpCode.FloatSelect => FloatSelection.TryGet(instruction, out _),
            OpCode.FloatProject => FloatProjection.TryGet(instruction, out _),
            OpCode.FloatCompare => instruction is { IntegerBitWidth: 0,
                Operands: [LocalVariable, LocalVariable, LocalVariable, Immediate { Value: 32 or 64 },
                    Immediate { Value: >= 0 and <= 15 }] },
            _ => false,
        };
        return !pure || instruction.Operands.Any(operand => !OperandEffects.IsPureValue(operand));
    }
}
