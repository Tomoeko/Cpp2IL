using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds ordinary array operations on a single native success path. Only their
/// own adjacent null and unsigned bounds checks are replaced. The typed graph
/// must independently retain the operations, their captured origins, and every
/// intervening memory effect and call before IL can be emitted.
/// </summary>
internal static partial class X64GuardedArrayOperationProof
{
    internal const string EvidenceKey = "X64GuardedArrayOperationProof.Evidence";
    private static readonly NativeRegister[] Arguments =
        [NativeRegister.RCX, NativeRegister.RDX, NativeRegister.R8, NativeRegister.R9];

    internal sealed record CallReturn(ulong Ip, MethodAnalysisContext Target,
        NativeRegister? ReceiverEntry);

    internal sealed record CheckedCall(ulong Ip, ValueOrigin Receiver);

    internal sealed record Origin(NativeRegister EntryRegister, ulong? FieldReadIp,
        FieldAnalysisContext? Field, ParameterAnalysisContext? Parameter, CallReturn? Call = null);

    internal sealed record IndexExtension(ulong Ip, NativeRegister Destination,
        NativeRegister Source);

    internal sealed record ValueOrigin(NativeRegister EntryRegister, ulong? DefinitionIp);
    internal sealed record StoreValue(long? Literal, ValueOrigin? Origin);

    internal sealed record Site(ulong OperationIp, ulong? NullTestIp,
        ulong? NullBranchIp, ulong BoundsCompareIp, ulong BoundsBranchIp,
        IndexExtension Index, ValueOrigin IndexOrigin, Origin ArrayOrigin, TypeAnalysisContext ElementType,
        bool IsStore, int Width, StoreValue? StoredValue, NativeRegister ArrayRegister,
        NativeRegister IndexRegister, ulong? OffsetPreparationIp);

    internal sealed record Evidence(IReadOnlyList<NativeInstruction> Body,
        IReadOnlyList<Site> Sites, HashSet<ulong> RemovedAddresses,
        IReadOnlyList<IndexExtension> IndexExtensions,
        HashSet<ulong> NoReturnCallAddresses, IReadOnlyList<ulong> EffectAddresses,
        IReadOnlyList<CheckedCall> NullCheckedCalls, IReadOnlyList<InvocationArgument> InvocationArguments);

    internal sealed record NativeSite(int Operation, int NullTest, int BoundsCompare,
        int Extension, NativeRegister ArrayRegister, NativeRegister IndexRegister,
        NativeRegister IndexSource, bool IsStore, int Width, int? OffsetPreparation);

    internal sealed record NativeEvidence(int SuccessEnd, int NullCall, int BoundsCall,
        IReadOnlyList<NativeSite> Sites, IReadOnlyList<int> Effects);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            if (decoded.Count is < 12 or > 512 ||
                !decoded.Any(instruction => instruction.Mnemonic == Mnemonic.Jae) ||
                !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) || !OrdinaryMethod(method) ||
                X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveNative(body,
                    target => X86RuntimeNullThrowProof.TryIdentify(method.AppContext, target) != null,
                    target => X86RuntimeBoundsThrowProof.TryIdentify(method.AppContext, target)) is not { } native)
                return null;

            var facts = new Facts(body);
            foreach (var effect in native.Effects.Where(index => IsInvocation(body[index])))
            {
                var call = body[effect];
                if (call.Code is not (Code.Call_rel32_64 or Code.Jmp_rel32_64 or Code.Jmp_rel8_64) ||
                    !method.AppContext.MethodsByAddress.TryGetValue(call.NearBranchTarget, out var targets) ||
                    targets is not [{ } target] || target.UnderlyingPointer != call.NearBranchTarget ||
                    !ReferenceEquals(target.AppContext, method.AppContext) || !HasEligibleEffectCall(target) ||
                    !X64GuardedEnumParameterCallProof.AccessibleTarget(method.DeclaringType!, target) ||
                    !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                        method.DeclaringType!.DeclaringAssembly, target.DeclaringType!.DeclaringAssembly))
                    return null;
            }
            var sites = new List<Site>();
            var removed = new HashSet<ulong>
            {
                body[native.BoundsCall].IP
            };
            foreach (var instruction in body.Skip(native.BoundsCall + 1))
                removed.Add(instruction.IP);
            foreach (var site in native.Sites)
            {
                if (BindArrayOrigin(method, facts, site.Operation, site.ArrayRegister) is not { } origin ||
                    ArrayType(origin) is not { } array ||
                    !AllowedElement(method.AppContext, array.ElementType, site.Width, site.IsStore))
                    return null;
                var extension = new IndexExtension(body[site.Extension].IP,
                    site.IndexRegister, site.IndexSource);
                var indexWriter = facts.TraceScalarCopies(site.Extension, site.IndexSource,
                    out var indexEntry);
                var indexOrigin = new ValueOrigin(indexEntry,
                    indexWriter < 0 ? null : body[indexWriter].IP);
                StoreValue? stored = null;
                if (site.IsStore)
                {
                    var operation = body[site.Operation];
                    if (operation.Code == Code.Mov_rm32_imm32)
                        stored = new StoreValue(unchecked((int)operation.Immediate32), null);
                    else
                    {
                        var writer = facts.TraceScalarCopies(site.Operation,
                            operation.Op1Register.GetFullRegister(), out var valueEntry);
                        stored = writer >= 0 && body[writer].Code == Code.Mov_r32_imm32
                            ? new StoreValue(unchecked((int)body[writer].Immediate32), null)
                            : new StoreValue(null, new ValueOrigin(valueEntry,
                                writer < 0 ? null : body[writer].IP));
                    }
                }
                sites.Add(new Site(body[site.Operation].IP,
                    site.NullTest < 0 ? null : body[site.NullTest].IP,
                    site.NullTest < 0 ? null : body[site.NullTest + 1].IP, body[site.BoundsCompare].IP,
                    body[site.BoundsCompare + 1].IP, extension, indexOrigin, origin,
                    array.ElementType, site.IsStore, site.Width, stored, site.ArrayRegister,
                    site.IndexRegister, site.OffsetPreparation is { } offset ? body[offset].IP : null));
                removed.UnionWith([body[site.BoundsCompare].IP, body[site.BoundsCompare + 1].IP]);
                if (site.NullTest >= 0)
                    removed.UnionWith([body[site.NullTest].IP, body[site.NullTest + 1].IP]);
                if (site.OffsetPreparation is { } preparation)
                    removed.Add(body[preparation].IP);
            }

            // The null arm may still serve a field or a call receiver. Its native
            // helper remains until the ordinary null-guard pass proves those uses.
            if (!body.Take(native.SuccessEnd).Any(instruction =>
                    instruction.FlowControl == FlowControl.ConditionalBranch &&
                    instruction.NearBranchTarget == body[native.NullCall].IP &&
                    !removed.Contains(instruction.IP)))
                removed.UnionWith([body[native.NullCall].IP, body[native.NullCall + 1].IP]);
            var checkedCalls = new List<CheckedCall>();
            foreach (var branch in Enumerable.Range(1, native.SuccessEnd - 1).Where(index =>
                         body[index].FlowControl == FlowControl.ConditionalBranch &&
                         body[index].NearBranchTarget == body[native.NullCall].IP &&
                         !removed.Contains(body[index].IP)))
            {
                var effect = native.Effects.Where(index => index > branch).DefaultIfEmpty(-1).First();
                if (effect < 0)
                    return null;
                if (!IsInvocation(body[effect]))
                    continue; // The existing field-null provenance validator retains field probes.
                var target = method.AppContext.MethodsByAddress[body[effect].NearBranchTarget].Single();
                var guardedWriter = facts.TraceCopies(branch - 1,
                    body[branch - 1].Op0Register.GetFullRegister(), out var guardedEntry);
                var receiverWriter = facts.TraceCopies(effect, NativeRegister.RCX, out var receiverEntry);
                if (target.IsStatic || guardedWriter != receiverWriter || guardedEntry != receiverEntry)
                    return null;
                checkedCalls.Add(new CheckedCall(body[effect].IP,
                    new ValueOrigin(receiverEntry, receiverWriter < 0 ? null : body[receiverWriter].IP)));
            }
            var noReturn = new HashSet<ulong>
                { body[native.NullCall].IP, body[native.BoundsCall].IP };
            if (X86CallerExceptionRegionProof.Check(method, body, noReturn) != null ||
                IsTailInvocation(body[native.SuccessEnd]) &&
                (X64NativeInvocationValues.Create(body, noReturn) is not { } tailValues ||
                 !X64NativeInvocationFrameProof.IsValid(method, body, tailValues)) ||
                !TryInvocationArguments(method, facts, native, sites, noReturn, out var arguments))
                return null;
            return new Evidence(body, sites, removed,
                sites.Select(site => site.Index).Distinct().ToArray(), noReturn,
                native.Effects.Select(index => body[index].IP).ToArray(), checkedCalls.Distinct().ToArray(), arguments);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method) =>
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) &&
        HasOrdinaryArguments(method) &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer].Count(candidate =>
            ReferenceEquals(candidate, method)) == 1 &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer].All(candidate =>
            ReferenceEquals(candidate.AppContext, method.AppContext) &&
            candidate.UnderlyingPointer == method.UnderlyingPointer && candidate.Definition != null) &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes && method.OverrideReturnType == null &&
        method.GenericParameters.Count == 0 &&
        method.Name is not (".ctor" or ".cctor") &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        method.Parameters.Select((parameter, index) => (parameter, index)).All(pair =>
            pair.parameter.ParameterIndex == pair.index &&
            ReferenceEquals(pair.parameter.DeclaringMethod, method) &&
            pair.parameter.Definition != null && !pair.parameter.IsRef &&
            !pair.parameter.UseOverrideDefaultValue &&
            pair.parameter.OverrideParameterType == null && pair.parameter.Name == pair.parameter.DefaultName &&
            pair.parameter.Attributes == pair.parameter.DefaultAttributes &&
            NullCheckedCall.SameOrdinaryType(pair.parameter.ParameterType, pair.parameter.DefaultParameterType));

    private static bool HasOrdinaryArguments(MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not
                X64CallingConventionResolver resolver || resolver.ReturnsViaHiddenBuffer(method) ||
            !OrdinaryAbiValue(method.Definition?.RawReturnType, method.ReturnType, allowVoid: true) ||
            method.Parameters.Any(parameter =>
                !OrdinaryAbiValue(parameter.Definition?.RawType, parameter.ParameterType, allowVoid: false)))
            return false;
        var argumentCount = method.Parameters.Count + (method.IsStatic ? 0 : 1);
        var operands = resolver.ResolveForParameters(method);
        return operands.Length == argumentCount + 1 &&
               operands.Take(Math.Min(4, argumentCount)).Select((operand, index) =>
                   operand is ISIL.Register register &&
                   register.Name == X86Utils.GetRegisterName(Arguments[index])).All(valid => valid);
    }

    private static bool OrdinaryAbiValue(Il2CppType? raw, TypeAnalysisContext type, bool allowVoid) =>
        raw is { NumMods: 0, Byref: 0, Pinned: 0 } &&
        (allowVoid && raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VOID ||
         raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
             Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
             Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
             Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
             Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 ||
         raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
             Il2CppTypeEnum.IL2CPP_TYPE_STRING && OrdinaryClass(type) ||
         raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY && OrdinaryArray(raw, type) &&
         type is SzArrayTypeAnalysisContext array &&
         OrdinaryAbiValue(raw.GetEncapsulatedType(), array.ElementType, allowVoid: false));

    internal static bool HasEligibleEffectCall(MethodAnalysisContext target) =>
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) &&
        NullCheckedCall.HasEligibleImplementation(target) &&
        (target.ImplAttributes & MethodImplAttributes.Synchronized) == 0 &&
        target.Name is not (".ctor" or ".cctor") && !target.IsVirtual &&
        target.Name == target.DefaultName && target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes && target.OverrideReturnType == null &&
        target.GenericParameters.Count == 0 &&
        NullCheckedCall.SameOrdinaryType(target.ReturnType, target.DefaultReturnType) &&
        (target.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        target.DeclaringType is { } owner && OrdinaryClass(owner) && HasOrdinaryArguments(target) &&
        target.Parameters.Select((parameter, index) => (parameter, index)).All(pair =>
            pair.parameter.ParameterIndex == pair.index &&
            ReferenceEquals(pair.parameter.DeclaringMethod, target) &&
            !pair.parameter.IsRef && !pair.parameter.UseOverrideDefaultValue &&
            pair.parameter.Name == pair.parameter.DefaultName &&
            pair.parameter.Attributes == pair.parameter.DefaultAttributes &&
            pair.parameter.OverrideParameterType == null &&
            NullCheckedCall.SameOrdinaryType(pair.parameter.ParameterType, pair.parameter.DefaultParameterType));

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) && type.Definition is
            { PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { NumMods: 0, Byref: 0, Pinned: 0 } } &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes && ReferenceEquals(type.BaseType, type.DefaultBaseType);

    internal static SzArrayTypeAnalysisContext? ArrayType(Origin origin) =>
        (origin.Field?.FieldType ?? origin.Parameter?.ParameterType ?? origin.Call?.Target.ReturnType)
        as SzArrayTypeAnalysisContext;

    private static bool AllowedElement(ApplicationAnalysisContext app, TypeAnalysisContext element,
        int width, bool store) => width == 4
        ? ReferenceEquals(element, app.SystemTypes.SystemInt32Type) ||
          ReferenceEquals(element, app.SystemTypes.SystemUInt32Type)
        : width == 8 && !store && OrdinaryClass(element);

    private static Origin? BindArrayOrigin(MethodAnalysisContext method, Facts facts,
        int before, NativeRegister arrayRegister)
    {
        var writer = facts.TraceCopies(before, arrayRegister, out var entry);
        if (writer < 0)
        {
            if (EntryParameter(method, entry) is not { } parameter ||
                !OrdinaryArray(parameter.Definition?.RawType, parameter.ParameterType))
                return null;
            return new Origin(entry, null, null, parameter);
        }
        var load = facts.Body[writer];
        // Calls clobber every volatile register, but only RAX is their ordinary
        // reference result. Copies into preserved registers retain this exact
        // call-site identity across subsequent effects and producer invocations.
        if (load.Code == Code.Call_rel32_64 && entry == NativeRegister.RAX)
        {
            if (!method.AppContext.MethodsByAddress.TryGetValue(load.NearBranchTarget, out var targets) ||
                targets is not [{ } target] || target.UnderlyingPointer != load.NearBranchTarget ||
                !ReferenceEquals(target.AppContext, method.AppContext) || !HasEligibleEffectCall(target) ||
                !OrdinaryArray(target.Definition?.RawReturnType, target.ReturnType))
                return null;
            NativeRegister? receiverEntry = null;
            if (!target.IsStatic)
            {
                // Initially bind producer receivers only to original incoming
                // class values. A nested field or another call result needs its
                // own complete receiver-origin proof before this route expands.
                if (facts.TraceCopies(writer, NativeRegister.RCX, out var receiver) >= 0 ||
                    !ReferenceEquals(EntryType(method, receiver), target.DeclaringType))
                    return null;
                receiverEntry = receiver;
            }
            return new Origin(NativeRegister.None, null, null, null,
                new CallReturn(load.IP, target, receiverEntry));
        }
        if (load.Code != Code.Mov_r64_rm64 || load.Op1Kind != OpKind.Memory ||
            load.MemoryIndex != NativeRegister.None || load.MemoryDisplacement64 > int.MaxValue ||
            facts.TraceCopies(writer, load.MemoryBase, out var ownerEntry) >= 0 ||
            EntryType(method, ownerEntry) is not { } owner || !OrdinaryClass(owner))
            return null;
        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (int)load.MemoryDisplacement64).Take(2).ToArray();
        if (fields is not [{ } field] || field.Name != field.DefaultName ||
            !ReferenceEquals(field.DeclaringType, owner) ||
            !ReferenceEquals(field.BackingData?.Field.DeclaringType, owner.Definition) ||
            field.OverrideFieldType != null || !AccessibleField(field, method.DeclaringType!) ||
            !OrdinaryArray(field.BackingData?.Field.RawFieldType, field.FieldType))
            return null;
        var local = new LocalVariable("array-origin", new ISIL.Register(null, "origin"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(field, local, field.Offset)))
            return null;
        return new Origin(ownerEntry, load.IP, field, null);
    }

    private static bool AccessibleField(FieldAnalysisContext field, TypeAnalysisContext caller)
    {
        if (ReferenceEquals(field.DeclaringType, caller))
            return true;
        var owner = field.DeclaringType;
        var sameAssembly = ReferenceEquals(owner.DeclaringAssembly, caller.DeclaringAssembly);
        return owner.DeclaringType == null &&
               ((owner.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public ||
                sameAssembly && (owner.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.NotPublic) &&
               (field.Visibility == FieldAttributes.Public || sameAssembly && field.Visibility == FieldAttributes.Assembly);
    }

    private static bool OrdinaryArray(Il2CppType? raw, TypeAnalysisContext type) =>
        raw is { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, NumMods: 0, Byref: 0, Pinned: 0 } &&
        raw.GetEncapsulatedType() is { NumMods: 0, Byref: 0, Pinned: 0 } element &&
        type is SzArrayTypeAnalysisContext array &&
        ReferenceEquals(array.ElementType, array.AppContext.ResolveIl2CppType(element));

    internal static ParameterAnalysisContext? EntryParameter(MethodAnalysisContext method,
        NativeRegister register)
    {
        var slot = Array.IndexOf(Arguments, register) - (method.IsStatic ? 0 : 1);
        return slot >= 0 && slot < method.Parameters.Count ? method.Parameters[slot] : null;
    }

    internal static TypeAnalysisContext? EntryType(MethodAnalysisContext method,
        NativeRegister register) => !method.IsStatic && register == NativeRegister.RCX
        ? method.DeclaringType : EntryParameter(method, register)?.ParameterType;

    internal static NativeEvidence? TryProveNative(IReadOnlyList<NativeInstruction> body,
        Func<ulong, bool> nullHelper, Func<ulong, bool> boundsHelper)
    {
        if (body.Count is < 12 or > 512 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None))
            return null;
        for (var index = 1; index < body.Count; index++)
            if (body[index - 1].NextIP != body[index].IP)
                return null;
        var successEnd = -1;
        for (var index = 0; index < body.Count; index++)
            if (body[index].FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch)
            {
                successEnd = index;
                break;
            }
        if (successEnd < 0 ||
            !(body[successEnd].Code == Code.Retnq || IsTailInvocation(body[successEnd]) &&
                (body[successEnd].NearBranchTarget < body[0].IP ||
                 body[successEnd].NearBranchTarget >= body[^1].NextIP)) ||
            body.Count < successEnd + 4 || body.Count > successEnd + 8 ||
            body[successEnd + 1].Code != Code.Call_rel32_64 ||
            body[successEnd + 2].Code != Code.Int3 ||
            body[successEnd + 3].Code != Code.Call_rel32_64 ||
            body.Skip(successEnd + 4).Any(instruction => instruction.Code != Code.Int3) ||
            !nullHelper(body[successEnd + 1].NearBranchTarget) ||
            !boundsHelper(body[successEnd + 3].NearBranchTarget))
            return null;
        var nullCall = successEnd + 1;
        var boundsCall = successEnd + 3;
        var facts = new Facts(body);
        var sites = new List<NativeSite>();
        for (var branch = 0; branch < successEnd; branch++)
        {
            var instruction = body[branch];
            if (instruction.FlowControl is FlowControl.IndirectBranch or FlowControl.Exception or
                FlowControl.Interrupt or FlowControl.Return or FlowControl.UnconditionalBranch)
                return null;
            if (instruction.FlowControl != FlowControl.ConditionalBranch)
                continue;
            if (instruction.Op0Kind != OpKind.NearBranch64)
                return null;
            if (instruction.NearBranchTarget == body[nullCall].IP)
            {
                if (branch == 0 || !NullTest(body[branch - 1]) || instruction.Mnemonic != Mnemonic.Je ||
                    !SoleFlagConsumer(body, branch - 1, branch, successEnd))
                    return null;
                continue;
            }
            if (instruction.NearBranchTarget != body[boundsCall].IP ||
                instruction.Mnemonic != Mnemonic.Jae || branch < 3 ||
                body[branch - 1].Code != Code.Cmp_r32_rm32 ||
                body[branch - 1].Op0Kind != OpKind.Register ||
                body[branch - 1].Op0Register.GetSize() != 4 ||
                !Memory(body[branch - 1], 1, 0x18, 4, indexed: false) ||
                !SoleFlagConsumer(body, branch - 1, branch, successEnd))
                return null;
            var compare = branch - 1;
            var array = body[compare].MemoryBase;
            var nullTest = compare - 2;
            if (nullTest < 0 || !NullTest(body[nullTest]) ||
                body[nullTest].Op0Register.GetFullRegister() != array ||
                body[nullTest + 1].Mnemonic != Mnemonic.Je ||
                body[nullTest + 1].NearBranchTarget != body[nullCall].IP)
            {
                // A preceding successful access proves this captured array
                // nonnull. No call or register write may replace it meanwhile.
                if (!sites.Any(site => site.ArrayRegister == array &&
                        facts.Unchanged(site.Operation - 1, compare, array)))
                    return null;
                nullTest = -1;
            }
            var operation = branch + 1;
            while (operation < successEnd && operation < branch + 5 &&
                   (PurePreparation(body[operation]) || OffsetPreparation(body[operation])))
                operation++;
            if (operation >= successEnd || !ArrayOperation(facts, operation, array,
                    out var store, out var width, out var indexRegister, out var offsetPreparation))
                return null;
            var extension = facts.LastWriter(offsetPreparation ?? operation, indexRegister);
            if (extension < 0 || !IndexExtensionInstruction(body[extension], indexRegister,
                    out var indexSource) ||
                !facts.Unchanged(nullTest < 0 ? compare : nullTest, operation, array) ||
                !(body[compare].Op0Register.GetFullRegister() == indexSource &&
                  facts.Unchanged(Math.Min(extension, compare), Math.Max(extension, compare), indexSource) ||
                  extension < compare && body[compare].Op0Register.GetFullRegister() == indexRegister &&
                  facts.Unchanged(extension, compare, indexRegister)))
                return null;
            sites.Add(new NativeSite(operation, nullTest, compare, extension,
                array, indexRegister, indexSource, store, width, offsetPreparation));
            if (sites.Count > 16)
                return null;
        }
        if (sites.Count == 0 || sites.Select(site => site.Operation).Distinct().Count() != sites.Count)
            return null;
        // Recheck shared extended indices with the complete site set. A 64-bit
        // use outside these exact element addresses would lose sign extension.
        foreach (var site in sites)
            if (!OnlyIndexUses(facts, site.Extension, successEnd,
                    site.IndexRegister, body[site.Operation].IP, sites) ||
                site.OffsetPreparation is { } preparation &&
                !OnlyOffsetUses(facts, preparation, successEnd, sites))
                return null;
        var effects = Enumerable.Range(0, successEnd + 1).Where(index =>
            !sites.Any(site => site.BoundsCompare == index || site.NullTest == index) &&
            facts.IsEffect(index)).ToArray();
        return new NativeEvidence(successEnd, nullCall, boundsCall, sites, effects);
    }

    internal static bool IsTailInvocation(NativeInstruction instruction) =>
        instruction.Code is Code.Jmp_rel32_64 or Code.Jmp_rel8_64 &&
        instruction.Op0Kind == OpKind.NearBranch64;

    private static bool IsInvocation(NativeInstruction instruction) =>
        instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall ||
        IsTailInvocation(instruction);

    private static bool OnlyIndexUses(Facts facts, int extension, int end,
        NativeRegister register, ulong operationIp, IReadOnlyList<NativeSite> sites)
    {
        for (var index = extension + 1; index <= end; index++)
        {
            var instruction = facts.Body[index];
            foreach (var use in facts.Info(index).GetUsedRegisters())
                if (use.Register.GetFullRegister() == register && Reads(use.Access) &&
                    use.Register.GetSize() == 8 &&
                    !(instruction.MemoryIndex == register &&
                      (instruction.IP == operationIp || sites.Any(site => site.Operation == index) ||
                       sites.Any(site => site.OffsetPreparation == index))))
                    return false;
            if (facts.Writes(index, register))
                break;
        }
        return true;
    }

    private static bool IndexExtensionInstruction(NativeInstruction instruction,
        NativeRegister destination, out NativeRegister source)
    {
        source = instruction.Code == Code.Cdqe ? NativeRegister.RAX :
            instruction.Op1Register.GetFullRegister();
        return instruction.Code == Code.Cdqe && destination == NativeRegister.RAX ||
               instruction.Code == Code.Movsxd_r64_rm32 &&
               instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
               instruction.Op0Register == destination && instruction.Op1Register.GetSize() == 4;
    }

    private static bool ArrayOperation(Facts facts, int operation, NativeRegister array,
        out bool store, out int width, out NativeRegister index, out int? offsetPreparation)
    {
        var instruction = facts.Body[operation];
        store = instruction.Code is Code.Mov_rm32_r32 or Code.Mov_rm32_imm32;
        width = instruction.MemorySize.GetSize();
        index = instruction.MemoryIndex;
        offsetPreparation = null;
        if (!(instruction.Code is Code.Mov_r64_rm64 or Code.Mov_r32_rm32 || store) ||
            width is not (4 or 8) || store && width != 4 ||
            instruction.GetOpKind(store ? 0 : 1) != OpKind.Memory)
            return false;
        if (Memory(instruction, store ? 0 : 1, 0x20, width, indexed: true) &&
            instruction.MemoryBase == array && instruction.MemoryIndexScale == width)
            return true;
        if (instruction.MemoryDisplacement64 != 0 || instruction.MemoryIndexScale != 1 ||
            instruction.MemoryBase.GetSize() != 8 || instruction.MemoryIndex.GetSize() != 8)
            return false;
        var offset = instruction.MemoryBase == array ? instruction.MemoryIndex :
            instruction.MemoryIndex == array ? instruction.MemoryBase : NativeRegister.None;
        var writer = facts.LastWriter(operation, offset);
        if (writer < 0 || !OffsetPreparation(facts.Body[writer]) ||
            facts.Body[writer].MemoryIndexScale != width ||
            !facts.Unchanged(writer, operation, facts.Body[writer].MemoryIndex))
            return false;
        offsetPreparation = writer;
        index = facts.Body[writer].MemoryIndex;
        return true;
    }

    private static bool OffsetPreparation(NativeInstruction instruction) =>
        instruction.Code == Code.Lea_r64_m && instruction.Op0Kind == OpKind.Register &&
        instruction.MemoryBase == NativeRegister.None && instruction.MemoryIndex.GetSize() == 8 &&
        instruction.MemoryIndexScale is 4 or 8 && instruction.MemoryDisplacement64 == 0x20;

    private static bool OnlyOffsetUses(Facts facts, int preparation, int end,
        IReadOnlyList<NativeSite> sites)
    {
        var register = facts.Body[preparation].Op0Register;
        for (var index = preparation + 1; index <= end; index++)
        {
            foreach (var use in facts.Info(index).GetUsedRegisters())
                if (use.Register.GetFullRegister() == register && Reads(use.Access) &&
                    !(use.Register.GetSize() == 8 && sites.Any(site =>
                        site.Operation == index && site.OffsetPreparation == preparation)))
                    return false;
            if (facts.Writes(index, register))
                break;
        }
        return true;
    }

    private static bool Memory(NativeInstruction instruction, int operand,
        ulong displacement, int width, bool indexed) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase.GetSize() == 8 &&
        instruction.MemoryBase is not (NativeRegister.RSP or NativeRegister.RIP) &&
        instruction.MemoryDisplacement64 == displacement && instruction.MemorySize.GetSize() == width &&
        (indexed ? instruction.MemoryIndex.GetSize() == 8 : instruction.MemoryIndex == NativeRegister.None);

    private static bool NullTest(NativeInstruction instruction) =>
        instruction.Code == Code.Test_rm64_r64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == instruction.Op1Register && instruction.Op0Register.GetSize() == 8;

    private static bool PurePreparation(NativeInstruction instruction) =>
        instruction.Code == Code.Cdqe || instruction.Code == Code.Movsxd_r64_rm32 &&
        instruction.Op1Kind == OpKind.Register ||
        instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Xor &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind is OpKind.Register or OpKind.Immediate8 or OpKind.Immediate32 or
            OpKind.Immediate32to64 or OpKind.Immediate64;

    private static bool SoleFlagConsumer(IReadOnlyList<NativeInstruction> body,
        int producer, int branch, int successEnd)
    {
        var live = body[producer].RflagsModified;
        if (live == RflagsBits.None || body[branch].RflagsRead == RflagsBits.None ||
            (body[branch].RflagsRead & ~live) != 0)
            return false;
        for (var index = branch + 1; index <= successEnd && live != RflagsBits.None; index++)
        {
            if ((body[index].RflagsRead & live) != 0)
                return false;
            live &= ~body[index].RflagsModified;
        }
        return true;
    }

    private static bool Reads(OpAccess access) =>
        access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite;

    private sealed class Facts(IReadOnlyList<NativeInstruction> body)
    {
        private readonly InstructionInfoFactory _factory = new();
        internal IReadOnlyList<NativeInstruction> Body => body;
        internal InstructionInfo Info(int index) => _factory.GetInfo(body[index]);
        internal bool Writes(int index, NativeRegister register) =>
            body[index].FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
            register is NativeRegister.RAX or NativeRegister.RCX or NativeRegister.RDX or
                NativeRegister.R8 or NativeRegister.R9 or NativeRegister.R10 or NativeRegister.R11 ||
            Info(index).GetUsedRegisters().Any(use => use.Register.GetFullRegister() == register &&
                use.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite);

        internal int LastWriter(int before, NativeRegister register)
        {
            for (var index = before - 1; index >= 0; index--)
                if (Writes(index, register))
                    return index;
            return -1;
        }

        internal bool Unchanged(int from, int to, NativeRegister register) =>
            !Enumerable.Range(from + 1, Math.Max(0, to - from - 1)).Any(index => Writes(index, register));

        internal int TraceCopies(int before, NativeRegister register, out NativeRegister entry)
            => TraceCopies(before, register, out entry, false);

        internal int TraceScalarCopies(int before, NativeRegister register, out NativeRegister entry)
            => TraceCopies(before, register, out entry, true);

        private int TraceCopies(int before, NativeRegister register, out NativeRegister entry, bool scalar)
        {
            entry = register;
            var seen = new HashSet<(int, NativeRegister)>();
            while (seen.Add((before, entry)))
            {
                var writer = LastWriter(before, entry);
                if (writer < 0)
                    return -1;
                var instruction = body[writer];
                if (instruction.Mnemonic != Mnemonic.Mov || instruction.Op0Kind != OpKind.Register ||
                    instruction.Op1Kind != OpKind.Register ||
                    !(scalar ? instruction.Op0Register.GetSize() == instruction.Op1Register.GetSize() &&
                               instruction.Op0Register.GetSize() is 4 or 8
                        : instruction.Op0Register.GetSize() == 8 && instruction.Op1Register.GetSize() == 8))
                    return writer;
                before = writer;
                entry = instruction.Op1Register.GetFullRegister();
            }
            return -2;
        }

        internal bool IsEffect(int index) =>
            IsInvocation(body[index]) ||
            body[index].Mnemonic is Mnemonic.Div or Mnemonic.Idiv ||
            Info(index).GetUsedMemory().Any(memory => memory.Base != NativeRegister.RSP &&
                memory.Access is not (OpAccess.None or OpAccess.NoMemAccess));
    }
}
