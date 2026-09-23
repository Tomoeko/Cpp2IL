using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Distinguishes an established native bounds-throw exit from an ordinary missing
/// decoded edge. This classification does not recover the caller's managed array
/// operation, exception effects, or source body.
/// </summary>
internal static class X86TerminalBoundsThrowDiagnostic
{
    internal const string MissingBoundary =
        "Native exception-region coverage is not established: a reachable edge has no decoded instruction boundary";

    internal const string UnresolvedBoundsSemantics =
        "Native bounds helper is proved nonreturning; the caller's managed bounds and exception semantics are unresolved";

    private static readonly ConditionalWeakTable<ApplicationAnalysisContext,
        ConcurrentDictionary<ulong, Lazy<bool>>> BoundsHelperCache = new();

    internal static string? TryClassify(MethodAnalysisContext method, IReadOnlyList<Instruction> body,
        ISet<ulong> provedNoReturnCallIPs, string originalFailure)
    {
        var app = method.AppContext;
        if (originalFailure != MissingBoundary ||
            app.Binary is not PE { PointerSizeBytes: 8 } pe ||
            pe.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.RawBytes.Length == 0 ||
            method.UnderlyingPointer > ulong.MaxValue - (ulong)method.RawBytes.Length)
            return null;

        var entry = method.UnderlyingPointer;
        var end = entry + (ulong)method.RawBytes.Length;
        var span = unwind.ClassifySpan(entry, end);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != entry || span.RootStart != entry)
            return null;

        // RawBytes must be precisely the executable PE bytes qualified by the unwind
        // reader; neither a guessed next-method boundary nor an appended padding byte
        // is evidence for a managed exception path.
        var first = pe.MapVirtualAddressToRaw(entry, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || last < first || last - first != method.RawBytes.Length - 1 ||
            last >= image.Length ||
            !image.Slice((int)first, method.RawBytes.Length).SequenceEqual(method.RawBytes.AsSpan()))
            return null;

        var cache = BoundsHelperCache.GetValue(app,
            _ => new ConcurrentDictionary<ulong, Lazy<bool>>());
        return TryClassify(body, entry, end, provedNoReturnCallIPs, originalFailure,
            unwind.ClassifySpan,
            target => cache.GetOrAdd(target, address => new Lazy<bool>(
                () => X86RuntimeBoundsThrowProof.TryIdentify(app, address),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value);
    }

    // Classifier and helper proof are injected only for synthetic control-flow tests.
    // Production additionally authenticates the exact profile and file-backed PE span.
    internal static string? TryClassify(IReadOnlyList<Instruction> body, ulong entry, ulong rawEnd,
        ISet<ulong> provedNoReturnCallIPs, string originalFailure,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify,
        Func<ulong, bool> provesBoundsThrow)
    {
        if (originalFailure != MissingBoundary || body.Count is 0 or > 4096 ||
            rawEnd <= entry || body[0].IP != entry)
            return null;
        var terminal = body[^1];
        if (terminal.NextIP != rawEnd || terminal.Code != Code.Call_rel32_64 ||
            terminal.Op0Kind != OpKind.NearBranch64 || terminal.NearBranchTarget == 0 ||
            terminal.CodeSize != CodeSize.Code64 || terminal.HasLockPrefix ||
            terminal.HasRepPrefix || terminal.HasRepnePrefix ||
            terminal.SegmentPrefix != Register.None)
            return null;

        var next = entry;
        foreach (var instruction in body)
        {
            if (instruction.IP != next || instruction.Length == 0 ||
                instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.NextIP <= instruction.IP || instruction.NextIP > rawEnd)
                return null;
            next = instruction.NextIP;
        }

        if (!provesBoundsThrow(terminal.NearBranchTarget))
            return null;

        // Adding only this call as a native no-return edge must close every reachable
        // path. An alternate missing edge, handler, boundary crossing or bad return
        // convention keeps the original failure instead of gaining this diagnosis.
        var proved = new HashSet<ulong>(provedNoReturnCallIPs) { terminal.IP };
        return X86CallerExceptionRegionProof.Check(body, entry, proved, classify) == null
            ? UnresolvedBoundsSemantics : null;
    }
}
