using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

// Authenticate every original row before selecting the canonical Object context.
// Nested descriptor payloads and native cache/helper semantics remain separate.
internal static class X64GenericInstantiationTableProof
{
    private const int MaximumRows = 1_000_000;
    private const int MaximumArguments = 32;
    private const int DescriptorBytes = sizeof(ulong) + sizeof(uint);

    internal sealed class Descriptor
    {
        internal Il2CppType Original { get; }
        internal ulong Address { get; }
        internal ulong Datapoint { get; }
        internal uint Bits { get; }
        internal ulong Dummy { get; }
        internal uint Attrs { get; }
        internal Il2CppTypeEnum Type { get; }
        internal uint NumMods { get; }
        internal uint Byref { get; }
        internal uint Pinned { get; }
        internal uint ValueType { get; }
        private readonly byte[] _bytes;
        internal ReadOnlySpan<byte> Bytes => _bytes;

        internal Descriptor(Il2CppType original, ulong address, byte[] bytes)
        {
            Original = original;
            Address = address;
            Datapoint = original.Datapoint;
            Bits = original.Bits;
            Dummy = original.Data.Dummy;
            Attrs = original.Attrs;
            Type = original.Type;
            NumMods = original.NumMods;
            Byref = original.Byref;
            Pinned = original.Pinned;
            ValueType = original.ValueType;
            _bytes = (byte[])bytes.Clone();
        }

        internal bool Matches(Descriptor other) => ReferenceEquals(Original, other.Original) &&
            Address == other.Address && Datapoint == other.Datapoint && Bits == other.Bits &&
            Dummy == other.Dummy && Attrs == other.Attrs && Type == other.Type &&
            NumMods == other.NumMods && Byref == other.Byref && Pinned == other.Pinned &&
            ValueType == other.ValueType && _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    internal sealed class Row
    {
        internal int Ordinal { get; }
        internal ulong Address { get; }
        internal ulong Count { get; }
        internal ulong Arguments { get; }
        private readonly ulong[] _argumentPointers;
        private readonly Descriptor[] _descriptors;
        internal ReadOnlySpan<ulong> ArgumentPointers => _argumentPointers;
        internal ReadOnlySpan<Descriptor> Descriptors => _descriptors;

        internal Row(int ordinal, ulong address, ulong count, ulong arguments,
            ulong[] argumentPointers, Descriptor[] descriptors)
        {
            Ordinal = ordinal;
            Address = address;
            Count = count;
            Arguments = arguments;
            _argumentPointers = (ulong[])argumentPointers.Clone();
            _descriptors = (Descriptor[])descriptors.Clone();
        }

        internal bool Matches(Row other)
        {
            if (Ordinal != other.Ordinal || Address != other.Address || Count != other.Count ||
                Arguments != other.Arguments || !_argumentPointers.AsSpan().SequenceEqual(other._argumentPointers) ||
                _descriptors.Length != other._descriptors.Length)
                return false;
            for (var ordinal = 0; ordinal < _descriptors.Length; ordinal++)
                if (!_descriptors[ordinal].Matches(other._descriptors[ordinal])) return false;
            return true;
        }
    }

    internal sealed class Evidence
    {
        internal Il2CppBinary.GenericInstantiationTableRegistration Table { get; }
        internal int CanonicalObjectOrdinal { get; }
        private readonly Row[] _rows;
        internal ReadOnlySpan<Row> Rows => _rows;

        internal Evidence(Il2CppBinary.GenericInstantiationTableRegistration table, Row[] rows,
            int canonicalObjectOrdinal)
        {
            Table = table;
            CanonicalObjectOrdinal = canonicalObjectOrdinal;
            _rows = (Row[])rows.Clone();
        }

        internal bool Matches(ApplicationAnalysisContext app, PE pe, X64UnwindProof.Index index) =>
            TryIdentify(app, pe, index) is { } current && Matches(current);

        internal bool Matches(Evidence current)
        {
            if (current.Table != Table || current.CanonicalObjectOrdinal != CanonicalObjectOrdinal ||
                current._rows.Length != _rows.Length) return false;
            for (var ordinal = 0; ordinal < _rows.Length; ordinal++)
                if (!_rows[ordinal].Matches(current._rows[ordinal])) return false;
            return true;
        }
    }

    internal static Evidence? TryIdentify(ApplicationAnalysisContext app, PE pe, X64UnwindProof.Index index)
    {
        try
        {
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
                !ReferenceEquals(app.Binary, pe) || !ReferenceEquals(X64UnwindProof.ForBinary(pe), index) ||
                pe.PointerSizeBytes != sizeof(ulong) || !pe.TryGetGenericInstantiationTableRegistration(out var table) ||
                table.Count is < 1 or > MaximumRows ||
                !ReadOnly(table.MetadataRegistrationAddress,
                    checked((uint)Il2CppMetadataRegistration.GetStructSize(false, 29)), out var metadata) ||
                U64(metadata, 16) != (ulong)table.Count || U64(metadata, 24) != table.Address ||
                !ReadOnly(table.Address, checked((uint)table.Count * sizeof(ulong)), out var slots))
                return null;

            var rows = new Row[table.Count];
            for (var ordinal = 0; ordinal < rows.Length; ordinal++)
            {
                if (!pe.TryGetGenericInstantiationRegistration(ordinal, out var origin) || origin.Index != ordinal ||
                    U64(slots, ordinal * sizeof(ulong)) != origin.Address ||
                    origin.ArgumentCount is < 1 or > MaximumArguments ||
                    !ReadOnly(origin.Address, 2 * sizeof(ulong), out var native) ||
                    U64(native, 0) != origin.ArgumentCount || U64(native, sizeof(ulong)) != origin.ArgumentsAddress ||
                    !ReadArguments(origin.ArgumentsAddress, checked((uint)origin.ArgumentCount * sizeof(ulong)), out var argv))
                    return null;

                var retained = pe.GetGenericInst(Il2CppVariableWidthIndex<Il2CppGenericInst>.MakeTemporaryForFixedWidthUsage(ordinal));
                if (retained.pointerCount != origin.ArgumentCount || retained.pointerStart != origin.ArgumentsAddress)
                    return null;
                var pointers = new ulong[origin.ArgumentCount];
                var descriptors = new Descriptor[origin.ArgumentCount];
                for (var argument = 0; argument < pointers.Length; argument++)
                {
                    var pointer = U64(argv, argument * sizeof(ulong));
                    // Never read lazy Il2CppGenericInst.Types before authenticating
                    // the original ordinal, native row, bounds and descriptor.
                    var type = pe.GetIl2CppTypeFromPointer(pointer);
                    if (!X64OriginalReferenceClassProof.RetainedDescriptor(app, type) ||
                        !pe.TryGetTypeVirtualAddress(type, out var address) || address != pointer ||
                        address > ulong.MaxValue - (DescriptorBytes - 1)) return null;
                    var offset = pe.MapVirtualAddressToRaw(address, false);
                    var raw = pe.GetRawBinaryContent();
                    if (offset < 0 || offset > raw.Length - DescriptorBytes ||
                        pe.MapVirtualAddressToRaw(address + DescriptorBytes - 1, false) != offset + DescriptorBytes - 1)
                        return null;
                    // Capture the native union/flags and every decoded cached value.
                    // Recursive array/generic/variable payloads are not qualified here.
                    descriptors[argument] = new(type, address, raw.Slice((int)offset, DescriptorBytes).ToArray());
                    pointers[argument] = pointer;
                }
                rows[ordinal] = new(ordinal, origin.Address, origin.ArgumentCount,
                    origin.ArgumentsAddress, pointers, descriptors);
            }

            // Malformed or ineligible original rows cannot disappear before this
            // uniqueness check. Even an ineligible second Object remains competing.
            var canonical = -1;
            for (var ordinal = 0; ordinal < rows.Length; ordinal++)
            {
                if (rows[ordinal].Descriptors is not [{ Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, Byref: 0 }]) continue;
                if (canonical >= 0) return null;
                canonical = ordinal;
            }
            if (canonical < 0 || rows[canonical].Descriptors is not
                [{ Attrs: 0, Pinned: 0, NumMods: 0, ValueType: 0 }]) return null;
            return new(table, rows, canonical);

            bool ReadArguments(ulong address, uint length, out ReadOnlySpan<byte> bytes)
            {
                // Original argv may be writable data: this binds file-backed
                // facts at capture, without asserting runtime immutability.
                if (ReadOnly(address, length, out bytes)) return true;
                if (!X64MetadataStaticGetterProof.FileBackedWritableData(pe, index, address, length)) return false;
                bytes = pe.GetRawBinaryContent().Slice(checked((int)pe.MapVirtualAddressToRaw(address, false)), checked((int)length));
                return true;
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
            return null;
        }
    }

    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));
}
