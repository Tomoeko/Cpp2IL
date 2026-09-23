using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Recognizes a direct managed Boolean return immediately tested through AL.
/// Only ZF may be consumed while the TEST flags remain live: the Windows x64
/// return convention does not define the unused upper bits of RAX, and a
/// noncanonical nonzero Boolean byte would not establish PF or SF.
/// </summary>
internal static class X86BooleanReturnSelfTestProof
{
    private const RflagsBits StatusFlags = RflagsBits.CF | RflagsBits.PF | RflagsBits.AF |
                                           RflagsBits.ZF | RflagsBits.SF | RflagsBits.OF;

    internal static HashSet<ulong> Find(MethodAnalysisContext method, IReadOnlyList<Instruction> native)
    {
        var result = new HashSet<ulong>();
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            native.Count is < 3 or > 4096 || native[0].IP != method.UnderlyingPointer ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any())
            return result;

        // Avoid indexing the unwind directory and metadata for the many methods
        // that contain no direct call/low-byte test with a bounded ZF consumer.
        var possible = Enumerable.Range(1, native.Count - 2)
            .Select(index => (TestIndex: index, ConsumerIndex: FindConsumerIndex(native, index)))
            .Where(candidate => candidate.ConsumerIndex >= 0).ToArray();
        if (possible.Length == 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.AppContext.Binary is not PE ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
            return result;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start)
            return result;

        foreach (var (index, consumerIndex) in possible)
        {
            var call = native[index - 1];
            var test = native[index];
            var consumer = native[consumerIndex];
            if (consumer.NextIP > region.End ||
                unwind.ClassifySpan(call.IP, consumer.NextIP) is not
                    { Kind: X64UnwindProof.SpanKind.HandlerFree } span ||
                span.Start != region.Start || span.End != region.End ||
                HasIncomingBranch(native, test.IP, consumer.NextIP) ||
                !OnlyZeroFlagConsumed(native, index) ||
                !method.AppContext.MethodsByAddress.TryGetValue(call.NearBranchTarget, out var bindings) ||
                bindings is not [var target] || !IsBoundBooleanTarget(method.AppContext, target))
                continue;
            result.Add(test.IP);
        }
        return result;
    }

    private static bool IsBoundBooleanTarget(ApplicationAnalysisContext app, MethodAnalysisContext target) =>
        ReferenceEquals(target.ReturnType, app.SystemTypes.SystemBooleanType) &&
        target.OverrideReturnType == null && target.Name == target.DefaultName &&
        target is not ConcreteGenericMethodAnalysisContext &&
        target.DeclaringType is { IsGenericInstance: false, GenericParameters.Count: 0 } &&
        target.GenericParameters.Count == 0 && target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes &&
        (target.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0 &&
        target.Definition?.RawReturnType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
            NumMods: 0, Byref: 0, Pinned: 0 } &&
        target.Parameters.Select((parameter, index) => (parameter, index)).All(item =>
            item.parameter.ParameterIndex == item.index &&
            ReferenceEquals(item.parameter.DeclaringMethod, target) &&
            item.parameter.OverrideParameterType == null &&
            item.parameter.OverrideAttributes == null &&
            !item.parameter.UseOverrideDefaultValue &&
            item.parameter.Attributes == item.parameter.DefaultAttributes &&
            item.parameter.Name == item.parameter.DefaultName && !item.parameter.IsRef) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target);

    internal static bool IsAdjacentShape(IReadOnlyList<Instruction> native, int testIndex)
        => FindConsumerIndex(native, testIndex) == testIndex + 1;

    internal static int FindConsumerIndex(IReadOnlyList<Instruction> native, int testIndex)
    {
        if (testIndex < 1 || testIndex + 1 >= native.Count)
            return -1;
        var call = native[testIndex - 1];
        var test = native[testIndex];
        if (call.Code != Code.Call_rel32_64 || call.Op0Kind != OpKind.NearBranch64 ||
            test.Mnemonic != Mnemonic.Test || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register || test.Op0Register != Register.AL ||
            test.Op1Register != Register.AL || call.NextIP != test.IP ||
            !HasOrdinaryEncoding(call) || !HasOrdinaryEncoding(test))
            return -1;

        // MSVC may materialize both alternatives between TEST and a register-source
        // CMOV. These two MOVs have no flag, memory, call, or exception effects.
        for (var index = testIndex + 1; index < native.Count && index <= testIndex + 3; index++)
        {
            var instruction = native[index];
            if (native[index - 1].NextIP != instruction.IP || !HasOrdinaryEncoding(instruction))
                return -1;
            if (index == testIndex + 1 && instruction.FlowControl == FlowControl.ConditionalBranch &&
                instruction.ConditionCode is ConditionCode.e or ConditionCode.ne &&
                instruction.Op0Kind == OpKind.NearBranch64)
                return index;
            if (instruction.Mnemonic is Mnemonic.Cmove or Mnemonic.Cmovne &&
                instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
                instruction.ConditionCode is ConditionCode.e or ConditionCode.ne &&
                instruction.FlowControl == FlowControl.Next)
                return index;
            if (index == testIndex + 3 || instruction.Mnemonic != Mnemonic.Mov ||
                instruction.Op0Kind != OpKind.Register || !instruction.Op1Kind.IsImmediate() ||
                instruction.RflagsRead != RflagsBits.None ||
                instruction.RflagsModified != RflagsBits.None ||
                instruction.FlowControl != FlowControl.Next)
                return -1;
        }
        return -1;
    }

    private static bool HasOrdinaryEncoding(Instruction instruction) =>
        !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == Register.None;

    internal static bool HasIncomingBranch(IReadOnlyList<Instruction> native, ulong start, ulong end)
    {
        foreach (var instruction in native)
        {
            if (instruction.FlowControl is FlowControl.IndirectBranch or FlowControl.IndirectCall)
                return true;
            if (instruction.FlowControl is not (FlowControl.ConditionalBranch or
                FlowControl.UnconditionalBranch or FlowControl.Call))
                continue;
            if (instruction.Op0Kind == OpKind.NearBranch64 &&
                instruction.NearBranchTarget >= start && instruction.NearBranchTarget < end)
                return true;
        }
        return false;
    }

    internal static bool OnlyZeroFlagConsumed(IReadOnlyList<Instruction> native, int testIndex)
    {
        var byAddress = new Dictionary<ulong, int>();
        for (var index = 0; index < native.Count; index++)
        {
            if (byAddress.ContainsKey(native[index].IP))
                return false;
            byAddress.Add(native[index].IP, index);
        }
        var pending = new Stack<(int Index, RflagsBits Live)>();
        var visited = new HashSet<(int Index, RflagsBits Live)>();
        pending.Push((testIndex + 1, StatusFlags));
        while (pending.Count > 0)
        {
            var (index, live) = pending.Pop();
            if (!visited.Add((index, live)))
                continue;
            if (index < 0 || index >= native.Count)
                return false;
            if (live == RflagsBits.None)
                continue;
            var instruction = native[index];
            var read = instruction.RflagsRead & live;
            if (read != RflagsBits.None &&
                !(read == RflagsBits.ZF && instruction.ConditionCode is ConditionCode.e or ConditionCode.ne &&
                  (instruction.FlowControl == FlowControl.ConditionalBranch ||
                   instruction.Mnemonic is Mnemonic.Cmove or Mnemonic.Cmovne &&
                   instruction.Op1Kind == OpKind.Register)))
                return false;
            if (instruction.Mnemonic == Mnemonic.Call)
                return false; // Do not infer what another callee does with live TEST flags.
            if (instruction.Mnemonic is not (Mnemonic.Shl or Mnemonic.Sal or Mnemonic.Shr or
                Mnemonic.Sar or Mnemonic.Shld or Mnemonic.Shrd or Mnemonic.Rol or
                Mnemonic.Ror or Mnemonic.Rcl or Mnemonic.Rcr))
                live &= ~instruction.RflagsModified;
            if (live == RflagsBits.None || instruction.FlowControl == FlowControl.Return)
                continue;
            switch (instruction.FlowControl)
            {
                case FlowControl.ConditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        !byAddress.TryGetValue(instruction.NearBranchTarget, out var conditionalTarget))
                        return false;
                    pending.Push((conditionalTarget, live));
                    pending.Push((index + 1, live));
                    break;
                case FlowControl.UnconditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        !byAddress.TryGetValue(instruction.NearBranchTarget, out var branchTarget))
                        return false;
                    pending.Push((branchTarget, live));
                    break;
                case FlowControl.Next:
                    pending.Push((index + 1, live));
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}
