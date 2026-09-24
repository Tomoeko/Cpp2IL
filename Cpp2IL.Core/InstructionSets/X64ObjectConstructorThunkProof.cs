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
            !HasSignature(method, owner, app, allowOverlong: true) ||
            !TrySelectReachableThunk(method, owner, app, pe, unwind, decoded,
                out var thunk) ||
            !FileBacked(pe, method.UnderlyingPointer, thunk[1].NextIP))
            return null;

        var objectConstructors = app.SystemTypes.SystemObjectType.Methods.Where(candidate =>
            candidate.Name == ".ctor" && !candidate.IsStatic && !candidate.IsVirtual &&
            candidate.Parameters.Count == 0 && candidate.IsVoid &&
            candidate.UnderlyingPointer == thunk[1].NearBranchTarget).ToArray();
        if (objectConstructors is not [{ } objectConstructor] ||
            !ConstructorChainRecovery.TryProveShape(thunk, method.UnderlyingPointer,
                7, objectConstructor.UnderlyingPointer) ||
            !X64IteratorFactoryProof.ProveInertObjectConstructor(app,
                objectConstructor.UnderlyingPointer, pe, unwind) ||
            X86CallerExceptionRegionProof.Check(method, thunk, new HashSet<ulong>()) != null ||
            Enumerable.Range(1, 6).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return null;

        // The same folded thunk can implement an entire empty constructor
        // chain. Traverse only unchanged, uniquely bound zero-argument base
        // constructors to emit the required inert call for this canonical chain.
        // The original source initializer syntax, optimized-away calls, and
        // MethodDef identity of a shared native target remain unknown.
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

    private static bool TrySelectReachableThunk(MethodAnalysisContext method,
        TypeAnalysisContext owner, ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, IReadOnlyList<NativeInstruction> decoded,
        out IReadOnlyList<NativeInstruction> thunk)
    {
        thunk = Array.Empty<NativeInstruction>();
        var start = method.UnderlyingPointer;
        if (method.RawBytes.Length < 7 || decoded.Count < 2 ||
            decoded[0].IP != start || decoded[1].IP != decoded[0].NextIP ||
            decoded[1].NextIP < start || decoded[1].NextIP - start != 7)
            return false;

        if (method.RawBytes.Length == 7)
        {
            if (decoded.Count != 2)
                return false;
            thunk = decoded;
            return true;
        }

        // A native span estimate can run past this leaf into the next function.
        // Accept only an ordinary single-constructor Object child, with byte-exact trap
        // padding followed by a different handler-free unwind entry. The later function
        // must never be treated as part of the managed constructor.
        var constructors = owner.Methods.Where(candidate => candidate.Name == ".ctor")
            .ToArray();
        if (!ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
            owner.InterfaceContexts.Count != 0 ||
            owner.Definition?.HasCctor == true ||
            owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            constructors is not [var soleConstructor] ||
            !ReferenceEquals(soleConstructor, method) ||
            !app.MethodsByAddress.TryGetValue(start, out var entryAliases) ||
            entryAliases.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
            decoded.Count < 4)
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var bytes = pe.GetRawBinaryContent();
        if (start < unwind.ImageBase || start - unwind.ImageBase > uint.MaxValue - 6 ||
            rawStart < 0 || rawStart > bytes.Length - 7 ||
            !bytes.Slice(checked((int)rawStart), 7)
                .SequenceEqual(method.RawBytes.AsSpan().Slice(0, 7)) ||
            Enumerable.Range(0, 7).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                    unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) != rawStart + offset))
            return false;

        var end = decoded[1].NextIP;
        var index = 2;
        while (index < decoded.Count && decoded[index].Code == Code.Int3)
        {
            if (decoded[index].IP != end || decoded[index].Length != 1)
                return false;
            end++;
            if (end - decoded[1].NextIP > 16)
                return false;
            index++;
        }
        if (index == 2 || index == decoded.Count || decoded[index].IP != end ||
            decoded[index].IsInvalid || decoded[index].NextIP <= end ||
            end - start >= (ulong)method.RawBytes.Length ||
            end < unwind.ImageBase || end - unwind.ImageBase > uint.MaxValue ||
            Enumerable.Range(0, checked((int)(end - decoded[1].NextIP)))
                .Any(offset => !unwind.IsExecutableRva(checked((uint)(
                    decoded[1].NextIP + (ulong)offset - unwind.ImageBase))) ||
                    pe.MapVirtualAddressToRaw(decoded[1].NextIP + (ulong)offset, false) !=
                    (long)rawStart + 7 + offset) ||
            !X64NativePaddingProof.HasInt3Padding(pe, decoded[1].NextIP, end) ||
            unwind.ClassifySpan(start, end).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
            unwind.ClassifySpan(end, decoded[index].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree, Start: var nextStart,
                    RootStart: var nextRoot } ||
            nextStart != end || nextRoot != end ||
            Enumerable.Range(1, checked((int)(end - start - 1))).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return false;

        thunk = [decoded[0], decoded[1]];
        return true;
    }

    private static bool HasSignature(MethodAnalysisContext method,
        TypeAnalysisContext owner, ApplicationAnalysisContext app,
        bool allowOverlong = false) =>
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
        method.UnderlyingPointer != 0 &&
        (method.RawBytes.Length == 7 || allowOverlong && method.RawBytes.Length > 7);

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
