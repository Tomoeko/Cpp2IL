using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Reads evidence for independently guarded x64 reference-array accesses. This
/// class does not remove native guards, emit managed accesses, or claim that an
/// enclosing method is recoverable. In particular, each field load remains a
/// distinct value after an intervening call or store. Any guard rewrite must
/// separately verify typed lifted operations at the recorded native IPs and
/// preserve the order of all calls, stores, and possible managed exceptions.
/// </summary>
internal static class X64ArrayGuardSiteProof
{
    internal enum ReadKind { Move, Compare }
    internal enum EffectKind { DirectCall, MemoryStore }

    internal sealed record Effect(ulong Ip, EffectKind Kind, Code NativeCode,
        ulong DirectCallTarget, Register StoreBase, Register StoreIndex,
        int StoreScale, ulong StoreDisplacement, int StoreWidth);

    internal sealed record ReferenceComparison(ulong LeftElementReadIp,
        ulong RightElementReadIp, ulong CompareIp, ulong SetEqualIp,
        Register LeftRegister);

    internal sealed record Site(ulong ArrayFieldReadIp,
        ulong OwnerNullTestIp, ulong OwnerNullBranchIp,
        ulong ArrayNullTestIp, ulong ArrayNullBranchIp,
        ulong BoundsCompareIp, ulong BoundsBranchIp, ulong ElementReadIp,
        ulong IndexExtensionIp, Register ExtendedIndexRegister,
        ulong? OwnerFieldReadIp, FieldAnalysisContext? OwnerField,
        FieldAnalysisContext ArrayField, Register IndexArgument, ReadKind Kind,
        IReadOnlyList<Effect> EffectsSincePreviousAccess);

    internal sealed record Evidence(IReadOnlyList<Site> Sites,
        ulong NullHelperCallIp, ulong NullTrapIp,
        ulong BoundsHelperCallIp, ulong BoundsTrapIp,
        ulong NativeEndExclusiveIp, ReferenceComparison? Comparison);

    internal sealed record NativeSite(int ArrayFieldRead, int OwnerNullBranch,
        int ArrayNullBranch, int BoundsBranch, int ElementRead,
        int IndexExtension, Register ExtendedIndexRegister,
        Register ArrayRegister, Register IndexArgument, ReadKind Kind,
        IReadOnlyList<Effect> EffectsSincePreviousAccess);

    internal sealed record NativeComparison(int LeftElementRead,
        int RightElementRead, int SetEqual, Register LeftRegister);

    internal sealed record NativeEvidence(IReadOnlyList<NativeSite> Sites,
        int NullHelperCall, int BoundsHelperCall,
        NativeComparison? Comparison);

    // A separate bounded path for two object-array parameters. The existing
    // field-origin evidence and matcher remain independent of this ABI shape.
    internal sealed record ParameterSite(ParameterAnalysisContext ArrayParameter,
        Register ArrayEntryRegister, Register ArrayUseRegister,
        IReadOnlyList<ulong> ArrayCaptureIps,
        ulong ArrayNullTestIp, ulong ArrayNullBranchIp,
        ulong BoundsCompareIp, ulong BoundsBranchIp, ulong ElementReadIp,
        ReadKind Kind, IReadOnlyList<Effect> EffectsSincePreviousAccess);

    internal sealed record ParameterEvidence(IReadOnlyList<ParameterSite> Sites,
        ParameterAnalysisContext IndexParameter, Register IndexEntryRegister,
        ulong IndexExtensionIp, Register IndexUseRegister,
        ulong OffsetLeaIp, Register OffsetRegister,
        ulong NullHelperCallIp, ulong NullTrapIp,
        ulong BoundsHelperCallIp, ulong BoundsTrapIp,
        ulong NativeEndExclusiveIp, ReferenceComparison Comparison);

    internal sealed record NativeParameterEvidence(int FirstElementRead,
        int SecondElementRead, int IndexExtension, int OffsetLea,
        int ArrayCapture, int MarkCall, int NullHelperCall,
        int BoundsHelperCall, int SetEqual);

    internal static ParameterEvidence? FindParameter(MethodAnalysisContext method,
        IReadOnlyList<Instruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                method.IsStatic || method.IsVirtual ||
                method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                method.DeclaringType is not
                    { IsValueType: false,
                        Definition:
                        { RawType:
                            { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                                NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                owner.Name != owner.DefaultName ||
                owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                method.Parameters is not [{ } left, { } right, { } index] ||
                method.OverrideReturnType != null ||
                method.Definition is not
                    { RawReturnType:
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                            NumMods: 0, Byref: 0, Pinned: 0 } } ||
                !ReferenceEquals(method.ReturnType,
                    app.SystemTypes.SystemBooleanType) ||
                !TryBindObjectArrayParameter(method, left, 0) ||
                !TryBindObjectArrayParameter(method, right, 1) ||
                !TryBindInt32Parameter(method, index, 2) ||
                decoded.Count is < 30 or > 512 ||
                !TryCompleteFileBackedRegion(method, decoded, pe, unwind,
                    out var complete) ||
                TryProveParameterNative(complete, method.UnderlyingPointer,
                    unwind.ClassifySpan,
                    target => X86RuntimeNullThrowProof.TryIdentify(app, target) != null,
                    target => X86RuntimeBoundsThrowProof.TryIdentify(app, target)) is not
                    { } native)
                return null;

            var targetAddress = complete[native.MarkCall].NearBranchTarget;
            if (!app.MethodsByAddress.TryGetValue(targetAddress, out var bindings) ||
                bindings is not [{ } target] ||
                !ReferenceEquals(target.DeclaringType, method.DeclaringType) ||
                target.IsStatic || target.IsVirtual || !target.IsVoid ||
                target.Parameters.Count != 0 ||
                target.OverrideReturnType != null ||
                target.Definition is not
                    { RawReturnType:
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                            NumMods: 0, Byref: 0, Pinned: 0 } } ||
                target.Name != target.DefaultName ||
                target.Attributes != target.DefaultAttributes ||
                target.ImplAttributes != target.DefaultImplAttributes ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target))
                return null;

            var mark = complete[native.MarkCall];
            var effect = new Effect(mark.IP, EffectKind.DirectCall,
                mark.Code, targetAddress, Register.None, Register.None, 0, 0, 0);
            var sites = new ParameterSite[]
            {
                new(left, Register.RDX, Register.RDX, Array.Empty<ulong>(),
                    complete[7].IP, complete[8].IP, complete[9].IP,
                    complete[10].IP, complete[native.FirstElementRead].IP,
                    ReadKind.Move, Array.Empty<Effect>()),
                new(right, Register.R8, Register.RDI,
                    [complete[native.ArrayCapture].IP],
                    complete[15].IP, complete[16].IP, complete[17].IP,
                    complete[18].IP, complete[native.SecondElementRead].IP,
                    ReadKind.Compare, [effect]),
            };
            var comparison = new ReferenceComparison(
                complete[native.FirstElementRead].IP,
                complete[native.SecondElementRead].IP,
                complete[native.SecondElementRead].IP,
                complete[native.SetEqual].IP, Register.RBP);
            return new ParameterEvidence(sites, index, Register.R9,
                complete[native.IndexExtension].IP, Register.RBX,
                complete[native.OffsetLea].IP, Register.RSI,
                complete[native.NullHelperCall].IP,
                complete[native.NullHelperCall + 1].IP,
                complete[native.BoundsHelperCall].IP,
                complete[native.BoundsHelperCall + 1].IP,
                complete[^1].NextIP, comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryBindObjectArrayParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, int position) =>
        parameter.Definition != null &&
        parameter.ParameterIndex == position &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        !parameter.IsRef && parameter.OverrideParameterType == null &&
        parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.ParameterType is SzArrayTypeAnalysisContext array &&
        ReferenceEquals(array.ElementType,
            method.AppContext.SystemTypes.SystemObjectType) &&
        parameter.Definition.RawType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                NumMods: 0, Byref: 0, Pinned: 0 };

    private static bool TryBindInt32Parameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, int position) =>
        parameter.Definition != null &&
        parameter.ParameterIndex == position &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        !parameter.IsRef && parameter.OverrideParameterType == null &&
        parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes &&
        ReferenceEquals(parameter.ParameterType,
            method.AppContext.SystemTypes.SystemInt32Type) &&
        parameter.Definition.RawType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 };

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<Instruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                method.IsStatic || method.DeclaringType == null ||
                decoded.Count is < 16 or > 512 ||
                !TryCompleteFileBackedRegion(method, decoded, pe, unwind,
                    out var complete))
                return null;

            var native = TryProveNative(complete, method.UnderlyingPointer,
                unwind.ClassifySpan,
                target => X86RuntimeNullThrowProof.TryIdentify(app, target) != null,
                target => X86RuntimeBoundsThrowProof.TryIdentify(app, target));
            if (native == null ||
                NativeGraph.Create(complete, native.NullHelperCall,
                    native.BoundsHelperCall) is not { } graph)
                return null;

            var facts = new NativeFacts(complete);
            var sites = new List<Site>(native.Sites.Count);
            foreach (var site in native.Sites)
            {
                var load = complete[site.ArrayFieldRead];
                var owner = TraceRegister(method, facts, graph,
                    site.ArrayFieldRead, load.MemoryBase.GetFullRegister(), 0);
                var ownerRoot = owner?.Field == null ? owner :
                    TraceRegister(method, facts, graph, owner.Definition,
                        complete[owner.Definition].MemoryBase.GetFullRegister(), 0);
                var origin = TraceRegister(method, facts, graph,
                    site.ArrayFieldRead + 1,
                    site.ArrayRegister, 0);
                if (owner == null || ownerRoot is not { Field: null } ||
                    origin is not { Field: { } field, Definition: var definition } ||
                    definition != site.ArrayFieldRead ||
                    field.BackingData?.Field.RawFieldType is not
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                            NumMods: 0, Byref: 0, Pinned: 0 } ||
                    field.FieldType is not SzArrayTypeAnalysisContext array ||
                    array.ElementType.IsValueType ||
                    native.Comparison != null &&
                    !ReferenceEquals(array.ElementType,
                        app.SystemTypes.SystemObjectType) ||
                    !TryBindInt32Argument(method, site.IndexArgument) ||
                    load.MemoryBase.GetFullRegister() == site.ArrayRegister)
                    return null;

                sites.Add(new Site(load.IP,
                    complete[site.OwnerNullBranch - 1].IP,
                    complete[site.OwnerNullBranch].IP,
                    complete[site.ArrayNullBranch - 1].IP,
                    complete[site.ArrayNullBranch].IP,
                    complete[site.BoundsBranch - 1].IP,
                    complete[site.BoundsBranch].IP,
                    complete[site.ElementRead].IP,
                    complete[site.IndexExtension].IP,
                    site.ExtendedIndexRegister,
                    owner.Field == null ? null : complete[owner.Definition].IP,
                    owner.Field, field,
                    site.IndexArgument, site.Kind, site.EffectsSincePreviousAccess));
            }

            if (native.Comparison != null &&
                !ReferenceEquals(method.ReturnType,
                    app.SystemTypes.SystemBooleanType))
                return null;

            var comparison = native.Comparison is { } proved
                ? new ReferenceComparison(
                    complete[proved.LeftElementRead].IP,
                    complete[proved.RightElementRead].IP,
                    complete[proved.RightElementRead].IP,
                    complete[proved.SetEqual].IP,
                    proved.LeftRegister)
                : null;

            return new Evidence(sites, complete[native.NullHelperCall].IP,
                complete[native.NullHelperCall + 1].IP,
                complete[native.BoundsHelperCall].IP,
                complete[native.BoundsHelperCall + 1].IP,
                complete[^1].NextIP, comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // The injected predicates make guard/CFG mutations testable without an
    // installed player. Production supplies independently proved runtime helpers
    // and an unwind classifier over exact file-backed bytes.
    internal static NativeEvidence? TryProveNative(IReadOnlyList<Instruction> body,
        ulong entry, Func<ulong, ulong, X64UnwindProof.SpanClassification> classify,
        Func<ulong, bool> provesNull, Func<ulong, bool> provesBounds)
    {
        if (body.Count is < 16 or > 513 || body[0].IP != entry ||
            body[^1].Code != Code.Int3 ||
            !DirectCall(body[^2]) || body[^3].Code != Code.Int3 ||
            !DirectCall(body[^4]) ||
            body[^4].NearBranchTarget == body[^2].NearBranchTarget ||
            !provesNull(body[^4].NearBranchTarget) ||
            !provesBounds(body[^2].NearBranchTarget))
            return null;

        var nextAddress = entry;
        foreach (var instruction in body)
        {
            if (instruction.IP != nextAddress || instruction.Length == 0 ||
                instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None)
                return null;
            nextAddress = instruction.NextIP;
        }

        var nullCall = body.Count - 4;
        var boundsCall = body.Count - 2;
        var entryRegion = classify(entry, body[^1].NextIP);
        if (entryRegion.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            entryRegion.Start != entry || entryRegion.RootStart != entry ||
            entryRegion.End != body[^1].NextIP ||
            X86CallerExceptionRegionProof.Check(body, entry,
                new HashSet<ulong> { body[nullCall].IP, body[boundsCall].IP },
                classify) != null ||
            NativeGraph.Create(body, nullCall, boundsCall) is not { } graph)
            return null;

        var facts = new NativeFacts(body);
        var sites = new List<NativeSite>();
        for (var index = 2; index + 2 < nullCall; index++)
        {
            if (TrySite(facts, graph, index, nullCall, boundsCall) is { } site)
                sites.Add(site);
        }
        if (sites.Count != 2 ||
            sites.Select(site => site.ElementRead).Distinct().Count() != sites.Count ||
            sites.Select(site => site.ArrayFieldRead).Distinct().Count() != sites.Count ||
            sites.Select(site => site.BoundsBranch).Distinct().Count() != sites.Count)
            return null;

        // The first dereference using an owner guard must occur before any
        // observable work. Later accesses may reuse that proved, unchanged
        // owner after calls or stores, while reloading their array fields.
        foreach (var owner in sites.GroupBy(site => site.OwnerNullBranch))
        {
            var firstLoad = owner.Min(site => site.ArrayFieldRead);
            if (!graph.HasOnlyLinearPredecessors(owner.Key, firstLoad) ||
                Enumerable.Range(owner.Key + 1, firstLoad - owner.Key - 1)
                    .Any(index => !PureRegisterPreparation(body[index])))
                return null;
        }

        foreach (var extension in sites.GroupBy(site => site.IndexExtension))
        {
            var register = extension.First().ExtendedIndexRegister;
            if (extension.Any(site => site.ExtendedIndexRegister != register))
                return null;
            var approved = new HashSet<int>(extension.Select(site => site.ElementRead));
            foreach (var site in extension.Where(site => site.IndexExtension <
                         site.BoundsBranch - 1))
                approved.Add(site.BoundsBranch - 1);
            if (!graph.HasOnlyApprovedIndexUses(facts, extension.Key,
                    register, approved))
                return null;
        }

        var nullBranches = new HashSet<int>(body.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.FlowControl == FlowControl.ConditionalBranch &&
                           item.instruction.NearBranchTarget == body[nullCall].IP)
            .Select(item => item.index));
        var expectedNullBranches = new HashSet<int>(sites.Select(site => site.ArrayNullBranch)
            .Concat(sites.Select(site => site.OwnerNullBranch)));
        var boundsBranches = new HashSet<int>(body.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.FlowControl == FlowControl.ConditionalBranch &&
                           item.instruction.NearBranchTarget == body[boundsCall].IP)
            .Select(item => item.index));
        if (!nullBranches.SetEquals(expectedNullBranches) ||
            !boundsBranches.SetEquals(sites.Select(site => site.BoundsBranch)) ||
            !graph.HasExactlyThesePredecessors(nullCall, expectedNullBranches) ||
            !graph.HasExactlyThesePredecessors(boundsCall, boundsBranches) ||
            !graph.HasExactlyThesePredecessors(nullCall + 1, new HashSet<int>()) ||
            !graph.HasExactlyThesePredecessors(boundsCall + 1, new HashSet<int>()) ||
            body.Any(instruction => instruction.FlowControl == FlowControl.UnconditionalBranch &&
                (instruction.NearBranchTarget == body[nullCall].IP ||
                 instruction.NearBranchTarget == body[boundsCall].IP)))
            return null;

        sites.Sort((left, right) => left.ElementRead.CompareTo(right.ElementRead));
        NativeComparison? comparison = null;
        if (sites[0].Kind != ReadKind.Move ||
            sites[1].Kind != ReadKind.Compare ||
            !TryReferenceComparison(facts, graph, sites[0], sites[1],
                out comparison))
            return null;

        foreach (var site in sites)
            if (!graph.HasSingleGuardFlagConsumer(body, site.OwnerNullBranch - 1,
                    site.OwnerNullBranch) ||
                !graph.HasSingleGuardFlagConsumer(body, site.ArrayNullBranch - 1,
                    site.ArrayNullBranch) ||
                !graph.HasSingleGuardFlagConsumer(body, site.BoundsBranch - 1,
                    site.BoundsBranch))
                return null;

        // This bounded path proves two array reads and their equality result.
        // A separate proof is needed for effects outside the two-read interval.
        for (var index = 0; index < sites[0].ArrayFieldRead; index++)
            if (facts.HasCallOrNonStackStore(index))
                return null;
        for (var index = sites[1].ElementRead + 1; index < nullCall; index++)
            if (facts.HasCallOrStore(index))
                return null;

        var ordered = new List<NativeSite>(sites.Count);
        var previousElement = -1;
        foreach (var site in sites)
        {
            if (site.ArrayFieldRead <= previousElement)
                return null;
            var effects = new List<Effect>();
            if (previousElement >= 0)
                for (var index = previousElement + 1;
                     index < site.ArrayFieldRead; index++)
                {
                    if (!facts.HasCallOrStore(index))
                        continue;
                    if (!facts.TryEffect(index, out var effect))
                        return null;
                    effects.Add(effect);
                }
            ordered.Add(site with { EffectsSincePreviousAccess = effects });
            previousElement = site.ElementRead;
        }
        return new NativeEvidence(ordered, nullCall, boundsCall, comparison);
    }

    // The first parameter-origin case is an exact two-site x64 shape. The shared
    // LEA computes only the element offset; the two source arrays remain distinct
    // and the direct managed call stays between their reads. This matcher makes
    // no claim about stack-spilled arguments or arbitrary address arithmetic.
    internal static NativeParameterEvidence? TryProveParameterNative(
        IReadOnlyList<Instruction> body, ulong entry,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify,
        Func<ulong, bool> provesNull, Func<ulong, bool> provesBounds)
    {
        if (body.Count != 31 || body[0].IP != entry ||
            !DirectCall(body[14]) || !DirectCall(body[27]) ||
            !DirectCall(body[29]) ||
            body[14].NearBranchTarget == body[27].NearBranchTarget ||
            body[14].NearBranchTarget == body[29].NearBranchTarget ||
            body[27].NearBranchTarget == body[29].NearBranchTarget ||
            !provesNull(body[27].NearBranchTarget) ||
            !provesBounds(body[29].NearBranchTarget))
            return null;

        var next = entry;
        foreach (var instruction in body)
        {
            if (instruction.IP != next || instruction.Length == 0 ||
                instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None)
                return null;
            next = instruction.NextIP;
        }
        var region = classify(entry, next);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != entry || region.RootStart != entry ||
            region.End != next ||
            X86CallerExceptionRegionProof.Check(body, entry,
                new HashSet<ulong> { body[27].IP, body[29].IP },
                classify) != null)
            return null;

        static bool Reg(Instruction instruction, Code code,
            Register destination, Register source) =>
            instruction.Code == code && instruction.OpCount == 2 &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op1Kind == OpKind.Register &&
            instruction.Op0Register == destination &&
            instruction.Op1Register == source;

        static bool StackSlot(Instruction instruction, Code code,
            Register register, ulong offset, bool save) =>
            instruction.Code == code && instruction.OpCount == 2 &&
            instruction.GetOpKind(save ? 0 : 1) == OpKind.Memory &&
            instruction.GetOpKind(save ? 1 : 0) == OpKind.Register &&
            instruction.GetOpRegister(save ? 1 : 0) == register &&
            Memory(instruction, save ? 0 : 1,
                Register.RSP, Register.None, 1, offset, 8);

        static bool StackAdjust(Instruction instruction, Code code) =>
            instruction.Code == code && instruction.OpCount == 2 &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op0Register == Register.RSP &&
            instruction.Op1Kind == OpKind.Immediate8to64 &&
            instruction.Immediate8 == 0x20;

        static bool ArrayLengthCompare(Instruction instruction,
            Register array) =>
            instruction.Code == Code.Cmp_r32_rm32 &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op0Register == Register.EBX &&
            Memory(instruction, 1, array, Register.None, 1, 0x18, 4);

        static bool SplitElement(Instruction instruction,
            Code code, Register destination, Register array) =>
            instruction.Code == code && instruction.OpCount == 2 &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op0Register == destination &&
            Memory(instruction, 1, Register.RSI, array, 1, 0, 8);

        if (!StackSlot(body[0], Code.Mov_rm64_r64, Register.RBX, 8, true) ||
            !StackSlot(body[1], Code.Mov_rm64_r64, Register.RBP, 0x10, true) ||
            !StackSlot(body[2], Code.Mov_rm64_r64, Register.RSI, 0x18, true) ||
            body[3].Code != Code.Push_r64 ||
            body[3].Op0Kind != OpKind.Register ||
            body[3].Op0Register != Register.RDI ||
            !StackAdjust(body[4], Code.Sub_rm64_imm8) ||
            !Reg(body[5], Code.Movsxd_r64_rm32,
                Register.RBX, Register.R9D) ||
            !(Reg(body[6], Code.Mov_r64_rm64,
                  Register.RDI, Register.R8) ||
              Reg(body[6], Code.Mov_rm64_r64,
                  Register.RDI, Register.R8)) ||
            !Reg(body[7], Code.Test_rm64_r64,
                Register.RDX, Register.RDX) ||
            !Branch(body[8], Mnemonic.Je, body[27].IP) ||
            !ArrayLengthCompare(body[9], Register.RDX) ||
            !Branch(body[10], Mnemonic.Jae, body[29].IP) ||
            body[11].Code != Code.Lea_r64_m ||
            body[11].Op0Kind != OpKind.Register ||
            body[11].Op0Register != Register.RSI ||
            body[11].Op1Kind != OpKind.Memory ||
            body[11].MemoryBase != Register.None ||
            body[11].MemoryIndex != Register.RBX ||
            body[11].MemoryIndexScale != 8 ||
            body[11].MemoryDisplacement64 != 0x20 ||
            !SplitElement(body[12], Code.Mov_r64_rm64,
                Register.RBP, Register.RDX) ||
            !(Reg(body[13], Code.Xor_r32_rm32,
                  Register.EDX, Register.EDX) ||
              Reg(body[13], Code.Xor_rm32_r32,
                  Register.EDX, Register.EDX)) ||
            !Reg(body[15], Code.Test_rm64_r64,
                Register.RDI, Register.RDI) ||
            !Branch(body[16], Mnemonic.Je, body[27].IP) ||
            !ArrayLengthCompare(body[17], Register.RDI) ||
            !Branch(body[18], Mnemonic.Jae, body[29].IP) ||
            !SplitElement(body[19], Code.Cmp_r64_rm64,
                Register.RBP, Register.RDI) ||
            !StackSlot(body[20], Code.Mov_r64_rm64,
                Register.RBX, 0x30, false) ||
            !StackSlot(body[21], Code.Mov_r64_rm64,
                Register.RBP, 0x38, false) ||
            body[22].Code != Code.Sete_rm8 ||
            body[22].Op0Kind != OpKind.Register ||
            body[22].Op0Register != Register.AL ||
            !StackSlot(body[23], Code.Mov_r64_rm64,
                Register.RSI, 0x40, false) ||
            !StackAdjust(body[24], Code.Add_rm64_imm8) ||
            body[25].Code != Code.Pop_r64 ||
            body[25].Op0Kind != OpKind.Register ||
            body[25].Op0Register != Register.RDI ||
            body[26].Code != Code.Retnq || body[26].OpCount != 0 ||
            body[28].Code != Code.Int3 ||
            body[30].Code != Code.Int3)
            return null;

        var facts = new NativeFacts(body);
        if (facts.LastWriter(5, Register.R9) >= 0 ||
            facts.LastWriter(6, Register.R8) >= 0 ||
            facts.LastWriter(7, Register.RDX) >= 0 ||
            facts.LastWriter(14, Register.RCX) >= 0 ||
            facts.LastWriter(19, Register.RBX) != 5 ||
            facts.LastWriter(19, Register.RDI) != 6 ||
            facts.LastWriter(19, Register.RSI) != 11 ||
            facts.LastWriter(19, Register.RBP) != 12 ||
            NativeGraph.Create(body, 27, 29) is not { } graph ||
            !graph.HasExactlyThesePredecessors(27, [8, 16]) ||
            !graph.HasExactlyThesePredecessors(29, [10, 18]) ||
            !graph.HasExactlyThesePredecessors(28, []) ||
            !graph.HasExactlyThesePredecessors(30, []) ||
            !graph.HasOnlyLinearPredecessors(8, 12) ||
            !graph.HasOnlyLinearPredecessors(16, 19) ||
            !graph.HasSingleGuardFlagConsumer(body, 7, 8) ||
            !graph.HasSingleGuardFlagConsumer(body, 9, 10) ||
            !graph.HasSingleGuardFlagConsumer(body, 15, 16) ||
            !graph.HasSingleGuardFlagConsumer(body, 17, 18) ||
            !graph.HasOnlyApprovedIndexUses(facts, 5, Register.RBX,
                [9, 11, 17]) ||
            !graph.HasOnlyApprovedIndexUses(facts, 11, Register.RSI,
                [12, 19]) ||
            !graph.HasUnchangedRegisterTo(facts, 6, 19,
                Register.RDI) ||
            !graph.HasUnchangedRegisterTo(facts, 12, 19,
                Register.RBP) ||
            !graph.HasSingleEqualityFlagConsumer(body, 19, 22))
            return null;

        return new NativeParameterEvidence(12, 19, 5, 11,
            6, 14, 27, 29, 22);
    }

    private static NativeSite? TrySite(NativeFacts facts, NativeGraph graph,
        int compare, int nullCall, int boundsCall)
    {
        var body = facts.Body;
        if (compare < 2 || compare + 1 >= boundsCall ||
            body[compare].Code != Code.Cmp_r32_rm32 ||
            body[compare].Op0Kind != OpKind.Register ||
            body[compare].Op0Register.GetSize() != 4 ||
            !Memory(body[compare], 1, body[compare].MemoryBase,
                Register.None, 1, 0x18, 4) ||
            body[compare - 2].Code != Code.Test_rm64_r64 ||
            body[compare - 2].Op0Kind != OpKind.Register ||
            body[compare - 2].Op1Kind != OpKind.Register ||
            body[compare - 2].Op0Register != body[compare - 2].Op1Register ||
            body[compare - 2].Op0Register.GetSize() != 8 ||
            body[compare - 2].Op0Register != body[compare].MemoryBase ||
            !Branch(body[compare - 1], Mnemonic.Je, body[nullCall].IP) ||
            !Branch(body[compare + 1], Mnemonic.Jae, body[boundsCall].IP))
            return null;

        var arrayRegister = body[compare].MemoryBase.GetFullRegister();
        var arrayGuard = compare - 1;
        var boundsGuard = compare + 1;
        var loadIndex = facts.LastWriter(compare - 2, arrayRegister);
        if (!graph.Dominates(arrayGuard, boundsGuard) ||
            loadIndex < 0 || loadIndex != compare - 3 ||
            !graph.Dominates(loadIndex, arrayGuard) ||
            body[loadIndex].Code != Code.Mov_r64_rm64 ||
            body[loadIndex].Op0Kind != OpKind.Register ||
            body[loadIndex].Op0Register != arrayRegister ||
            !Memory(body[loadIndex], 1, body[loadIndex].MemoryBase,
                Register.None, 1, body[loadIndex].MemoryDisplacement64, 8) ||
            body[loadIndex].MemoryDisplacement64 is < 0x10 or > int.MaxValue)
            return null;

        var receiver = body[loadIndex].MemoryBase.GetFullRegister();
        var receiverDefinition = facts.LastWriter(loadIndex, receiver);
        if (receiverDefinition >= 0 &&
            !graph.Dominates(receiverDefinition, loadIndex))
            return null;
        var ownerGuard = -1;
        for (var index = 1; index < loadIndex; index++)
        {
            if (body[index - 1].Code != Code.Test_rm64_r64 ||
                body[index - 1].Op0Kind != OpKind.Register ||
                body[index - 1].Op1Kind != OpKind.Register ||
                body[index - 1].Op0Register != receiver ||
                body[index - 1].Op1Register != receiver ||
                !Branch(body[index], Mnemonic.Je, body[nullCall].IP) ||
                !graph.Dominates(index, loadIndex) ||
                facts.LastWriter(index - 1, receiver) !=
                facts.LastWriter(loadIndex, receiver))
                continue;
            ownerGuard = index;
        }
        if (ownerGuard < 0 ||
            (ownerGuard > 1 &&
             !graph.HasOnlyLinearPredecessors(ownerGuard - 2, ownerGuard)) ||
            (ownerGuard == 1 &&
             !graph.HasOnlyLinearPredecessors(0, ownerGuard)))
            return null;

        var element = -1;
        var kind = ReadKind.Move;
        for (var index = boundsGuard + 1; index <= Math.Min(boundsGuard + 5, nullCall - 1); index++)
        {
            if (ReadElement(body[index], arrayRegister, out kind))
            {
                element = index;
                break;
            }
            if (!PureRegisterPreparation(body[index]) ||
                facts.WritesRegister(index, arrayRegister))
                return null;
        }
        if (element < 0 ||
            !graph.HasOnlyLinearPredecessors(loadIndex, element) ||
            !graph.Dominates(arrayGuard, element) ||
            !graph.Dominates(boundsGuard, element) ||
            facts.LastWriter(element, arrayRegister) != loadIndex ||
            !TryIndex(facts, graph, compare, element,
                body[compare].Op0Register, body[element].MemoryIndex,
                out var extension, out var sourceArgument))
            return null;

        return new NativeSite(loadIndex, ownerGuard, arrayGuard, boundsGuard,
            element, extension, body[element].MemoryIndex.GetFullRegister(),
            arrayRegister, sourceArgument, kind, Array.Empty<Effect>());
    }

    private static bool TryReferenceComparison(NativeFacts facts,
        NativeGraph graph, NativeSite left, NativeSite right,
        out NativeComparison? comparison)
    {
        comparison = null;
        var body = facts.Body;
        var leftRegister = body[left.ElementRead].Op0Register.GetFullRegister();
        var compare = body[right.ElementRead];
        if (leftRegister == Register.None ||
            compare.Op0Register.GetFullRegister() != leftRegister ||
            facts.LastWriter(right.ElementRead, leftRegister) != left.ElementRead ||
            !graph.HasUnchangedRegisterTo(facts, left.ElementRead,
                right.ElementRead, leftRegister))
            return false;

        var setEqual = -1;
        for (var index = right.ElementRead + 1; index < body.Count - 4; index++)
        {
            var instruction = body[index];
            if (instruction.Code == Code.Sete_rm8 &&
                instruction.Op0Kind == OpKind.Register &&
                instruction.Op0Register == Register.AL)
            {
                setEqual = index;
                break;
            }
            // Only saved nonvolatile registers may be restored before SETE.
            // These frame reads neither consume flags nor change managed state.
            if (instruction.Code != Code.Mov_r64_rm64 ||
                instruction.Op0Kind != OpKind.Register ||
                instruction.Op1Kind != OpKind.Memory ||
                instruction.MemoryBase != Register.RSP ||
                instruction.MemoryIndex != Register.None ||
                instruction.MemorySize.GetSize() != 8 ||
                instruction.Op0Register is not
                    (Register.RBX or Register.RBP or Register.RSI or Register.RDI or
                     Register.R12 or Register.R13 or Register.R14 or Register.R15))
                return false;
        }
        if (setEqual < 0 ||
            !graph.HasOnlyLinearPredecessors(right.ElementRead, setEqual) ||
            !graph.HasSingleEqualityFlagConsumer(body,
                right.ElementRead, setEqual))
            return false;

        comparison = new NativeComparison(left.ElementRead,
            right.ElementRead, setEqual, leftRegister);
        return true;
    }

    private static bool TryIndex(NativeFacts facts, NativeGraph graph,
        int compare, int element,
        Register comparedIndex, Register addressIndex,
        out int extensionIndex, out Register sourceArgument)
    {
        extensionIndex = -1;
        sourceArgument = Register.None;
        var fullAddress = addressIndex.GetFullRegister();
        extensionIndex = facts.LastWriter(element, fullAddress);
        if (extensionIndex < 0 ||
            facts.Body[extensionIndex] is not { Code: Code.Movsxd_r64_rm32 } extension ||
            extension.Op0Kind != OpKind.Register ||
            extension.Op0Register != fullAddress ||
            extension.Op1Kind != OpKind.Register ||
            extension.Op1Register.GetSize() != 4)
            return false;
        sourceArgument = extension.Op1Register.GetFullRegister();
        if (sourceArgument is not (Register.RDX or Register.R8 or Register.R9) ||
            sourceArgument == fullAddress ||
            facts.LastWriter(extensionIndex, sourceArgument) >= 0 ||
            !graph.Dominates(extensionIndex, element))
            return false;
        if (extensionIndex > compare)
            return graph.Dominates(compare, extensionIndex) &&
                   comparedIndex == extension.Op1Register &&
                   facts.LastWriter(extensionIndex, sourceArgument) ==
                   facts.LastWriter(compare, sourceArgument);
        return graph.Dominates(extensionIndex, compare) &&
               comparedIndex.GetFullRegister() == fullAddress &&
               comparedIndex.GetSize() == 4;
    }

    private static bool TryBindInt32Argument(MethodAnalysisContext method,
        Register register)
    {
        var argumentRegisters = new[] { Register.RCX, Register.RDX, Register.R8, Register.R9 };
        var slot = Array.IndexOf(argumentRegisters, register);
        var parameterIndex = slot - (method.IsStatic ? 0 : 1);
        if (parameterIndex < 0 || parameterIndex >= method.Parameters.Count)
            return false;
        var parameter = method.Parameters[parameterIndex];
        return parameter.Definition != null &&
               parameter.ParameterIndex == parameterIndex &&
               !parameter.IsRef && parameter.OverrideParameterType == null &&
               parameter.Attributes == parameter.DefaultAttributes &&
               ReferenceEquals(parameter.ParameterType,
                   method.AppContext.SystemTypes.SystemInt32Type) &&
               parameter.Definition.RawType is
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                       NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private sealed record Origin(TypeAnalysisContext Type,
        FieldAnalysisContext? Field, int Definition);

    private static Origin? TraceRegister(MethodAnalysisContext method,
        NativeFacts facts, NativeGraph graph, int before, Register register,
        int depth)
    {
        if (depth > 4)
            return null;
        var writer = facts.LastWriter(before, register);
        if (writer < 0)
            return register == Register.RCX && !method.IsStatic &&
                   method.DeclaringType != null
                ? new Origin(method.DeclaringType, null, -1) : null;
        var instruction = facts.Body[writer];
        if (!graph.Dominates(writer, before) ||
            instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != register)
            return null;
        if (instruction.Op1Kind == OpKind.Register)
            return TraceRegister(method, facts, graph, writer,
                instruction.Op1Register.GetFullRegister(), depth + 1);
        if (instruction.Op1Kind != OpKind.Memory ||
            instruction.MemoryIndex != Register.None ||
            instruction.MemoryBase is Register.None or Register.RIP ||
            instruction.MemoryDisplacement64 is < 0x10 or > int.MaxValue ||
            instruction.MemorySize.GetSize() != 8 ||
            TraceRegister(method, facts, graph, writer,
                instruction.MemoryBase.GetFullRegister(), depth + 1) is not
                { Type: var owner } || owner.IsValueType)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (int)instruction.MemoryDisplacement64).ToArray();
        if (fields is not [{ } field] || field.Name != field.DefaultName ||
            field.FieldType.IsValueType ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new ISIL.FieldReference(field,
                    new ISIL.LocalVariable("proved-field-owner",
                        new ISIL.Register(null, "proof"), owner), field.Offset)))
            return null;
        return new Origin(field.FieldType, field, writer);
    }

    internal static bool TryCompleteFileBackedRegion(MethodAnalysisContext method,
        IReadOnlyList<Instruction> decoded, PE pe, X64UnwindProof.Index unwind,
        out IReadOnlyList<Instruction> complete)
    {
        complete = Array.Empty<Instruction>();
        if (decoded.Count == 0 || decoded[0].IP != method.UnderlyingPointer)
            return false;
        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        var rawEnd = start + (ulong)method.RawBytes.Length;
        if (rawEnd <= start || method.RawBytes.Length > 8192 ||
            decoded[^1].NextIP != rawEnd ||
            !decoded.SequenceEqual(X86Utils.Iterate(
                method.RawBytes.AsSpan(), start, false)) ||
            SelectUnwindClosedPrefix(decoded, start, unwind.ClassifySpan) is
                not { } selected)
            return false;
        var end = selected[^1].NextIP;
        if (end <= start || end > rawEnd + 1 ||
            method.AppContext.MethodsByAddress.Keys.Any(address =>
                address > start && address < end))
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        var length = checked((int)(end - start));
        var inRawLength = Math.Min(length, method.RawBytes.Length);
        if (start < unwind.ImageBase ||
            start - unwind.ImageBase > uint.MaxValue - (uint)length + 1)
            return false;
        if (first < 0 || last - first != length - 1 ||
            first > image.Length - inRawLength || last >= image.Length ||
            !image.Slice((int)first, inRawLength)
                .SequenceEqual(method.RawBytes.AsSpan().Slice(0, inRawLength)) ||
            image[(int)last] != 0xCC)
            return false;
        var rva = checked((uint)(start - unwind.ImageBase));
        for (var offset = 0; offset < length; offset++)
            if (!unwind.IsExecutableRva(rva + (uint)offset) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) != first + offset)
                return false;
        complete = selected;
        return true;
    }

    // A metadata method's inferred raw extent may run into the next native
    // function. The unwind region and a closed pair of terminal helper arms
    // establish the exact prefix; no suffix byte becomes method evidence.
    internal static IReadOnlyList<Instruction>? SelectUnwindClosedPrefix(
        IReadOnlyList<Instruction> decoded, ulong entry,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify)
    {
        if (decoded.Count < 16 || decoded[0].IP != entry ||
            entry == ulong.MaxValue || decoded[^1].NextIP == ulong.MaxValue)
            return null;
        var root = classify(entry, entry + 1);
        if (root.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            root.Start != entry || root.RootStart != entry ||
            root.End <= entry || root.End > decoded[^1].NextIP + 1)
            return null;
        var matches = new List<IReadOnlyList<Instruction>>();
        for (var index = 3; index < decoded.Count; index++)
            if (IsClosedCandidate(decoded, index, entry, root.End, classify))
                matches.Add(decoded.Take(index + 1).ToArray());

        if (decoded[^1].Code == Code.Call_rel32_64 &&
            decoded[^1].NextIP != ulong.MaxValue)
        {
            var trap = Decoder.Create(64, new ByteArrayCodeReader([0xCC]),
                decoded[^1].NextIP).Decode();
            var extended = decoded.Append(trap).ToArray();
            if (IsClosedCandidate(extended, extended.Length - 1,
                    entry, root.End, classify))
                matches.Add(extended);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsClosedCandidate(IReadOnlyList<Instruction> decoded,
        int trapIndex, ulong entry, ulong rootEnd,
        Func<ulong, ulong, X64UnwindProof.SpanClassification> classify)
    {
        if (trapIndex < 15 || decoded[trapIndex].Code != Code.Int3 ||
            decoded[trapIndex - 1].Code != Code.Call_rel32_64 ||
            decoded[trapIndex - 2].Code != Code.Int3 ||
            decoded[trapIndex - 3].Code != Code.Call_rel32_64)
            return false;
        var end = decoded[trapIndex].NextIP;
        if (end != rootEnd)
            return false;
        var span = classify(entry, end);
        return span.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               span.Start == entry && span.RootStart == entry &&
               span.End == end;
    }

    private static bool DirectCall(Instruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0 &&
        instruction.CodeSize == CodeSize.Code64 &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == Register.None;

    private static bool Branch(Instruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(Instruction instruction, int operand,
        Register @base, Register index, int scale, ulong offset, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base &&
        instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == size &&
        instruction.SegmentPrefix == Register.None &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix;

    private static bool ReadElement(Instruction instruction,
        Register array, out ReadKind kind)
    {
        kind = instruction.Code == Code.Cmp_r64_rm64 ? ReadKind.Compare : ReadKind.Move;
        return (instruction.Code is Code.Mov_r64_rm64 or Code.Cmp_r64_rm64) &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register.GetSize() == 8 &&
               instruction.MemoryIndex.GetSize() == 8 &&
               Memory(instruction, 1, array, instruction.MemoryIndex, 8, 0x20, 8);
    }

    private static bool PureRegisterPreparation(Instruction instruction) =>
        instruction.Code == Code.Movsxd_r64_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register ||
        (instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64) &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register ||
        (instruction.Code is Code.Xor_r32_rm32 or Code.Xor_rm32_r32) &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == instruction.Op1Register;

    private sealed class NativeFacts(IReadOnlyList<Instruction> body)
    {
        private readonly InstructionInfoFactory _info = new();
        internal IReadOnlyList<Instruction> Body => body;

        internal int LastWriter(int before, Register register)
        {
            for (var index = before - 1; index >= 0; index--)
                if (WritesRegister(index, register))
                    return index;
            return -1;
        }

        internal bool WritesRegister(int index, Register register)
        {
            var instruction = body[index];
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                register is Register.RAX or Register.RCX or Register.RDX or Register.R8 or
                    Register.R9 or Register.R10 or Register.R11)
                return true;
            return _info.GetInfo(instruction).GetUsedRegisters().Any(used =>
                used.Register.GetFullRegister() == register && Writes(used.Access));
        }

        internal bool ReadsRegister(int index, Register register) =>
            _info.GetInfo(body[index]).GetUsedRegisters().Any(used =>
                used.Register.GetFullRegister() == register &&
                used.Access is OpAccess.Read or OpAccess.CondRead or
                    OpAccess.ReadWrite or OpAccess.ReadCondWrite);

        internal bool HasCallOrStore(int index)
        {
            var instruction = body[index];
            return instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall ||
                   _info.GetInfo(instruction).GetUsedMemory().Any(used => Writes(used.Access));
        }

        internal bool HasCallOrNonStackStore(int index)
        {
            var instruction = body[index];
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                return true;
            return _info.GetInfo(instruction).GetUsedMemory().Any(used =>
                Writes(used.Access) &&
                (used.Base.GetFullRegister() != Register.RSP ||
                 used.Index != Register.None));
        }

        internal bool TryEffect(int index, out Effect effect)
        {
            var instruction = body[index];
            effect = null!;
            if (DirectCall(instruction))
            {
                effect = new Effect(instruction.IP, EffectKind.DirectCall,
                    instruction.Code, instruction.NearBranchTarget,
                    Register.None, Register.None, 0, 0, 0);
                return true;
            }
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall ||
                instruction.Op0Kind != OpKind.Memory &&
                instruction.Op1Kind != OpKind.Memory ||
                instruction.MemoryBase is Register.None or Register.RIP ||
                instruction.MemoryIndex != Register.None ||
                instruction.SegmentPrefix != Register.None ||
                instruction.MemorySize.GetSize() is not (1 or 2 or 4 or 8) ||
                _info.GetInfo(instruction).GetUsedMemory()
                    .Count(used => Writes(used.Access)) != 1)
                return false;
            effect = new Effect(instruction.IP, EffectKind.MemoryStore,
                instruction.Code, 0, instruction.MemoryBase.GetFullRegister(),
                instruction.MemoryIndex, instruction.MemoryIndexScale,
                instruction.MemoryDisplacement64,
                instruction.MemorySize.GetSize());
            return true;
        }

        private static bool Writes(OpAccess access) =>
            access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or
                OpAccess.ReadCondWrite;
    }

    private sealed class NativeGraph
    {
        private readonly HashSet<int>[] _dominators;
        private readonly List<int>[] _predecessors;
        private readonly List<int>[] _successors;

        private NativeGraph(HashSet<int>[] dominators,
            List<int>[] predecessors, List<int>[] successors)
        {
            _dominators = dominators;
            _predecessors = predecessors;
            _successors = successors;
        }

        internal bool Dominates(int guard, int site) =>
            site >= 0 && site < _dominators.Length &&
            _dominators[site].Contains(guard);

        internal bool HasExactlyThesePredecessors(int site,
            HashSet<int> expected) =>
            site >= 0 && site < _predecessors.Length &&
            _predecessors[site].Count == expected.Count &&
            _predecessors[site].All(expected.Contains);

        internal bool HasOnlyLinearPredecessors(int first, int last)
        {
            if (first < 0 || last >= _predecessors.Length || first > last)
                return false;
            for (var site = first + 1; site <= last; site++)
                if (_predecessors[site].Count != 1 ||
                    _predecessors[site][0] != site - 1)
                    return false;
            return true;
        }

        // A suppressed TEST/CMP may feed only its adjacent guard branch. Walk
        // both branch arms until each produced flag is overwritten or the path
        // exits. A returning call is not an overwrite proof: a later flag read
        // would still need to be explained explicitly.
        internal bool HasSingleGuardFlagConsumer(IReadOnlyList<Instruction> body,
            int producer, int guard)
        {
            if (producer < 0 || guard != producer + 1 ||
                guard >= body.Count || !Dominates(producer, guard) ||
                _successors[guard].Count != 2 ||
                body[guard].FlowControl != FlowControl.ConditionalBranch)
                return false;

            var liveFlags = body[producer].RflagsModified;
            var guardReads = body[guard].RflagsRead;
            if (liveFlags == RflagsBits.None || guardReads == RflagsBits.None ||
                (guardReads & liveFlags) != guardReads ||
                body[guard].RflagsModified != RflagsBits.None)
                return false;

            var pending = new Stack<(int Index, RflagsBits Live)>();
            foreach (var successor in _successors[guard])
                pending.Push((successor, liveFlags));
            var seen = new HashSet<(int, RflagsBits)>();
            while (pending.Count != 0)
            {
                var (index, live) = pending.Pop();
                if (!seen.Add((index, live)))
                    continue;
                var instruction = body[index];
                if ((instruction.RflagsRead & live) != RflagsBits.None)
                    return false;
                var remaining = live & ~instruction.RflagsModified;
                if (remaining == RflagsBits.None)
                    continue;
                foreach (var successor in _successors[index])
                    pending.Push((successor, remaining));
            }
            return true;
        }

        // A native extension can be lowered as an Int32 index only when no
        // other observable operation reads its high bits. Follow every edge
        // until the register is overwritten, including helper exits.
        internal bool HasOnlyApprovedIndexUses(NativeFacts facts,
            int definition, Register register, HashSet<int> approved)
        {
            var pending = new Stack<int>(_successors[definition]);
            var seen = new HashSet<int>();
            while (pending.Count != 0)
            {
                var index = pending.Pop();
                if (!seen.Add(index))
                    continue;
                if (facts.ReadsRegister(index, register) && !approved.Contains(index))
                    return false;
                if (facts.WritesRegister(index, register))
                    continue;
                foreach (var successor in _successors[index])
                    pending.Push(successor);
            }
            return approved.All(index => seen.Contains(index));
        }

        internal bool HasUnchangedRegisterTo(NativeFacts facts,
            int definition, int use, Register register)
        {
            if (!Dominates(definition, use))
                return false;
            var pending = new Stack<int>(_successors[definition]);
            var seen = new HashSet<int>();
            var reachedUse = false;
            while (pending.Count != 0)
            {
                var index = pending.Pop();
                if (!seen.Add(index))
                    continue;
                if (index == use)
                {
                    reachedUse = true;
                    continue;
                }
                if (facts.WritesRegister(index, register))
                    return false;
                foreach (var successor in _successors[index])
                    pending.Push(successor);
            }
            return reachedUse;
        }

        internal bool HasSingleEqualityFlagConsumer(
            IReadOnlyList<Instruction> body, int compare, int setEqual)
        {
            var liveFlags = body[compare].RflagsModified;
            if ((liveFlags & RflagsBits.ZF) == 0 ||
                body[setEqual].RflagsRead != RflagsBits.ZF ||
                body[setEqual].RflagsModified != RflagsBits.None)
                return false;

            var pending = new Stack<(int Index, RflagsBits Live, bool SawSete)>();
            foreach (var successor in _successors[compare])
                pending.Push((successor, liveFlags, false));
            var seen = new HashSet<(int, RflagsBits, bool)>();
            var sawTerminal = false;
            while (pending.Count != 0)
            {
                var state = pending.Pop();
                if (!seen.Add(state))
                    continue;
                var instruction = body[state.Index];
                var consumed = instruction.RflagsRead & state.Live;
                if (consumed != RflagsBits.None &&
                    (state.Index != setEqual || state.SawSete ||
                     consumed != RflagsBits.ZF))
                    return false;
                var sawSete = state.SawSete || state.Index == setEqual;
                if (state.Index == setEqual && state.SawSete)
                    return false;
                if (!sawSete &&
                    (instruction.RflagsModified != RflagsBits.None ||
                     instruction.FlowControl is FlowControl.Call or
                         FlowControl.IndirectCall))
                    return false;
                var remaining = state.Live & ~instruction.RflagsModified;
                if (instruction.FlowControl is FlowControl.Call or
                    FlowControl.IndirectCall)
                    remaining = RflagsBits.None;
                if (remaining == RflagsBits.None ||
                    _successors[state.Index].Count == 0)
                {
                    if (!sawSete)
                        return false;
                    sawTerminal = true;
                    continue;
                }
                foreach (var successor in _successors[state.Index])
                    pending.Push((successor, remaining, sawSete));
            }
            return sawTerminal;
        }

        internal static NativeGraph? Create(IReadOnlyList<Instruction> body,
            int nullCall, int boundsCall)
        {
            var addresses = body.Select((instruction, index) => (instruction.IP, index))
                .ToDictionary(item => item.IP, item => item.index);
            var successors = new List<int>[body.Count];
            var predecessors = Enumerable.Range(0, body.Count)
                .Select(_ => new List<int>()).ToArray();
            for (var index = 0; index < body.Count; index++)
            {
                var instruction = body[index];
                var next = new List<int>();
                if (index != nullCall && index != boundsCall)
                    switch (instruction.FlowControl)
                    {
                        case FlowControl.Return:
                        case FlowControl.Interrupt:
                            break;
                        case FlowControl.UnconditionalBranch:
                        case FlowControl.ConditionalBranch:
                            if (instruction.Op0Kind != OpKind.NearBranch64 ||
                                !addresses.TryGetValue(instruction.NearBranchTarget,
                                    out var target))
                                return null;
                            next.Add(target);
                            if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                                break;
                            goto case FlowControl.Next;
                        case FlowControl.Next:
                        case FlowControl.Call:
                            if (index + 1 == body.Count)
                                return null;
                            next.Add(index + 1);
                            break;
                        default:
                            return null;
                    }
                successors[index] = next;
                foreach (var target in next)
                    predecessors[target].Add(index);
            }
            var reachable = new HashSet<int> { 0 };
            var pending = new Stack<int>();
            pending.Push(0);
            while (pending.Count != 0)
            {
                var index = pending.Pop();
                foreach (var target in successors[index])
                    if (reachable.Add(target))
                        pending.Push(target);
            }

            var dominators = Enumerable.Range(0, body.Count)
                .Select(index => index == 0 ? new HashSet<int> { 0 } :
                    reachable.Contains(index) ? new HashSet<int>(reachable) :
                    new HashSet<int>()).ToArray();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var index in reachable.Where(index => index != 0))
                {
                    var inputs = predecessors[index].Where(reachable.Contains).ToArray();
                    if (inputs.Length == 0)
                        return null;
                    var next = new HashSet<int>(dominators[inputs[0]]);
                    foreach (var predecessor in inputs.Skip(1))
                        next.IntersectWith(dominators[predecessor]);
                    next.Add(index);
                    if (next.SetEquals(dominators[index]))
                        continue;
                    dominators[index] = next;
                    changed = true;
                }
            }
            return new NativeGraph(dominators, predecessors, successors);
        }
    }
}
