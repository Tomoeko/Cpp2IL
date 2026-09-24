using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Restores an immediate-base call when two or more empty constructor layers
/// share one complete tail thunk to an older constructor. The native target
/// identifies the executed body, while the unchanged inheritance chain
/// identifies the only legal managed call for the current constructor.
/// </summary>
internal static class X64AncestorConstructorThunkProof
{
    internal sealed record Evidence(MethodAnalysisContext BaseConstructor, ulong TailTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        if (method.Name != ".ctor" ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext))
            return null;
        try
        {
            method.EnsureRawBytes();
            return Find(method, X86Utils.Iterate(method).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryConstructor(method) ||
                method.DeclaringType is not { } owner ||
                !OrdinaryOwner(owner) ||
                owner.Methods.Where(candidate => candidate.Name == ".ctor")
                    .ToArray() is not [var soleConstructor] ||
                !ReferenceEquals(soleConstructor, method) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var entryAliases) ||
                entryAliases.Count < 2 ||
                entryAliases.Count != new HashSet<MethodAnalysisContext>(entryAliases).Count ||
                entryAliases.Any(alias => !OrdinaryConstructor(alias) ||
                    !OrdinaryOwner(alias.DeclaringType) ||
                    alias.UnderlyingPointer != method.UnderlyingPointer ||
                    alias.DeclaringType!.Methods.Count(candidate => candidate.Name == ".ctor") != 1 ||
                    RuntimeNullGuardCoalescer.HasOutputOptions(alias) ||
                    !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(alias,
                        requireUniqueBinding: false)))
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            if (start > ulong.MaxValue - 7 || method.RawBytes.Length != 7 ||
                unwind.ClassifySpan(start, start + 7).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
                !FileBackedExecutable(pe, unwind, method.RawBytes.AsSpan(), start) ||
                Enumerable.Range(1, 6).Any(offset =>
                    app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
                return null;

            var exact = X86Utils.Iterate(method.RawBytes.AsSpan(), start, false);
            if (exact.Count != 2 || decoded.Count != 2 ||
                !decoded.SequenceEqual(exact) ||
                X86CallerExceptionRegionProof.Check(method, decoded,
                    new HashSet<ulong>()) != null)
                return null;

            var target = decoded[1].NearBranchTarget;
            if (!ConstructorChainRecovery.TryProveShape(decoded, start, 7, target) ||
                target == start ||
                !app.MethodsByAddress.TryGetValue(target, out var targetAliases) ||
                targetAliases.Count == 0 ||
                targetAliases.Count != new HashSet<MethodAnalysisContext>(targetAliases).Count ||
                targetAliases.Any(alias => !TargetConstructor(alias, target)))
                return null;

            // A same-address immediate base has no native effects before the
            // authenticated tail. Continue only through unchanged, uniquely
            // bound constructors at that address until an older ancestor is
            // bound to the tail target. No parameter-count alias substitution
            // is allowed.
            var visited = new HashSet<TypeAnalysisContext> { owner };
            var current = owner;
            MethodAnalysisContext? immediateBase = null;
            while (current.BaseType is { } baseType && visited.Add(baseType) &&
                   OrdinaryOwner(baseType))
            {
                var constructors = baseType.Methods.Where(candidate =>
                    candidate.Name == ".ctor").ToArray();
                if (constructors is not [{ } baseConstructor] ||
                    !TargetConstructor(baseConstructor,
                        baseConstructor.UnderlyingPointer))
                    return null;
                immediateBase ??= baseConstructor;
                if (baseConstructor.UnderlyingPointer == target)
                {
                    if (ReferenceEquals(current, owner) ||
                        targetAliases.Count(candidate =>
                            ReferenceEquals(candidate, baseConstructor)) != 1)
                        return null;
                    return new Evidence(immediateBase, target);
                }
                if (baseConstructor.UnderlyingPointer != start ||
                    entryAliases.Count(candidate =>
                        ReferenceEquals(candidate, baseConstructor)) != 1)
                    return null;
                current = baseType;
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static bool OrdinaryOwner(TypeAnalysisContext? owner) =>
        owner is { Definition: { GenericContainer: null, HasCctor: false,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } } &&
        !owner.IsInterface && !owner.IsValueType && !owner.IsGenericInstance &&
        owner.GenericParameters.Count == 0 &&
        owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
        owner.Attributes == owner.DefaultAttributes &&
        ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
        owner.Methods.All(candidate => candidate.Name != ".cctor");

    internal static bool OrdinaryConstructor(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.Name == ".ctor" && method.Name == method.DefaultName &&
               !method.IsStatic && !method.IsVirtual && method.IsVoid &&
               method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
               method.OverrideReturnType == null &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.MemberAccessMask |
                                     MethodAttributes.SpecialName |
                                     MethodAttributes.RTSpecialName)) ==
               (MethodAttributes.Public | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName) &&
               (method.Attributes & (MethodAttributes.Abstract |
                                     MethodAttributes.PinvokeImpl)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               method.Definition is { GenericContainer: null, parameterCount: 0,
                   RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                       NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               ReferenceEquals(definition.DeclaringType,
                   method.DeclaringType?.Definition) &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) &&
               ReferenceEquals(method.ReturnType, method.DefaultReturnType);
    }

    private static bool TargetConstructor(MethodAnalysisContext method,
        ulong address) =>
        OrdinaryConstructor(method) &&
        method.UnderlyingPointer == address && address != 0 &&
        OrdinaryOwner(method.DeclaringType) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding: false);

    internal static bool FileBackedExecutable(PE pe, X64UnwindProof.Index unwind,
        ReadOnlySpan<byte> raw, ulong start)
    {
        if (raw.Length == 0)
            return false;
        var finalOffset = (ulong)raw.Length - 1;
        if (start < unwind.ImageBase ||
            start > ulong.MaxValue - finalOffset ||
            start - unwind.ImageBase > (ulong)uint.MaxValue - finalOffset)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var bytes = pe.GetRawBinaryContent();
        return first >= 0 && first <= bytes.Length - raw.Length &&
               raw.SequenceEqual(bytes.Slice(checked((int)first), raw.Length)) &&
               Enumerable.Range(0, raw.Length).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start +
                       (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                   first + offset);
    }
}
