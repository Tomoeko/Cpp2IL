using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates a complete parameter class test against an ordinary sealed
/// class. Exact object-class equality replaces a hierarchy lookup; all target
/// metadata, initialization, ABI and null-result rules remain independently bound.
/// </summary>
internal static class X64SealedParameterClassTestProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal static TypeAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            var booleanResult = ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType);
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !X64ParameterClassTestProof.OrdinaryMethod(method, booleanResult) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method) ||
                decoded.Count == 0 || decoded[0].IP != method.UnderlyingPointer)
                return null;

            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } current)
                return null;
            var body = current.TakeWhile(instruction => instruction.Code != Code.Int3).ToArray();
            var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
            if (TryProveShape(body, booleanResult) is not { } shape ||
                !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
                region.End - body[^1].NextIP > 15 ||
                current.Skip(body.Length).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
                !decoded.Take(body.Length).SequenceEqual(body) ||
                decoded.TakeWhile(instruction => instruction.IP < region.End).Skip(body.Length)
                    .Any(instruction => instruction.Code != Code.Int3) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
                return null;

            return BindProvedShape(method, shape, booleanResult);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Shape binding is not complete emission evidence. Find must authenticate
    // the current loaded native caller and every return before using this target.
    internal static TypeAnalysisContext? BindProvedShape(MethodAnalysisContext method,
        X64ParameterClassTestProof.Shape shape, bool booleanResult)
    {
        try
        {
            if (!X64ParameterClassTestProof.OrdinaryMethod(method, booleanResult) ||
                X64ParameterClassTestProof.BindTypeInfoTarget(method, shape) is not { IsSealed: true } target ||
                !booleanResult && !ReferenceEquals(method.ReturnType, target))
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
        IReadOnlyList<NativeInstruction> body, bool booleanResult)
    {
        var shape = X64ClassCastLookupProof.TryProveSealedParameterShape(body, booleanResult);
        return shape == null ? null : new X64ParameterClassTestProof.Shape(shape.Flag,
            shape.TypeInfoSlot, shape.Initializer);
    }
}
