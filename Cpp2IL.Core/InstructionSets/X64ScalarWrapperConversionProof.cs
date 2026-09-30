using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves construction of a signed-word wrapper by a complete register-only leaf.</summary>
internal static class X64ScalarWrapperConversionProof
{
    internal const string EvidenceKey = "X64ScalarWrapperConversionProof";

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Proof>(EvidenceKey) != null;

    internal sealed record Proof(X64SmallAggregateFieldGetterProof.Shape Native,
        ParameterAnalysisContext Parameter, FieldAnalysisContext Field,
        X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native &&
            ReferenceEquals(Parameter, other.Parameter) && ReferenceEquals(Field, other.Field) &&
            Input.Matches(other.Input);
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        try
        {
            if (method is not { AppContext: { Binary: PE } app, DeclaringType: { Definition: { HasCctor: true } } owner,
                    Definition: { GenericContainer: null, parameterCount: 1 } definition } ||
                !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                definition.InternalParameterData is not [var original] || method.Parameters is not [var parameter] ||
                !ReferenceEquals(parameter.Definition, original) || parameter.ParameterIndex != 0 ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
                parameter.OverrideParameterType != null || parameter.Name != parameter.DefaultName ||
                parameter.Attributes != parameter.DefaultAttributes || parameter.UseOverrideDefaultValue ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt16Type) ||
                original.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I2,
                    NumMods: 0, Byref: 0, Pinned: 0, Data: not null } rawParameter ||
                !method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
                method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
                method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                    MethodImplAttributes.InternalCall)) != 0 || method.OverrideReturnType != null ||
                !ReferenceEquals(method.ReturnType, owner) || !ReferenceEquals(method.DefaultReturnType, owner) ||
                definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0, Data: not null } rawReturn ||
                !ReferenceEquals(app.ResolveIl2CppType(rawReturn), owner) || method.BaseMethod != null ||
                method.Overrides.Count != 0 || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
                X64SmallAggregateFieldGetterProof.AggregateField(owner, method) is not { } field ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt16Type) ||
                !X64SmallAggregateFieldGetterProof.OriginalAbi(method))
                return null;

            var body = X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) ??
                       X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32);
            if (body == null || X64SmallAggregateFieldGetterProof.TryProveShape(body) is not
                    { Width: 16, Signed: false } shape)
                return null;

            var values = new List<object>();
            X64SmallAggregateFieldGetterProof.CaptureType(owner, values);
            X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
            values.Add(parameter);
            values.Add(parameter.Name);
            values.Add(parameter.Attributes);
            values.Add(parameter.ParameterIndex);
            values.Add(parameter.ParameterType);
            values.Add(original.nameIndex);
            values.Add(original.token);
            values.Add(original.typeIndex);
            X64SmallAggregateFieldGetterProof.CaptureRawType(rawParameter, values);
            X64SmallAggregateFieldGetterProof.CaptureRawType(rawReturn, values);
            if (!X64ScalarWrapperInitializationProof.TryCapture(owner, field, values)) return null;
            var length = checked((int)(shape.Return.NextIP - method.UnderlyingPointer));
            return new(shape, parameter, field,
                new(values, method.RawBytes.AsSpan().Slice(0, length).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static bool TryAuthenticate(MethodAnalysisContext method, out Proof proof)
    {
        proof = null!;
        if (Find(method) is not { } current) return false;
        var saved = method.GetExtraData<Proof>(EvidenceKey);
        if (NativeRecoveryProofTracker.Has(method, EvidenceKey))
        {
            if (saved == null || !saved.Matches(current)) return false;
        }
        else
        {
            if (saved != null) return false;
            method.PutExtraData(EvidenceKey, current);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        }
        proof = current;
        return true;
    }
}
