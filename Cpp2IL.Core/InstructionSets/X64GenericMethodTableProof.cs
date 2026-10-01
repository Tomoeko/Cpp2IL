using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

// Original registration closure, not a native implementation or helper proof.
// Every original row is authenticated before any semantic selection is possible.
internal static class X64GenericMethodTableProof
{
    private const int MaximumRows = 1_000_000;
    private const int SpecificationBytes = 12;
    private const int FunctionBytes = 16;

    internal sealed record Specification(Il2CppMethodSpec Instance,
        Il2CppBinary.GenericMethodSpecificationRegistration Origin);

    internal sealed record Function(Il2CppGenericMethodFunctionsDefinitions Instance,
        Il2CppBinary.GenericMethodFunctionRegistration Origin);

    internal sealed class Evidence
    {
        internal Il2CppBinary.GenericMethodTableRegistration Origin { get; }
        internal X64GenericInstantiationTableProof.Evidence Instantiations { get; }
        private readonly Specification[] _specifications;
        private readonly Function[] _functions;
        private readonly ulong[] _methodPointers;
        private readonly ulong[] _invokers;
        private readonly ulong[] _adjustorThunks;

        internal ReadOnlySpan<Specification> Specifications => _specifications;
        internal ReadOnlySpan<Function> Functions => _functions;
        internal ReadOnlySpan<ulong> MethodPointers => _methodPointers;
        internal ReadOnlySpan<ulong> Invokers => _invokers;
        internal ReadOnlySpan<ulong> AdjustorThunks => _adjustorThunks;

        internal Evidence(Il2CppBinary.GenericMethodTableRegistration origin,
            X64GenericInstantiationTableProof.Evidence instantiations,
            Specification[] specifications, Function[] functions,
            ulong[] methodPointers, ulong[] invokers, ulong[] adjustorThunks)
        {
            Origin = origin;
            Instantiations = instantiations;
            _specifications = (Specification[])specifications.Clone();
            _functions = (Function[])functions.Clone();
            _methodPointers = (ulong[])methodPointers.Clone();
            _invokers = (ulong[])invokers.Clone();
            _adjustorThunks = (ulong[])adjustorThunks.Clone();
        }

        internal bool Matches(ApplicationAnalysisContext app, PE pe, X64UnwindProof.Index index)
        {
            if (TryIdentify(app, pe, index) is not { } current || current.Origin != Origin ||
                !Instantiations.Matches(current.Instantiations) ||
                _specifications.Length != current._specifications.Length ||
                _functions.Length != current._functions.Length)
                return false;
            for (var ordinal = 0; ordinal < _specifications.Length; ordinal++)
                if (_specifications[ordinal].Origin != current._specifications[ordinal].Origin ||
                    !ReferenceEquals(_specifications[ordinal].Instance, current._specifications[ordinal].Instance))
                    return false;
            for (var ordinal = 0; ordinal < _functions.Length; ordinal++)
                if (_functions[ordinal].Origin != current._functions[ordinal].Origin ||
                    !ReferenceEquals(_functions[ordinal].Instance, current._functions[ordinal].Instance))
                    return false;
            return _methodPointers.AsSpan().SequenceEqual(current._methodPointers) &&
                   _invokers.AsSpan().SequenceEqual(current._invokers) &&
                   _adjustorThunks.AsSpan().SequenceEqual(current._adjustorThunks);
        }
    }

    internal static Evidence? TryIdentify(ApplicationAnalysisContext app, PE pe, X64UnwindProof.Index index)
    {
        try
        {
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
                pe.PointerSizeBytes != sizeof(ulong) || !ReferenceEquals(app.Binary, pe) ||
                !ReferenceEquals(X64UnwindProof.ForBinary(pe), index) ||
                !pe.TryGetGenericMethodTableRegistration(out var origin) ||
                origin.SpecificationCount is < 1 or > MaximumRows ||
                origin.FunctionCount is < 1 or > MaximumRows ||
                origin.MethodPointerCount is < 1 or > MaximumRows ||
                origin.InvokerCount is < 1 or > MaximumRows ||
                !ReadOnly(origin.CodeRegistrationAddress,
                    checked((uint)Il2CppCodeRegistration.GetStructSize(false, 29)), out var code) ||
                !ReadOnly(origin.MetadataRegistrationAddress,
                    checked((uint)Il2CppMetadataRegistration.GetStructSize(false, 29)), out var metadata) ||
                U64(code, 16) != origin.MethodPointerCount ||
                U64(code, 24) != origin.MethodPointersAddress ||
                U64(code, 32) != origin.AdjustorThunksAddress ||
                U64(code, 40) != origin.InvokerCount || U64(code, 48) != origin.InvokersAddress ||
                U64(metadata, 32) != (ulong)origin.FunctionCount ||
                U64(metadata, 40) != origin.FunctionsAddress ||
                U64(metadata, 64) != (ulong)origin.SpecificationCount ||
                U64(metadata, 72) != origin.SpecificationsAddress ||
                X64GenericInstantiationTableProof.TryIdentify(app, pe, index) is not { } instantiations ||
                instantiations.Table.MetadataRegistrationAddress != origin.MetadataRegistrationAddress ||
                !ReadOnly(origin.SpecificationsAddress,
                    checked((uint)origin.SpecificationCount * SpecificationBytes), out var specs) ||
                !ReadOnly(origin.FunctionsAddress,
                    checked((uint)origin.FunctionCount * FunctionBytes), out var functions) ||
                ReadPointerTable(origin.MethodPointersAddress, origin.MethodPointerCount) is not { } methodPointers ||
                ReadPointerTable(origin.InvokersAddress, origin.InvokerCount) is not { } invokers)
                return null;

            var retainedSpecifications = new Specification[origin.SpecificationCount];
            for (var ordinal = 0; ordinal < retainedSpecifications.Length; ordinal++)
            {
                if (!pe.TryGetGenericMethodSpecificationRegistration(ordinal, out var original) ||
                    original.Index != ordinal ||
                    I32(specs, ordinal * SpecificationBytes) != original.MethodDefinitionIndex ||
                    I32(specs, ordinal * SpecificationBytes + 4) != original.ClassInstantiationIndex ||
                    I32(specs, ordinal * SpecificationBytes + 8) != original.MethodInstantiationIndex ||
                    original.MethodDefinitionIndex < 0 || original.MethodDefinitionIndex >= app.Metadata.MethodDefinitionCount ||
                    !InstantiationIndex(original.ClassInstantiationIndex) ||
                    !InstantiationIndex(original.MethodInstantiationIndex))
                    return null;
                var retained = app.Metadata.AllGenericMethodSpecs[ordinal];
                if (retained.methodDefinitionIndex.Value != original.MethodDefinitionIndex ||
                    retained.classIndexIndex.Value != original.ClassInstantiationIndex ||
                    retained.methodIndexIndex.Value != original.MethodInstantiationIndex)
                    return null;
                retainedSpecifications[ordinal] = new(retained, original);
            }

            var retainedFunctions = new Function[origin.FunctionCount];
            var adjustorCount = 0;
            for (var ordinal = 0; ordinal < retainedFunctions.Length; ordinal++)
            {
                if (!pe.TryGetGenericMethodFunctionRegistration(ordinal, out var original) ||
                    original.Index != ordinal ||
                    I32(functions, ordinal * FunctionBytes) != original.SpecificationIndex ||
                    I32(functions, ordinal * FunctionBytes + 4) != original.MethodPointerIndex ||
                    I32(functions, ordinal * FunctionBytes + 8) != original.InvokerIndex ||
                    I32(functions, ordinal * FunctionBytes + 12) != original.AdjustorThunkIndex ||
                    original.SpecificationIndex < 0 || original.SpecificationIndex >= origin.SpecificationCount ||
                    !OptionalIndex(original.MethodPointerIndex, origin.MethodPointerCount) ||
                    !OptionalIndex(original.InvokerIndex, origin.InvokerCount) ||
                    original.AdjustorThunkIndex is < -1 or >= MaximumRows)
                    return null;
                var retained = app.Metadata.genericMethodTables[ordinal];
                if (retained.GenericMethodIndex != original.SpecificationIndex ||
                    retained.methodIndex != original.MethodPointerIndex ||
                    retained.invokerIndex != original.InvokerIndex ||
                    retained.adjustorThunk != original.AdjustorThunkIndex)
                    return null;
                retainedFunctions[ordinal] = new(retained, original);
                adjustorCount = Math.Max(adjustorCount, original.AdjustorThunkIndex + 1);
            }

            // The native registration has no adjustor count. Its consumed extent
            // is derived from ALL immutable original function indices, never from
            // an eligible subset. Unreferenced trailing slots have no denominator.
            var adjustors = adjustorCount == 0 ? Array.Empty<ulong>() :
                ReadPointerTable(origin.AdjustorThunksAddress, (ulong)adjustorCount);
            if (adjustors == null) return null;

            // These slot values are original file-backed input facts at capture.
            // The producer does not snapshot their initial values during Init.
            // Matches binds their freshness; executable ABI/body/alias semantics
            // and each selected per-reference native registration remain separate.
            return new(origin, instantiations, retainedSpecifications, retainedFunctions,
                methodPointers, invokers, adjustors);

            bool InstantiationIndex(int value) => value == -1 || value >= 0 && value < instantiations.Rows.Length;

            ulong[]? ReadPointerTable(ulong address, ulong count)
            {
                if (!ReadOnly(address, checked((uint)count * sizeof(ulong)), out var values)) return null;
                var result = new ulong[count];
                for (var ordinal = 0; ordinal < result.Length; ordinal++)
                    result[ordinal] = U64(values, ordinal * sizeof(ulong));
                return result;
            }

            bool ReadOnly(ulong address, uint length, out ReadOnlySpan<byte> bytes)
            {
                bytes = default;
                var offset = index.MapReadOnlyData(address, length);
                if (offset < 0 || length > pe.GetRawBinaryContent().Length - (long)offset) return false;
                bytes = pe.GetRawBinaryContent().Slice(offset, checked((int)length));
                return true;
            }
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            // Malformed or unsupported registration is an unavailable precondition.
            // No recovery disposition, fallback body, or helper evidence is emitted.
            return null;
        }
    }

    private static bool OptionalIndex(int value, ulong count) => value == -1 || value >= 0 && (ulong)value < count;

    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));

    private static int I32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
}
