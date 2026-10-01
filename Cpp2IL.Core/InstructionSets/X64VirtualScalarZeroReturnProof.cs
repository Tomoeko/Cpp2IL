using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>A complete positive-zero leaf with separately authenticated original virtual dispatch.</summary>
internal static partial class X64VirtualScalarZeroReturnProof
{
    internal const string EvidenceKey = "X64VirtualScalarZeroReturnProof";

    internal sealed class Evidence
    {
        private readonly NativeInstruction[] _body;
        private readonly MethodAnalysisContext[] _aliases;
        private readonly TypeAnalysisContext[] _types;
        private readonly object[] _inputs;
        private readonly X64GenericMethodTableProof.Evidence _genericTables;
        internal MethodAnalysisContext Method { get; }
        internal ReadOnlySpan<NativeInstruction> Body => _body;
        internal ReadOnlySpan<MethodAnalysisContext> Aliases => _aliases;
        internal ReadOnlySpan<TypeAnalysisContext> Types => _types;

        // IL2CPP metadata has no original managed MethodImpl table. The body and
        // observed slot relation do not manufacture that missing declaration fact.
        internal bool MethodImplRowPresenceUnavailable => true;
        internal bool ExplicitInterfacePropertyRowPresenceUnavailable => true;

        internal Evidence(MethodAnalysisContext method, NativeInstruction[] body,
            MethodAnalysisContext[] aliases, TypeAnalysisContext[] types, object[] inputs,
            X64GenericMethodTableProof.Evidence genericTables)
        {
            Method = method;
            _body = body.ToArray();
            _aliases = aliases.ToArray();
            _types = types.ToArray();
            _inputs = inputs.ToArray();
            _genericTables = genericTables;
        }

        internal bool IsUnchanged() => Method.AppContext.Binary is PE pe &&
            X64UnwindProof.ForApplication(Method.AppContext) is { } unwind &&
            _genericTables.Matches(Method.AppContext, pe, unwind) && Find(Method) is { } current &&
            _body.SequenceEqual(current._body) && _aliases.SequenceEqual(current._aliases) &&
            _types.SequenceEqual(current._types) && _inputs.SequenceEqual(current._inputs);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> native)
    {
        if (!X64ScalarZeroReturnProof.MatchesBody(native, method.UnderlyingPointer) ||
            Find(method) is not { } proof || !native.SequenceEqual(proof.Body.ToArray()) ||
            HasEvidence(method) && (method.GetExtraData<Evidence>(EvidenceKey) is not { } saved || !saved.IsUnchanged()))
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        IOperand zero = ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType)
            ? new FloatLiteral(0f) : new DoubleLiteral(0d);
        return [new Instruction(0, OpCode.Return, zero) { NativeAddress = native[^1].IP }];
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!method.IsVirtual || !X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
                X64UnwindProof.ForApplication(app) is not { } unwind) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            var prefix = Cpp2IL.Core.Utils.X86Utils.Iterate(method).Take(3).ToArray();
            var count = Array.FindIndex(prefix, site => site.Code == Code.Retnq) + 1;
            if (count == 0) return null;
            var body = prefix.Take(count).ToArray();
            if (!X64ScalarZeroReturnProof.MatchesBody(body, method.UnderlyingPointer) ||
                !X64ScalarZeroReturnProof.HasAuthenticatedBody(method, body, pe, unwind) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                X64GenericMethodTableProof.TryIdentify(app, pe, unwind) is not { } tables ||
                !CompleteAliases(method, tables, out var aliases)) return null;
            var inputs = new List<object> { app, app.LibCpp2IlContext, app.Metadata, pe };
            var types = new List<TypeAnalysisContext>();
            foreach (var alias in aliases)
                if (!CaptureSignature(alias, inputs) || !CaptureDispatch(alias, inputs, types) ||
                    !X64ScalarZeroReturnProof.HasAuthenticatedBody(alias, body, pe, unwind) ||
                    X86CallerExceptionRegionProof.Check(alias, body, new HashSet<ulong>()) != null) return null;
            return new(method, body, aliases, types.Distinct().ToArray(), inputs.ToArray(), tables);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (method.GetExtraData<Evidence>(EvidenceKey) is not { } proof || !proof.IsUnchanged() ||
                !NativeStraightLineGraph.TryGetBody(method, out var body) || body is not [var returned] ||
                returned.OpCode != OpCode.Return || returned.IntegerBitWidth != 0 ||
                returned.CallSemantics != CallSemantics.Direct || returned.NativeAddress != proof.Body[^1].IP ||
                method.NullCheckedFieldAccesses.Count != 0 || method.NullArmFieldProbes.Count != 0 ||
                method.InlinedBooleanSetters.Count != 0) return false;
            return ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType)
                ? returned.Operands is [FloatLiteral single] && BitConverter.ToInt32(BitConverter.GetBytes(single.Value), 0) == 0
                : returned.Operands is [DoubleLiteral doublePrecision] && BitConverter.DoubleToInt64Bits(doublePrecision.Value) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return false;
        }
    }
}
