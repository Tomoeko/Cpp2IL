using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    /// <summary>
    /// A proved pair of parameter-array reads replaces explicit native null and
    /// bounds exits. Confirm that final managed IL still reads both original
    /// parameters at the proved sites, keeps the intervening call, and returns
    /// the comparison of those two values.
    /// </summary>
    internal static void ValidateParameterGuardedArrayAccesses(MethodAnalysisContext method)
    {
        if (method.ParameterGuardedArrayAccessEvidence is not { } evidence)
            return;

        var native = X86Utils.Iterate(method).ToArray();
        if (X64ArrayGuardSiteProof.FindParameter(method, native) is not { } current ||
            !SameParameterEvidence(evidence, current))
            throw ParameterArrayFailure("the complete native proof or metadata changed");

        var graph = method.ControlFlowGraph;
        var linear = graph == null ? null : LinearInstructions(graph);
        if (linear == null || graph!.EntryBlock.Instructions.Count != 0 ||
            graph.ExitBlock.Instructions.Count != 0 ||
            evidence.Sites is not [var first, var second] ||
            first.Kind != X64ArrayGuardSiteProof.ReadKind.Move ||
            second.Kind != X64ArrayGuardSiteProof.ReadKind.Compare ||
            first.ArrayParameter.ParameterIndex == second.ArrayParameter.ParameterIndex ||
            first.EffectsSincePreviousAccess.Count != 0 ||
            second.EffectsSincePreviousAccess is not [var effect] ||
            effect.Kind != X64ArrayGuardSiteProof.EffectKind.DirectCall)
            throw ParameterArrayFailure("the final graph is not the proved two-read path");

        // This native body has exactly two element reads, one call, one equality
        // result, and one return. Any extra surviving operation needs its own
        // native and exceptional-exit proof, even if it has only local operands.
        if (linear.Select(instruction => instruction.OpCode).ToArray() is not
            [OpCode.Move, OpCode.CallVoid, OpCode.Move,
                OpCode.CheckEqual, OpCode.Return])
            throw ParameterArrayFailure("an unproved managed operation survived the native path");

        var elementReads = new List<Instruction>(2);
        var previousPosition = -1;
        foreach (var site in evidence.Sites)
        {
            var read = UniqueAt(linear, site.ElementReadIp, instruction => instruction is
            {
                OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable, ArrayAccess]
            });
            if (read?.Operands is not [LocalVariable element, ArrayAccess access] ||
                !ReferenceEquals(element.Type,
                    method.AppContext.SystemTypes.SystemObjectType) ||
                site.ArrayParameter.ParameterType is not SzArrayTypeAnalysisContext arrayType ||
                !ReferenceEquals(arrayType.ElementType,
                    method.AppContext.SystemTypes.SystemObjectType) ||
                !NullCheckedCall.SameOrdinaryType(access.Array.Type, arrayType) ||
                !ParameterLocal(method, site.ArrayParameter,
                    site.ArrayEntryRegister, out var parameter) ||
                !ReachesCapturedParameter(linear, access.Array,
                    linear.IndexOf(read), parameter, site.ArrayCaptureIps) ||
                access.Index is not LocalVariable index ||
                !ReferenceEquals(index.Type,
                    method.AppContext.SystemTypes.SystemInt32Type) ||
                !IndexComesFromParameter(method, linear, evidence.IndexEntryRegister,
                    evidence.IndexExtensionIp, index, linear.IndexOf(read)) ||
                linear.IndexOf(read) <= previousPosition)
                throw ParameterArrayFailure("an array read lost its proved parameter, index, type, or order");

            previousPosition = linear.IndexOf(read);
            elementReads.Add(read);
        }

        var call = UniqueAt(linear, effect.Ip, instruction => instruction is
        {
            OpCode: OpCode.CallVoid, IntegerBitWidth: 0,
            CallSemantics: CallSemantics.Direct,
            Operands: [MethodAnalysisContext, LocalVariable]
        });
        var nativeCall = native.SingleOrDefault(instruction => instruction.IP == effect.Ip);
        if (call?.Operands is not [MethodAnalysisContext target, LocalVariable receiver] ||
            nativeCall.IsInvalid || nativeCall.Code != Code.Call_rel32_64 ||
            nativeCall.NearBranchTarget != effect.DirectCallTarget ||
            effect.NativeCode != Code.Call_rel32_64 ||
            !ReferenceEquals(target.AppContext, method.AppContext) ||
            !ReferenceEquals(target.DeclaringType, method.DeclaringType) ||
            target.IsStatic || !target.IsVoid || target.Parameters.Count != 0 ||
            target.Name != target.DefaultName ||
            target.Attributes != target.DefaultAttributes ||
            target.ImplAttributes != target.DefaultImplAttributes ||
            target.OverrideReturnType != null ||
            target.UnderlyingPointer != effect.DirectCallTarget ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
            !method.AppContext.MethodsByAddress.TryGetValue(effect.DirectCallTarget,
                out var bindings) ||
            bindings is not [{ } uniqueTarget] ||
            !ReferenceEquals(uniqueTarget, target) ||
            method.IsStatic || method.ParameterOperands.FirstOrDefault() is not
                Register { Name: "rcx" } thisRegister ||
            method.ParameterLocals.Where(local => local.IsThis) is not { } thisCandidates ||
            thisCandidates.ToArray() is not [{ } thisLocal] ||
            thisLocal.Register.Number != thisRegister.Number ||
            thisLocal.Register.Version != -1 ||
            !ReferenceEquals(receiver, thisLocal) ||
            !ReferenceEquals(receiver.Type, method.DeclaringType) ||
            NativeWritesRegisterBetween(native, 0, effect.Ip,
                Iced.Intel.Register.RCX) ||
            linear.IndexOf(call) <= linear.IndexOf(elementReads[0]) ||
            linear.IndexOf(call) >= linear.IndexOf(elementReads[1]))
            throw ParameterArrayFailure("the intervening call lost its native target, receiver, or order");

        ValidateReferenceComparison(method, evidence.Comparison,
            evidence.Sites.Select(site => site.Kind).ToArray(),
            evidence.Sites.Select(site => site.ElementReadIp).ToArray(),
            linear, elementReads);
    }

    private static bool ParameterLocal(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, Iced.Intel.Register entryRegister,
        out LocalVariable local)
    {
        local = null!;
        var slot = parameter.ParameterIndex + 1;
        if (parameter.ParameterIndex is < 0 or > 2 ||
            parameter.ParameterIndex >= method.Parameters.Count ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            !ReferenceEquals(method.Parameters[parameter.ParameterIndex], parameter) ||
            parameter.Definition == null || parameter.IsRef ||
            parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            slot >= method.ParameterOperands.Count ||
            method.ParameterOperands[slot] is not Register argument ||
            argument.Name != X86Utils.GetRegisterName(entryRegister))
            return false;
        var matches = method.ParameterLocals.Where(candidate =>
            !candidate.IsThis && !candidate.IsMethodInfo &&
            candidate.Register.Number == argument.Number &&
            candidate.Register.Version == -1).ToArray();
        if (matches is not [{ } found] ||
            !NullCheckedCall.SameOrdinaryType(found.Type,
                parameter.ParameterType))
            return false;
        local = found;
        return true;
    }

    private static bool ReachesCapturedParameter(List<Instruction> instructions,
        LocalVariable value, int before, LocalVariable parameter,
        IReadOnlyList<ulong> captureIps)
    {
        var captures = new HashSet<ulong>(captureIps);
        var seen = new HashSet<LocalVariable>();
        while (seen.Add(value))
        {
            if (ReferenceEquals(value, parameter))
                // Copy coalescing can replace the captured native register with
                // its source parameter. The native proof still binds the capture
                // and excludes intervening writes to that register.
                return true;
            var definition = instructions.Take(before).LastOrDefault(instruction =>
                ReferenceEquals(instruction.Destination, value));
            if (definition is not
                {
                    OpCode: OpCode.Move, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    NativeAddress: { } ip,
                    Operands: [LocalVariable, LocalVariable source]
                } || !captures.Remove(ip))
                return false;
            before = instructions.IndexOf(definition);
            value = source;
        }
        return false;
    }

    private static bool SameParameterEvidence(
        X64ArrayGuardSiteProof.ParameterEvidence left,
        X64ArrayGuardSiteProof.ParameterEvidence right)
    {
        if (!ReferenceEquals(left.IndexParameter, right.IndexParameter) ||
            left.IndexEntryRegister != right.IndexEntryRegister ||
            left.IndexExtensionIp != right.IndexExtensionIp ||
            left.IndexUseRegister != right.IndexUseRegister ||
            left.OffsetLeaIp != right.OffsetLeaIp ||
            left.OffsetRegister != right.OffsetRegister ||
            left.NullHelperCallIp != right.NullHelperCallIp ||
            left.NullTrapIp != right.NullTrapIp ||
            left.BoundsHelperCallIp != right.BoundsHelperCallIp ||
            left.BoundsTrapIp != right.BoundsTrapIp ||
            left.NativeEndExclusiveIp != right.NativeEndExclusiveIp ||
            left.Comparison != right.Comparison ||
            left.Sites.Count != right.Sites.Count)
            return false;
        return left.Sites.Zip(right.Sites,
            (first, second) => (First: first, Second: second)).All(pair =>
            ReferenceEquals(pair.First.ArrayParameter,
                pair.Second.ArrayParameter) &&
            pair.First.ArrayEntryRegister == pair.Second.ArrayEntryRegister &&
            pair.First.ArrayUseRegister == pair.Second.ArrayUseRegister &&
            pair.First.ArrayCaptureIps.SequenceEqual(pair.Second.ArrayCaptureIps) &&
            pair.First.ArrayNullTestIp == pair.Second.ArrayNullTestIp &&
            pair.First.ArrayNullBranchIp == pair.Second.ArrayNullBranchIp &&
            pair.First.BoundsCompareIp == pair.Second.BoundsCompareIp &&
            pair.First.BoundsBranchIp == pair.Second.BoundsBranchIp &&
            pair.First.ElementReadIp == pair.Second.ElementReadIp &&
            pair.First.Kind == pair.Second.Kind &&
            pair.First.EffectsSincePreviousAccess.SequenceEqual(
                pair.Second.EffectsSincePreviousAccess));
    }

    private static DecompilerException ParameterArrayFailure(string detail) =>
        new("Parameter-array guard proof no longer matches final managed operations: " + detail);
}
