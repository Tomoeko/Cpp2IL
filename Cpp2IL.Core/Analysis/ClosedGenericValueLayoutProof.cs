using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Computes default sequential storage after authenticating closed integer substitutions.
/// Open, synthesized, reference-containing and explicitly sized layouts remain unknown.
/// </summary>
internal static class ClosedGenericValueLayoutProof
{
    internal sealed class Layout(long size, long alignment, long[] offsets)
    {
        internal long Size { get; } = size;
        internal long Alignment { get; } = alignment;
        internal IReadOnlyList<long> Offsets { get; } = Array.AsReadOnly((long[])offsets.Clone());
    }
    private sealed record Witness(Layout Layout, object[] Facts);
    private static readonly ConditionalWeakTable<GenericInstanceTypeAnalysisContext, Witness> Witnesses = new();

    internal static Layout? Find(GenericInstanceTypeAnalysisContext instance)
    {
        try
        {
            if (!TryCapture(instance, out var layout, out var facts))
                return null;
            var witness = Witnesses.GetValue(instance, _ => new Witness(layout!, facts));
            return witness.Facts.SequenceEqual(facts) ? witness.Layout : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or EndOfStreamException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static bool WasProved(GenericInstanceTypeAnalysisContext instance) => Witnesses.TryGetValue(instance, out _);

    private static bool TryCapture(GenericInstanceTypeAnalysisContext instance, out Layout? layout, out object[] facts)
    {
        layout = null;
        facts = [];
        var app = instance.AppContext;
        var owner = instance.GenericType;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary.MetadataVersion != 29 ||
            !instance.HasUnchangedOriginalRawType || instance.OriginalRawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST, NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            !app.Binary.TryGetTypeVirtualAddress(raw, out var rawAddress) ||
            ReadUnchangedType(app, rawAddress) is not { } freshRoot || !SameRaw(freshRoot, raw) ||
            owner.Definition is not { IsValueType: true, IsEnumType: false, HasCctor: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true, GenericContainer: { } container,
                RawBaseType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0, Data: not null },
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !owner.IsValueType || owner.IsGenericInstance || owner.DeclaringType != null || !definition.DeclaringTypeIndex.IsNull ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || (owner.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemValueTypeType) ||
            owner.BaseType.Definition?.RawSizes.instance_size != 16 ||
            container.isGenericMethod || !ReferenceEquals(container.TypeOwner, definition) ||
            container.genericParameterCount is < 1 or > 8 ||
            instance.GenericArguments.Count != container.genericParameterCount ||
            owner.GenericParameters.Count != container.genericParameterCount ||
            owner.Fields.Count != definition.FieldCount || owner.Fields.Count is < 1 or > 64 ||
            !owner.Fields.Select(field => field.BackingData?.Field).SequenceEqual(definition.Fields!) ||
            owner.Methods.Count != definition.MethodCount || owner.Methods.Any(method => method.Name == ".cctor") ||
            instance.OverrideBaseType != null || instance.DeclaringType != null ||
            !ReadData(app, raw.Data.GenericClass, 32, out var classBytes))
            return false;

        var values = new List<object>();
        AddRaw(raw, values);
        values.Add(rawAddress);
        values.Add(owner); values.Add(owner.Name); values.Add(owner.Namespace); values.Add(owner.Attributes);
        values.Add(owner.BaseType); values.Add(definition.NameIndex); values.Add(definition.NamespaceIndex);
        values.Add(definition.Flags); values.Add(definition.Bitfield); values.Add(definition.ByvalTypeIndex);
        values.Add(definition.ParentIndex); values.Add(definition.DeclaringTypeIndex); values.Add(definition.GenericContainerIndex);
        values.Add(definition.FirstFieldIdx); values.Add(definition.FieldCount); values.Add(definition.FirstMethodIdx);
        values.Add(definition.MethodCount); values.Add(container.ownerIndex); values.Add(container.genericParameterCount);
        values.Add(container.genericParameterStart); values.Add(container.isGenericMethod);
        AddRaw(definition.RawType, values); AddRaw(definition.RawBaseType!, values);
        AddBytes(classBytes, values);
        var generic = raw.GetGenericClass();
        values.Add(generic.V27TypePointer); values.Add(generic.CachedClass);
        values.Add(generic.Context.class_inst); values.Add(generic.Context.method_inst);
        if (!CanonicalPointer(app, rawAddress, raw.Data.GenericClass) ||
            !CanonicalPointer(app, raw.Data.GenericClass, generic.V27TypePointer) ||
            !CanonicalPointer(app, raw.Data.GenericClass + 8, generic.Context.class_inst) ||
            generic.CachedClass != 0 || !Unrelocated(app, raw.Data.GenericClass + 24, 8) ||
            !WritableCacheSlot(app, raw.Data.GenericClass + 24) ||
            generic.Context.method_inst != 0 || !Unrelocated(app, raw.Data.GenericClass + 16, 8) ||
            !ReadData(app, generic.V27TypePointer, 16, out var baseBytes) ||
            !ReadData(app, generic.Context.class_inst, 16, out var instBytes))
            return false;
        AddBytes(baseBytes, values); AddBytes(instBytes, values);
        var baseRaw = ReadUnchangedType(app, generic.V27TypePointer);
        if (baseRaw == null || baseRaw.Type != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE ||
            !ReferenceEquals(baseRaw.AsClass(), definition) || !ReferenceEquals(baseRaw, definition.RawType))
            return false;
        AddRaw(baseRaw, values);
        var arguments = generic.Context.ClassInst!;
        values.Add(arguments.pointerCount); values.Add(arguments.pointerStart);
        if (!Unrelocated(app, generic.Context.class_inst, 8) ||
            !CanonicalPointer(app, generic.Context.class_inst + 8, arguments.pointerStart) ||
            arguments.pointerCount != (ulong)instance.GenericArguments.Count ||
            !ReadData(app, arguments.pointerStart, checked((uint)arguments.pointerCount * 8), out var argumentBytes))
            return false;
        AddBytes(argumentBytes, values);
        var pointers = arguments.Pointers;
        var argumentSizes = new long[pointers.Length];
        for (var i = 0; i < pointers.Length; i++)
        {
            if (!CanonicalPointer(app, arguments.pointerStart + (ulong)i * 8, pointers[i]))
                return false;
            var argumentRaw = ReadUnchangedType(app, pointers[i]);
            var argument = instance.GenericArguments[i];
            if (argumentRaw == null || !CanonicalInteger(argument, argumentRaw, out argumentSizes[i]))
                return false;
            values.Add(argument); AddRaw(argumentRaw, values); CapturePrimitive(argument, values);
        }
        var parameters = container.GenericParameters.ToArray();
        if (parameters.Length != container.genericParameterCount)
            return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var context = owner.GenericParameters[i];
            if (!ReferenceEquals(parameter.Owner, container) || parameter.genericParameterIndexInOwner != i ||
                parameter.constraintsCount != 0 || parameter.flags != 0 || context.Index != i ||
                context.Type != Il2CppTypeEnum.IL2CPP_TYPE_VAR || !ReferenceEquals(context.Owner, owner) ||
                context.Name != parameter.Name || context.Name != context.DefaultName ||
                context.Attributes != parameter.Attributes || context.ConstraintTypes.Count != 0)
                return false;
            values.Add(context); values.Add(parameter.ownerIndex); values.Add(parameter.nameIndex);
            values.Add(parameter.constraintsStart); values.Add(parameter.constraintsCount);
            values.Add(parameter.genericParameterIndexInOwner); values.Add(parameter.flags);
        }
        var sizes = new List<long>();
        foreach (var field in owner.Fields)
        {
            if (field.BackingData?.Field is not { RawFieldType: { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } fieldRaw } fieldDefinition ||
                !ReferenceEquals(field.DeclaringType, owner) || !ReferenceEquals(fieldDefinition.DeclaringType, definition) ||
                field.Attributes != field.DefaultAttributes || field.IsStatic ||
                (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
                field.OverrideFieldType != null || field.Name != field.DefaultName || field.Offset != field.DefaultOffset ||
                field.UseOverrideConstantValue || field.OverrideStaticArrayInitialValue != null)
                return false;
            long size;
            if (fieldRaw.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_I4 or
                Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8))
                return false;
            if (fieldRaw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VAR)
            {
                var parameter = fieldRaw.GetGenericParameterDef();
                var index = parameter.genericParameterIndexInOwner;
                if (index >= parameters.Length || !ReferenceEquals(parameters[index], parameter) ||
                    !ReferenceEquals(field.FieldType, owner.GenericParameters[index]))
                    return false;
                size = argumentSizes[index];
            }
            else if (!CanonicalInteger(field.FieldType, fieldRaw, out size))
                return false;
            sizes.Add(size);
            values.Add(field); values.Add(field.Name); values.Add(field.Attributes); values.Add(field.Offset);
            values.Add(fieldDefinition.nameIndex); values.Add(fieldDefinition.token); values.Add(fieldDefinition.typeIndex);
            AddRaw(fieldRaw, values);
        }
        layout = ComputeIntegerLayout(sizes);
        facts = values.ToArray();
        return layout != null;
    }

    // The matching runtime lays out inflated fields from the 16-byte boxed header,
    // rounds each field to its alignment, then rounds the final value to its maximum alignment.
    internal static Layout? ComputeIntegerLayout(IReadOnlyList<long> sizes)
    {
        if (sizes.Count is < 1 or > 64 || sizes.Any(size => size is not (4 or 8)))
            return null;
        long position = 0, alignment = 1;
        var offsets = new long[sizes.Count];
        for (var i = 0; i < sizes.Count; i++)
        {
            var size = sizes[i];
            position = checked((position + size - 1) & -size);
            offsets[i] = position;
            position = checked(position + size);
            alignment = Math.Max(alignment, size);
        }
        return new Layout(checked((position + alignment - 1) & -alignment), alignment, offsets);
    }

    private static bool CanonicalInteger(TypeAnalysisContext context, Il2CppType raw, out long size)
    {
        size = raw.Type switch { Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => 4,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 => 8, _ => 0 };
        var system = context.AppContext.SystemTypes;
        var canonical = raw.Type switch { Il2CppTypeEnum.IL2CPP_TYPE_I4 => system.SystemInt32Type,
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => system.SystemUInt32Type, Il2CppTypeEnum.IL2CPP_TYPE_I8 => system.SystemInt64Type,
            Il2CppTypeEnum.IL2CPP_TYPE_U8 => system.SystemUInt64Type, _ => null };
        return size != 0 && raw is { NumMods: 0, Byref: 0, Pinned: 0 } &&
            ReferenceEquals(context, canonical) && context.Type == raw.Type && context.Definition?.RawType.Type == raw.Type &&
            context.Name == context.DefaultName && context.Namespace == context.DefaultNamespace &&
            context.Attributes == context.DefaultAttributes && context.GenericParameters.Count == 0 &&
            context.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } &&
            context.Definition?.RawSizes.instance_size == size + 16;
    }
    private static void CapturePrimitive(TypeAnalysisContext type, List<object> values)
    {
        var definition = type.Definition!;
        values.Add(type.Name); values.Add(type.Namespace); values.Add(type.Attributes); values.Add(definition.Flags);
        values.Add(definition.Bitfield); values.Add(definition.RawSizes.instance_size); values.Add(definition.RawSizes.native_size);
        AddRaw(definition.RawType, values);
    }
    internal static Il2CppType? ReadUnchangedType(ApplicationAnalysisContext app, ulong address)
    {
        if (!ReadData(app, address, 16, out _))
            return null;
        var fresh = app.Binary.ReadReadableAtVirtualAddress<Il2CppType>(address);
        var cached = app.Binary.GetIl2CppTypeFromPointer(address);
        // GENERICINST's union is a rebased native pointer; primitive/VALUETYPE
        // unions are indices, and all descriptors' bitfields are scalar facts.
        var pointerUnion = fresh.Type is Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or
            Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or Il2CppTypeEnum.IL2CPP_TYPE_PTR;
        var start = pointerUnion ? address + 8 : address;
        var count = pointerUnion ? 8U : 16U;
        return Unrelocated(app, start, count) && SameRaw(fresh, cached) &&
            fresh is { NumMods: 0, Byref: 0, Pinned: 0 } ? cached : null;
    }
    private static bool SameRaw(Il2CppType first, Il2CppType second)
    {
        var a = new List<object>(); var b = new List<object>();
        AddRaw(first, a); AddRaw(second, b);
        return a.SequenceEqual(b);
    }
    internal static bool Unrelocated(ApplicationAnalysisContext app, ulong address, uint length) =>
        X64UnwindProof.ForApplication(app)?.IsUnaffectedByBaseRelocation(address, length) == true;
    internal static bool CanonicalPointer(ApplicationAnalysisContext app, ulong address, ulong value) =>
        value == 0 ? Unrelocated(app, address, 8) :
            X64UnwindProof.ForApplication(app)?.HasCanonicalPointerRelocation(address) == true;
    internal static bool WritableCacheSlot(ApplicationAnalysisContext app, ulong address) =>
        X64UnwindProof.ForApplication(app) is { } index && address >= index.ImageBase &&
        address - index.ImageBase <= uint.MaxValue &&
        index.IsWritableVirtualRangeInOneSection((uint)(address - index.ImageBase), 8);
    private static void AddRaw(Il2CppType raw, List<object> values)
    {
        if (raw.Data == null || raw.Data.Dummy != raw.Datapoint || raw.Attrs != (raw.Bits & 0xffff) ||
            (uint)raw.Type != ((raw.Bits >> 16) & 0xff) || raw.NumMods != ((raw.Bits >> 24) & 31) ||
            raw.Byref != ((raw.Bits >> 29) & 1) || raw.Pinned != ((raw.Bits >> 30) & 1) || raw.ValueType != raw.Bits >> 31)
            throw new InvalidOperationException("Changed generic storage descriptor.");
        values.Add(raw.Datapoint); values.Add(raw.Bits); values.Add(raw.Data.Dummy); values.Add(raw.Attrs);
        values.Add(raw.Type); values.Add(raw.NumMods); values.Add(raw.Byref); values.Add(raw.Pinned); values.Add(raw.ValueType);
    }
    private static void AddBytes(byte[] bytes, List<object> values)
    {
        foreach (var value in bytes)
            values.Add(value);
    }
    internal static bool ReadData(ApplicationAnalysisContext app, ulong address, uint length, out byte[] bytes)
    {
        bytes = [];
        if (app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
            length is 0 or > 1024 || address < index.ImageBase || address - index.ImageBase > uint.MaxValue ||
            address > ulong.MaxValue - length)
            return false;
        var offset = pe.MapVirtualAddressToRaw(address, false);
        var image = pe.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - length)
            return false;
        // GenericClass.cached_class makes some otherwise constant descriptor tables
        // writable. Authenticate the entire input-file range, never live process data.
        for (uint i = 0; i < length; i++)
            if (address + i - index.ImageBase > uint.MaxValue ||
                !index.IsReadableFileBackedRva((uint)(address + i - index.ImageBase)) ||
                pe.MapVirtualAddressToRaw(address + i, false) != offset + i)
                return false;
        bytes = image.Slice(checked((int)offset), checked((int)length)).ToArray();
        return true;
    }
}
