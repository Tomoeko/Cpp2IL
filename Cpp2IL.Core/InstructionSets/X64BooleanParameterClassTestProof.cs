using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves an ordinary object-parameter class test returning a canonical Boolean.
/// The target comes from authenticated TypeInfo, never from the Boolean return
/// type. Unsupported targets and exceptional explicit-cast helpers stay unresolved.
/// </summary>
internal static class X64BooleanParameterClassTestProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal static TypeAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                !X64ParameterClassTestProof.OrdinaryMethod(method, booleanResult: true) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method) ||
                decoded.Count == 0 || decoded[0].IP != method.UnderlyingPointer)
                return null;

            var start = method.UnderlyingPointer;
            var region = unwind.ClassifySpan(start, start + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != start || region.RootStart != start ||
                region.End <= start || region.End - start is < 121 or > 136 ||
                unwind.ClassifySpan(start, region.End).Kind != X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame) ||
                app.MethodsByAddress.Keys.Any(address => address > start && address < region.End))
                return null;

            var inRegion = decoded.TakeWhile(instruction => instruction.IP < region.End).ToArray();
            var body = inRegion.TakeWhile(instruction => instruction.Code != Code.Int3).ToArray();
            if (TryProveShape(body) is not { } shape || body[^1].NextIP > region.End ||
                inRegion.Skip(body.Length).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, start,
                    checked((uint)(body[^1].NextIP - start))) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
                return null;

            method.EnsureRawBytes();
            var length = checked((int)(body[^1].NextIP - start));
            if (method.RawBytes.Length < length ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                    method.RawBytes.AsSpan().Slice(0, length), start) ||
                !X86Utils.Iterate(method).Take(body.Length).SequenceEqual(body))
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // A binding alone is not emission evidence: Find authenticates the complete
    // loaded caller, including both Boolean exits, before this step is used.
    internal static TypeAnalysisContext? BindProvedShape(MethodAnalysisContext method,
        X64ParameterClassTestProof.Shape shape)
    {
        try
        {
            if (!X64ParameterClassTestProof.OrdinaryMethod(method, booleanResult: true) ||
                X64ParameterClassTestProof.BindTypeInfoTarget(method, shape) is not { IsSealed: false } target)
                return null;
            return target;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static X64ParameterClassTestProof.Shape? TryProveShape(
        IReadOnlyList<NativeInstruction> body)
    {
        var shape = X64ClassCastLookupProof.TryProveBooleanParameterShape(body);
        return shape == null ? null : new X64ParameterClassTestProof.Shape(shape.Flag,
            shape.TypeInfoSlot, shape.Initializer);
    }
}
