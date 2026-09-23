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
        return FindUnresolvedInitializationGuards(body, RuntimeMetadataHelpers(context));
    }

    internal static HashSet<ulong> FindUnresolvedInitializationGuards(IReadOnlyList<Instruction> body,
        ISet<ulong> helperAddresses)
    {
        var guards = new HashSet<ulong>();
        for (var index = 0; index + 5 < body.Count; index++)
            if (MatchesInitializationGuard(body, index, helperAddresses))
                guards.Add(body[index].IP);
        return guards;
    }

    internal static LiteralGuard? Find(IReadOnlyList<Instruction> body, ISet<ulong> helperAddresses,
        Func<ulong, bool> isLiteralSlot)
    {
        for (var index = 0; index + 5 < body.Count; index++)
        {
            if (!MatchesInitializationGuard(body, index, helperAddresses))
                continue;
            var argument = body[index + 2];
            var call = body[index + 3];
            var materialize = body[index + 5];
            if (materialize.Mnemonic != Mnemonic.Mov || materialize.Op0Register != Register.RAX ||
                !IsRipMemory(materialize, 1, 8) ||
                materialize.IPRelativeMemoryAddress != argument.IPRelativeMemoryAddress ||
                !isLiteralSlot(argument.IPRelativeMemoryAddress))
                continue;

            // RAX is overwritten with the evidenced literal, and only stack restoration may
            // follow before RET. Thus no removed helper's volatile registers or flags escape.
            // General liveness across calls, joins, partial writes and tail calls is not guessed.
            var returnIndex = index + 6;
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
                materialize.IP, body.Skip(index).Take(5).Select(instruction => instruction.IP).ToArray());
        }
        return null;
    }

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

    private static bool MatchesInitializationGuard(IReadOnlyList<Instruction> body, int index,
        ISet<ulong> helperAddresses)
    {
        var compare = body[index];
        var branch = body[index + 1];
        var argument = body[index + 2];
        var call = body[index + 3];
        var store = body[index + 4];
        return compare.Mnemonic == Mnemonic.Cmp && IsRipMemory(compare, 0, 1) &&
               compare.Op1Kind == OpKind.Immediate8 && compare.Immediate8 == 0 &&
               branch.Mnemonic == Mnemonic.Jne && branch.Op0Kind == OpKind.NearBranch64 &&
               branch.NearBranchTarget == body[index + 5].IP &&
               argument.Mnemonic == Mnemonic.Lea && argument.Op0Register == Register.RCX &&
               IsRipMemory(argument, 1) &&
               call.Mnemonic == Mnemonic.Call && call.Op0Kind == OpKind.NearBranch64 &&
               helperAddresses.Contains(call.NearBranchTarget) &&
               store.Mnemonic == Mnemonic.Mov && IsRipMemory(store, 0, 1) &&
               store.Op1Kind == OpKind.Immediate8 && store.Immediate8 == 1 &&
               store.IPRelativeMemoryAddress == compare.IPRelativeMemoryAddress &&
               compare.IPRelativeMemoryAddress != argument.IPRelativeMemoryAddress;
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
