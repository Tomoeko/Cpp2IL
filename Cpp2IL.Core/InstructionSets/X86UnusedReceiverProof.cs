using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves that an incoming receiver value is dead before its register is reused.</summary>
internal static class X86UnusedReceiverProof
{
    private const string AggregateEvidenceKey = "X86UnusedReceiverProof.AggregateReceiver";
    internal sealed record AggregateReceiverEvidence(ISIL.Register Receiver);

    internal static AggregateReceiverEvidence? GetAggregateEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<AggregateReceiverEvidence>(AggregateEvidenceKey);

    public static bool IsUnused(MethodAnalysisContext method, ISIL.Register receiver)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            method.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            method.RawBytes.Length == 0 ||
            !Enum.TryParse<Register>(receiver.Name, true, out var nativeRegister) ||
            !nativeRegister.IsGPR64())
            return false;

        if (nativeRegister == Register.RCX && !method.IsStatic &&
            method.GetExtraData<X86BooleanFieldReadProof.Proof>(
                X86BooleanFieldReadProof.EvidenceKey) is { ReceiverRegister: Register.RDX } original &&
            X86BooleanFieldReadProof.Find(method, X86Utils.Iterate(method).ToArray()) is
                { ReceiverRegister: Register.RDX } current &&
            ReferenceEquals(original.Field, current.Field) && original.LoadIp == current.LoadIp)
            return true;

        // A closed parameter-array store binds every consumed incoming value to
        // a managed parameter. Its null/bounds helper exits consume no receiver,
        // so the absent instance local is established by the complete body proof.
        if (!method.IsStatic && receiver == new ISIL.Register(null, "rcx") &&
            method.ParameterOperands.FirstOrDefault() is ISIL.Register incoming && incoming == receiver &&
            X64ParameterBooleanArrayStoreProof.GetEvidence(method) is { } recorded &&
            X64ParameterBooleanArrayStoreProof.Find(method, X86Utils.Iterate(method).ToArray()) == recorded)
            return true;

        if (IsUnusedForAggregate(method, receiver))
        {
            method.PutExtraData(AggregateEvidenceKey, new AggregateReceiverEvidence(receiver));
            return true;
        }

        return IsUnused(X86Utils.Disassemble(method.RawBytes.AsSpan(), method.UnderlyingPointer, false), nativeRegister);
    }

    // The general entry-prefix proof below deliberately stops at a branch. This
    // separate route authenticates both aggregate projections and the entire
    // leaf body before establishing that no path consumes the incoming receiver.
    internal static bool IsUnusedForAggregate(MethodAnalysisContext method, ISIL.Register receiver)
    {
        if (method.Definition == null || method.IsStatic ||
            method.DeclaringType is not { IsValueType: false } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            X64AggregateScalarOperandProof.GetEvidence(method) is not { Count: 2 } recorded ||
            recorded.Select(evidence => evidence.ParameterIndex).Distinct().Count() != 2 ||
            recorded.Select(evidence => evidence.Shape.Load.IP).Distinct().Count() != 2)
            return false;
        var resolver = new X64CallingConventionResolver();
        var abi = resolver.ResolveForParameters(method);
        if (resolver.ReturnsViaHiddenBuffer(method) ||
            abi.FirstOrDefault() is not ISIL.Register original ||
            original != new ISIL.Register(null, "rcx") || receiver != original ||
            method.ParameterOperands.Count != abi.Length ||
            method.ParameterOperands[0] is not ISIL.Register current || current != original ||
            X64AggregateScalarOperandProof.ReadBody(method) is not { } body ||
            !body.Any(instruction => instruction.FlowControl is
                FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch) ||
            recorded.Any(evidence => X64AggregateScalarOperandProof.Find(method, evidence.Shape.Load) != evidence))
            return false;
        return HasNoReceiverUseInClosedBody(body, Register.RCX);
    }

    internal static bool HasNoReceiverUseInClosedBody(IReadOnlyList<Instruction> body, Register receiver)
    {
        if (body.Count is 0 or > 128 || !receiver.IsGPR64())
            return false;
        var addresses = new Dictionary<ulong, int>();
        var information = new InstructionInfoFactory();
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 || instruction.Length == 0 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None || addresses.ContainsKey(instruction.IP) ||
                index != 0 && body[index - 1].NextIP != instruction.IP ||
                information.GetInfo(instruction).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == receiver))
                return false;
            addresses.Add(instruction.IP, index);
        }

        var successors = new List<int>[body.Count];
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            successors[index] = [];
            switch (instruction.FlowControl)
            {
                case FlowControl.Return:
                    if (instruction.Code != Code.Retnq || instruction.OpCount != 0)
                        return false;
                    break;
                case FlowControl.ConditionalBranch:
                case FlowControl.UnconditionalBranch:
                    if (instruction.OpCount != 1 || instruction.Op0Kind != OpKind.NearBranch64 ||
                        instruction.NearBranchTarget <= instruction.IP ||
                        !addresses.TryGetValue(instruction.NearBranchTarget, out var target))
                        return false;
                    successors[index].Add(target);
                    if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                        break;
                    goto case FlowControl.Next;
                case FlowControl.Next:
                    if (index + 1 >= body.Count)
                        return false;
                    successors[index].Add(index + 1);
                    break;
                default:
                    // Calls may consume ABI arguments without explicit operands.
                    // Indirect exits, traps and unsupported control flow fail closed.
                    return false;
            }
        }

        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (reachable.Add(index))
                foreach (var target in successors[index])
                    pending.Push(target);
        }
        // All paths in this forward-only, closed graph terminate in a RET.
        // Unreachable decoded bytes cannot supply missing receiver provenance.
        return reachable.Count == body.Count;
    }

    internal static bool IsUnused(IEnumerable<Instruction> body, Register receiver)
    {
        if (!receiver.IsGPR64())
            return false;

        var factory = new InstructionInfoFactory();
        foreach (var instruction in body)
        {
            // Calls can consume ABI arguments not listed as native instruction operands.
            // Branches need a whole-CFG proof; this bounded entry-prefix proof does not guess.
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.FlowControl is not (FlowControl.Next or FlowControl.Return))
                return false;

            var overwritten = false;
            foreach (var used in factory.GetInfo(instruction).GetUsedRegisters())
            {
                if (used.Register.GetFullRegister() != receiver)
                    continue;
                if (used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                    return false;
                // A dword write clears the upper32 bits on x64. Byte/word writes preserve
                // bits of the incoming receiver and cannot prove that value is dead.
                overwritten |= used.Access == OpAccess.Write && used.Register.GetSize() is 4 or 8;
            }

            if (overwritten || instruction.Mnemonic == Mnemonic.Ret)
                return true;
            if (instruction.FlowControl != FlowControl.Next)
                return false;
        }

        return false;
    }
}
