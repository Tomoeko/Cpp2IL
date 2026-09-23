using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Qualifies the native exception regions reached by a managed method's decoded body.
/// This does not recover exception handlers or establish a tail transfer's managed ABI.
/// </summary>
internal static class X86CallerExceptionRegionProof
{
    public static string? Check(MethodAnalysisContext context, IReadOnlyList<Instruction> body, ISet<ulong> provedNoReturnCallIPs)
    {
        var app = context.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 || app.UnityVersion.ToString() != "2021.3.35f1")
            return null;
        var index = X64UnwindProof.ForApplication(app);
        return index == null
            ? Reject("the native unwind directory is malformed or unavailable")
            : Check(body, context.UnderlyingPointer, provedNoReturnCallIPs, index.ClassifySpan);
    }

    // The classifier is independently responsible for PE bounds, unwind format and handler
    // flags. This overload isolates native reachability and leaf-frame rules in regressions.
    internal static string? Check(IReadOnlyList<Instruction> body, ulong entry, ISet<ulong> provedNoReturnCallIPs,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify)
    {
        if (body.Count == 0 || body[0].IP != entry)
            return Reject("the decoded entry is missing");
        var byAddress = new Dictionary<ulong, Instruction>();
        var end = entry;
        foreach (var instruction in body)
        {
            if (instruction.IP != end || instruction.Length == 0 || instruction.NextIP <= instruction.IP)
                return Reject("the decoded instruction span is not contiguous");
            byAddress.Add(instruction.IP, instruction);
            end = instruction.NextIP;
        }

        var entryRegion = classify(entry, body[0].NextIP);
        if (entryRegion.Kind == X64UnwindProof.SpanKind.Unsupported)
            return Reject("the entry has unsupported native handlers or unwind metadata");
        if (entryRegion.Kind == X64UnwindProof.SpanKind.HandlerFree && entryRegion.Start != entry)
            return Reject("the managed entry is inside a different native unwind region");

        var pending = new Stack<ulong>();
        var visited = new HashSet<ulong>();
        var registerInfo = new InstructionInfoFactory();
        pending.Push(entry);
        while (pending.Count != 0)
        {
            var address = pending.Pop();
            if (!visited.Add(address))
                continue;
            if (!byAddress.TryGetValue(address, out var instruction))
                return Reject("a reachable edge has no decoded instruction boundary");
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64)
                return Reject("a reachable instruction has no valid x64 decoding");

            var region = classify(address, instruction.NextIP);
            if (region.Kind == X64UnwindProof.SpanKind.Unsupported)
                return Reject("a reachable instruction has unsupported native handlers or unwind metadata");
            if (region.Kind != entryRegion.Kind ||
                region.Kind == X64UnwindProof.SpanKind.HandlerFree &&
                (region.Start != entryRegion.Start || region.End != entryRegion.End ||
                 region.Start > address || region.End < instruction.NextIP))
                return Reject("reachable code crosses an unproved native unwind region boundary");

            if (region.Kind == X64UnwindProof.SpanKind.NoEntry)
            {
                if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                    return Reject("a calling method has no native unwind entry");
                // A plain return pops the caller's return address; this is the defined
                // unwind behavior for a frame-free x64 leaf, not a method-owned frame.
                if (instruction.Code != Code.Retnq)
                    foreach (var used in registerInfo.GetInfo(instruction).GetUsedRegisters())
                        if (Writes(used.Access) && NeedsUnwindRestoration(used.Register))
                            return Reject("a method changes its frame or nonvolatile registers without unwind metadata");
            }

            if (provedNoReturnCallIPs.Contains(address))
            {
                if (instruction.Code != Code.Call_rel32_64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None)
                    return Reject("a claimed nonreturning edge is not a plain direct x64 call");
                continue;
            }

            switch (instruction.FlowControl)
            {
                case FlowControl.Return:
                    if (instruction.Code != Code.Retnq)
                        return Reject("the native return convention is not established");
                    break;
                case FlowControl.UnconditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64)
                        return Reject("an indirect or far native exit is not established");
                    var target = instruction.NearBranchTarget;
                    // Leaving the decoded caller by a direct JMP ends this coverage proof.
                    // Existing call lowering and stack checks still own tail-call correctness.
                    if (target >= entry && target < end)
                        pending.Push(target);
                    break;
                case FlowControl.ConditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        instruction.NearBranchTarget < entry || instruction.NearBranchTarget >= end)
                        return Reject("a conditional native edge leaves the established body");
                    pending.Push(instruction.NearBranchTarget);
                    pending.Push(instruction.NextIP);
                    break;
                case FlowControl.Next:
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                    pending.Push(instruction.NextIP);
                    break;
                default:
                    return Reject("native control flow has an unproved exit");
            }
        }
        return null;
    }

    private static bool Writes(OpAccess access) =>
        access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;

    private static bool NeedsUnwindRestoration(Register register)
    {
        var full = register.GetFullRegister();
        if (full is Register.RSP or Register.RBX or Register.RBP or Register.RSI or Register.RDI or
            Register.R12 or Register.R13 or Register.R14 or Register.R15)
            return true;
        // Conservatively include wide aliases of the nonvolatile low 128-bit vector state.
        var number = (int)register;
        return number >= (int)Register.XMM6 && number <= (int)Register.XMM15 ||
               number >= (int)Register.YMM6 && number <= (int)Register.YMM15 ||
               number >= (int)Register.ZMM6 && number <= (int)Register.ZMM15;
    }

    private static string Reject(string reason) => "Native exception-region coverage is not established: " + reason;
}
