using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>A complete ordinary Int32 static initializer with an authenticated TypeInfo guard.</summary>
internal static partial class X64ScalarStaticConstructorProof
{
    internal const string EvidenceKey = "X64ScalarStaticConstructorProof";

    internal sealed class Evidence
    {
        private readonly object[] _input;
        private readonly X64GenericMethodTableProof.Evidence _genericTables;
        internal MethodAnalysisContext Method { get; }
        internal FieldAnalysisContext StaticField { get; }
        internal uint ValueBits { get; }

        internal Evidence(MethodAnalysisContext method, FieldAnalysisContext field, uint bits,
            List<object> input, X64GenericMethodTableProof.Evidence genericTables)
        {
            Method = method;
            StaticField = field;
            ValueBits = bits;
            _input = input.ToArray();
            _genericTables = genericTables;
        }

        internal bool IsUnchanged() => Method.AppContext.Binary is PE pe &&
            X64UnwindProof.ForApplication(Method.AppContext) is { } unwind &&
            _genericTables.Matches(Method.AppContext, pe, unwind) &&
            Find(Method) is { } current && ReferenceEquals(StaticField, current.StaticField) &&
            ValueBits == current.ValueBits && _input.SequenceEqual(current._input);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static bool TryAuthenticate(MethodAnalysisContext method, out Evidence proof)
    {
        proof = null!;
        if (HasEvidence(method) && (method.GetExtraData<Evidence>(EvidenceKey) is not { } saved || !saved.IsUnchanged()) ||
            Find(method) is not { } current) return false;
        method.PutExtraData(EvidenceKey, current);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        proof = current;
        return true;
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            if (method.DeclaringType is not { IsValueType: false } owner ||
                owner.Fields.Where(field => field.IsStatic).ToArray() is not [var field] ||
                !ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemInt32Type) ||
                X64Stack28BodyProof.Read(method, 11, 96) is not { } body ||
                X64ScalarWrapperStaticConstructorProof.TryProveShape(body) is not { Width: 4 } shape)
                return null;
            var input = new List<object>();
            if (!CaptureBinding(method, field, shape, input, out var genericTables)) return null;
            return new(method, field, unchecked((uint)shape.ValueBits), input, genericTables);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }
}
