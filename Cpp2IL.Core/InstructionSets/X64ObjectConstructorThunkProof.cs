using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates an exact-target empty constructor tail thunk before the
/// shared Object constructor address can be resolved to an unrelated alias.
/// </summary>
internal static class X64ObjectConstructorThunkProof
{
    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } baseConstructor)
            return null;

        return
        [
            new(0, ISIL.OpCode.CallVoid, baseConstructor, new ISIL.Register(null, "rcx")),
            new(1, ISIL.OpCode.Return),
        ];
    }

    internal static MethodAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { } owner ||
            !HasSignature(method, owner, app) ||
            decoded.Count != 2 || decoded[0].IP != method.UnderlyingPointer ||
            decoded[1].NextIP != method.UnderlyingPointer + (ulong)method.RawBytes.Length ||
            !FileBacked(pe, method.UnderlyingPointer, decoded[1].NextIP))
            return null;

        var objectConstructors = app.SystemTypes.SystemObjectType.Methods.Where(candidate =>
            candidate.Name == ".ctor" && !candidate.IsStatic && !candidate.IsVirtual &&
            candidate.Parameters.Count == 0 && candidate.IsVoid &&
            candidate.UnderlyingPointer == decoded[1].NearBranchTarget).ToArray();
        if (objectConstructors is not [{ } objectConstructor] ||
            !ConstructorChainRecovery.TryProveShape(decoded, method.UnderlyingPointer,
                method.RawBytes.Length, objectConstructor.UnderlyingPointer) ||
            !X64IteratorFactoryProof.ProveInertObjectConstructor(app,
                objectConstructor.UnderlyingPointer, pe, unwind) ||
            X86CallerExceptionRegionProof.Check(method, decoded, new HashSet<ulong>()) != null ||
            Enumerable.Range(1, method.RawBytes.Length - 1).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return null;

        // The same folded thunk can implement an entire empty constructor
        // chain. Each managed constructor must call its immediate base, so
        // traverse only unchanged, uniquely bound zero-argument base ctors.
        var seen = new HashSet<TypeAnalysisContext>();
        var current = owner;
        MethodAnalysisContext? immediateBase = null;
        while (seen.Add(current))
        {
            if (ReferenceEquals(current.BaseType, app.SystemTypes.SystemObjectType))
                return immediateBase ?? objectConstructor;
            if (current.BaseType is not { } baseType)
                return null;
            var baseConstructors = baseType.Methods.Where(candidate =>
                candidate.Name == ".ctor" && !candidate.IsStatic && !candidate.IsVirtual &&
                candidate.Parameters.Count == 0 && candidate.IsVoid &&
                candidate.UnderlyingPointer == method.UnderlyingPointer).ToArray();
            if (baseConstructors is not [{ } baseConstructor])
                return null;
            baseConstructor.EnsureRawBytes();
            if (!HasSignature(baseConstructor, baseType, app))
                return null;
            immediateBase ??= baseConstructor;
            current = baseType;
        }
        return null;
    }

    private static bool HasSignature(MethodAnalysisContext method,
        TypeAnalysisContext owner, ApplicationAnalysisContext app) =>
        method.Name == ".ctor" && method.Name == method.DefaultName &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall)) == 0 &&
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        owner.Definition is { GenericContainer: null,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        owner.Attributes == owner.DefaultAttributes &&
        owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
        owner.GenericParameters.Count == 0 && !owner.IsGenericInstance &&
        owner.BaseType is { } baseType && NullCheckedCall.IsReferenceClass(baseType) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding: false) &&
        method.UnderlyingPointer != 0 && method.RawBytes.Length == 7;

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
    }
}
