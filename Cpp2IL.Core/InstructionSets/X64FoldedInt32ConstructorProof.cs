using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Recovers an Object-child constructor that writes its unchanged Int32
/// argument to one instance field. An exact unwind entry bounds the body when
/// the managed-address estimate continues into a later native function.
/// </summary>
internal static class X64FoldedInt32ConstructorProof
{
    internal sealed record Evidence(MethodAnalysisContext BaseConstructor,
        FieldAnalysisContext State, ulong NativeEnd);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        return
        [
            new(0, ISIL.OpCode.CallVoid, proof.BaseConstructor, receiver),
            new(1, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.State.Offset),
                new ISIL.Register(null, "rdx")),
            new(2, ISIL.OpCode.Return),
        ];
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
                method.DeclaringType is not { } owner ||
                !OrdinaryObjectChild(owner, app) ||
                !X64IteratorFactoryProof.HasConstructorSignature(method, owner, app) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var aliases) ||
                aliases.Count(candidate => ReferenceEquals(candidate, method)) != 1)
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            var span = unwind.ClassifySpan(start, start + 1);
            if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                span.Start != start || span.RootStart != start ||
                span.End - start != 36 ||
                !X64IteratorFactoryProof.MatchesConstructorUnwind(
                    unwind, start, span.End) ||
                method.RawBytes.Length < 36 || decoded.Count < 12 ||
                !FileBackedExecutableBody(method, pe, unwind, start, span.End) ||
                Enumerable.Range(1, 35).Any(offset =>
                    app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
                return null;

            // RawBytes is refreshed by EnsureRawBytes. Bind the supplied decode to
            // those authenticated PE bytes, including the estimated suffix.
            var exactBody = X86Utils.Iterate(method.RawBytes.AsSpan().Slice(0, 36),
                start, false);
            if (exactBody.Count != 12 ||
                !decoded.Take(12).SequenceEqual(exactBody) ||
                !decoded.SequenceEqual(X86Utils.Iterate(method)))
                return null;

            var body = decoded.Take(12).ToArray();
            if (body[0].IP != start || body[^1].NextIP != span.End ||
                !ProveEstimatedSuffix(decoded, method.RawBytes.Length,
                    span.End, pe, unwind, app) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null)
                return null;

            var offset = body[7].MemoryDisplacement64;
            if (offset is < 16 or > int.MaxValue ||
                owner.Fields.Where(field => !field.IsStatic &&
                    field.Offset == (long)offset) is not { } fields ||
                fields.ToArray() is not [{ } state] ||
                !ReferenceEquals(state.DeclaringType, owner) ||
                !ReferenceEquals(state.FieldType,
                    app.SystemTypes.SystemInt32Type) ||
                state.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(state,
                        new ISIL.LocalVariable("constructor-receiver",
                            new ISIL.Register(null, "rcx"), owner),
                        state.Offset), 32))
                return null;

            // The target may have other managed aliases, but its complete native
            // body is proved inert below. A direct Object child still requires
            // a base constructor call; the original callsite MethodDef identity
            // cannot be recovered from this folded address alone.
            var objectConstructors = app.SystemTypes.SystemObjectType.Methods
                .Where(candidate => candidate.Name == ".ctor" &&
                    !candidate.IsStatic && candidate.Parameters.Count == 0 &&
                    candidate.IsVoid &&
                    candidate.UnderlyingPointer == body[6].NearBranchTarget)
                .ToArray();
            if (objectConstructors is not [{ } objectConstructor] ||
                !X64IteratorFactoryProof.ProveInertObjectConstructor(
                    app, objectConstructor.UnderlyingPointer, pe, unwind) ||
                !X64IteratorFactoryProof.TryProveConstructorShape(body,
                    state.Offset, objectConstructor.UnderlyingPointer))
                return null;

            return new Evidence(objectConstructor, state, span.End);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryObjectChild(TypeAnalysisContext owner,
        ApplicationAnalysisContext app) =>
        !owner.IsValueType && !owner.IsInterface && !owner.IsGenericInstance &&
        owner.GenericParameters.Count == 0 &&
        owner.Attributes == owner.DefaultAttributes &&
        owner.Name == owner.DefaultName &&
        owner.Namespace == owner.DefaultNamespace &&
        ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
        ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
        owner.Definition is { HasCctor: false, GenericContainer: null,
            PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        (owner.Attributes & TypeAttributes.LayoutMask) !=
            TypeAttributes.ExplicitLayout &&
        owner.Methods.All(candidate => candidate.Name != ".cctor");

    private static bool FileBackedExecutableBody(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind, ulong start, ulong end)
    {
        if (start < unwind.ImageBase ||
            start - unwind.ImageBase > uint.MaxValue - 35 ||
            end != start + 36)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var bytes = pe.GetRawBinaryContent();
        return first >= 0 && last == first + 35 &&
               first <= bytes.Length - 36 &&
               method.RawBytes.AsSpan().Slice(0, 36)
                   .SequenceEqual(bytes.Slice((int)first, 36)) &&
               Enumerable.Range(0, 36).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(
                       start + (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                   first + offset);
    }

    private static bool ProveEstimatedSuffix(
        IReadOnlyList<NativeInstruction> decoded, int estimateLength,
        ulong end, PE pe, X64UnwindProof.Index unwind,
        ApplicationAnalysisContext app)
    {
        if (estimateLength == 36)
            return decoded.Count == 12;
        if (estimateLength < 36 || decoded.Count < 14 ||
            decoded[12].IP != end)
            return false;

        var index = 12;
        var next = end;
        while (index < decoded.Count && decoded[index].Code == Code.Int3)
        {
            if (decoded[index].IP != next || decoded[index].Length != 1 ||
                next - end >= 16)
                return false;
            next++;
            index++;
        }
        if (index == 12 || index == decoded.Count ||
            decoded[index].IP != next || decoded[index].IsInvalid ||
            decoded[index].NextIP <= next ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, next) ||
            unwind.ClassifySpan(end, next).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
            unwind.ClassifySpan(next, decoded[index].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree,
                    Start: var nextStart, RootStart: var nextRoot } ||
            nextStart != next || nextRoot != next ||
            Enumerable.Range(0, checked((int)(next - end))).Any(offset =>
                app.MethodsByAddress.ContainsKey(end + (ulong)offset)))
            return false;
        return true;
    }
}
