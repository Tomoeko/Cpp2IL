using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Classifies exact metadata-initialization guards. Only a closed literal-return case is removed,
/// after its native load is proven to become the same ldstr. Other guards remain unresolved.
/// </summary>
internal static class X86MetadataGuardProof
{
    internal sealed record LiteralGuard(ulong GuardAddress, ulong HelperAddress, ulong LiteralSlot,
        ulong MaterializationAddress, IReadOnlyList<ulong> RemovedAddresses);

    public static LiteralGuard? Find(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1" ||
            context.ReturnType.FullName != "System.String")
            return null;
        return Find(body, RuntimeMetadataHelpers(context),
            address => context.AppContext.LibCpp2IlContext.GetLiteralByAddress(address) != null);
    }

    // This only classifies a still-unrecovered native guard. Its metadata helper, flag
    // write and branch are retained; arbitrary token initialization may affect class state.
    public static HashSet<ulong> FindUnresolvedInitializationGuards(MethodAnalysisContext context,
        IReadOnlyList<Instruction> body)
    {
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1")
            return [];
        return FindUnresolvedInitializationGuards(body, RuntimeMetadataHelpers(context),
            address => IsKnownMetadataSlot(context, address));
    }

    internal static HashSet<ulong> FindUnresolvedInitializationGuards(IReadOnlyList<Instruction> body,
        ISet<ulong> helperAddresses, Func<ulong, bool>? isKnownMetadataSlot = null)
    {
        var guards = new HashSet<ulong>();
        for (var index = 0; index + 5 < body.Count; index++)
            if (TryMatchInitializationGuard(body, index, helperAddresses,
                    isKnownMetadataSlot ?? (_ => false), out _))
                guards.Add(body[index].IP);
        return guards;
    }

    internal static LiteralGuard? Find(IReadOnlyList<Instruction> body, ISet<ulong> helperAddresses,
        Func<ulong, bool> isLiteralSlot)
    {
        for (var index = 0; index + 5 < body.Count; index++)
        {
            if (!TryMatchInitializationGuard(body, index, helperAddresses, isLiteralSlot,
                    out var layout))
                continue;
            var argument = body[layout.ArgumentIndex];
            var call = body[layout.CallIndex];
            var materialize = body[layout.NextIndex];
            if (materialize.Mnemonic != Mnemonic.Mov || materialize.Op0Register != Register.RAX ||
                !IsRipMemory(materialize, 1, 8) ||
                materialize.IPRelativeMemoryAddress != argument.IPRelativeMemoryAddress ||
                !isLiteralSlot(argument.IPRelativeMemoryAddress))
                continue;

            // RAX is overwritten with the evidenced literal, and only stack restoration may
            // follow before RET. Thus no removed helper's volatile registers or flags escape.
            // General liveness across calls, joins, partial writes and tail calls is not guessed.
            var returnIndex = layout.NextIndex + 1;
            while (returnIndex < body.Count && IsStackRestore(body[returnIndex]))
                returnIndex++;
            if (returnIndex >= body.Count || body[returnIndex].Mnemonic != Mnemonic.Ret || body[returnIndex].OpCount != 0)
                continue;

            // The native byte range may include padding and an adjacent unmanaged function.
            // Bound this proof only after establishing a closed entry path: a straight-line
            // prefix, the exact guard/merge above, and stack restoration ending at RET. No
            // accepted edge reaches appended bytes; arbitrary method lifting is not truncated.
            var reachablePrefix = body.Take(returnIndex + 1);
            if (body.Take(index).Any(instruction => instruction.FlowControl != FlowControl.Next) ||
                reachablePrefix.Any(instruction => instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 &&
                    instruction.NearBranchTarget >= body[index].IP && instruction.NearBranchTarget < materialize.IP))
                continue;
            return new LiteralGuard(body[index].IP, call.NearBranchTarget, argument.IPRelativeMemoryAddress,
                materialize.IP, new[] { body[index].IP, body[layout.BranchIndex].IP,
                    argument.IP, call.IP, body[layout.StoreIndex].IP });
        }
        return null;
    }

    private static bool IsKnownMetadataSlot(MethodAnalysisContext context, ulong address) =>
        context.AppContext.LibCpp2IlContext.GetRawTypeGlobalByAddress(address)?.IsValid == true ||
        context.AppContext.LibCpp2IlContext.GetLiteralByAddress(address) != null;

    private static HashSet<ulong> RuntimeMetadataHelpers(MethodAnalysisContext context)
    {
        var helpers = context.AppContext.GetOrCreateKeyFunctionAddresses();
        var addresses = new HashSet<ulong>
        {
            helpers.il2cpp_codegen_initialize_runtime_metadata,
            helpers.il2cpp_codegen_initialize_runtime_metadata_inline,
        };
        addresses.Remove(0);
        return addresses;
    }

    private readonly record struct GuardLayout(int BranchIndex, int ArgumentIndex, int CallIndex,
        int StoreIndex, int NextIndex);

    private static bool TryMatchInitializationGuard(IReadOnlyList<Instruction> body, int index,
        ISet<ulong> helperAddresses, Func<ulong, bool> isKnownMetadataSlot, out GuardLayout layout)
    {
        layout = default;
        for (var movedArgument = 0; movedArgument <= 1; movedArgument++)
        {
            if (index + 5 + movedArgument >= body.Count ||
                movedArgument == 1 && !IsFlagPreservingRegisterMove(body[index + 1]))
                continue;
            var candidate = new GuardLayout(index + 1 + movedArgument, index + 2 + movedArgument,
                index + 3 + movedArgument, index + 4 + movedArgument, index + 5 + movedArgument);
            if (!MatchesInitializationGuard(body, index, candidate, helperAddresses) ||
                movedArgument == 1 && !isKnownMetadataSlot(body[candidate.ArgumentIndex].IPRelativeMemoryAddress))
                continue;
            layout = candidate;
            return true;
        }
        return false;
    }

    private static bool MatchesInitializationGuard(IReadOnlyList<Instruction> body, int index,
        GuardLayout layout, ISet<ulong> helperAddresses)
    {
        var compare = body[index];
        var branch = body[layout.BranchIndex];
        var argument = body[layout.ArgumentIndex];
        var call = body[layout.CallIndex];
        var store = body[layout.StoreIndex];
        return compare.Mnemonic == Mnemonic.Cmp && IsRipMemory(compare, 0, 1) &&
               compare.Op1Kind == OpKind.Immediate8 && compare.Immediate8 == 0 &&
               branch.Mnemonic == Mnemonic.Jne && branch.Op0Kind == OpKind.NearBranch64 &&
               branch.NearBranchTarget == body[layout.NextIndex].IP &&
               argument.Mnemonic == Mnemonic.Lea && argument.Op0Register == Register.RCX &&
               IsRipMemory(argument, 1) &&
               call.Mnemonic == Mnemonic.Call && call.Op0Kind == OpKind.NearBranch64 &&
               helperAddresses.Contains(call.NearBranchTarget) &&
               store.Mnemonic == Mnemonic.Mov && IsRipMemory(store, 0, 1) &&
               store.Op1Kind == OpKind.Immediate8 && store.Immediate8 == 1 &&
               store.IPRelativeMemoryAddress == compare.IPRelativeMemoryAddress &&
               compare.IPRelativeMemoryAddress != argument.IPRelativeMemoryAddress;
    }

    private static bool IsFlagPreservingRegisterMove(Instruction instruction)
    {
        if (instruction.Mnemonic != Mnemonic.Mov || instruction.CodeSize != CodeSize.Code64 ||
            instruction.Op0Kind != OpKind.Register || instruction.Op1Kind != OpKind.Register ||
            instruction.OpCount != 2 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
            instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None)
            return false;
        var width = instruction.Op0Register.GetSize();
        if (width is not (4 or 8) || instruction.Op1Register.GetSize() != width)
            return false;
        // A stack/frame-pointer rewrite needs separate unwind and alias proofs.
        return instruction.Op0Register.GetFullRegister() is not (Register.RSP or Register.RBP) &&
               instruction.Op1Register.GetFullRegister() is not (Register.RSP or Register.RBP);
    }

    private static bool IsRipMemory(Instruction instruction, int operand, int bytes = 0) =>
        instruction.GetOpKind(operand) == OpKind.Memory && instruction.MemoryBase == Register.RIP &&
        instruction.MemoryIndex == Register.None && instruction.SegmentPrefix == Register.None &&
        (bytes == 0 || instruction.MemorySize.GetSize() == bytes);

    private static bool IsStackRestore(Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Add && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP && instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 ||
        instruction.Mnemonic == Mnemonic.Pop && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register is Register.RBX or Register.RBP or Register.RSI or Register.RDI or Register.R12 or Register.R13 or Register.R14 or Register.R15;
}
