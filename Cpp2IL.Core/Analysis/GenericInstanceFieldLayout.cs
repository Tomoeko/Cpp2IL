using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

//Resolves field offsets on generic types, which are all 0 in the metadata.
public static class GenericInstanceFieldLayout
{
    public static FieldAnalysisContext? FindFieldAtOffset(TypeAnalysisContext definition, long targetOffset)
        => FindFieldAtOffset(definition, targetOffset, false);

    private static FieldAnalysisContext? FindFieldAtOffset(TypeAnalysisContext definition, long targetOffset,
        bool closedReferenceParameters)
    {
        if (definition is GenericInstanceTypeAnalysisContext constructed)
        {
            // Storage extent alone does not prove whether a native receiver is
            // boxed or byref, nor establish a projected field's offset identity.
            if (constructed.IsValueType)
                return null;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(constructed.AppContext) &&
                constructed.GenericArguments.Count > 0 && constructed.GenericArguments.All(argument =>
                    !argument.IsValueType && argument.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR)))
                return FindFieldAtOffset(constructed.GenericType, targetOffset, true);
            return FindClosedReferenceFieldAtOffset(constructed, targetOffset);
        }
        var pointerSize = definition.AppContext.Binary.PointerSizeBytes;

        // TODO Support anything outside the trivial case.
        for (var baseType = definition.BaseType; baseType != null; baseType = baseType.BaseType)
            if (baseType.Fields.Any(f => !f.IsStatic))
                return null;

        var offset = 2L * pointerSize;

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            var fieldType = field.FieldType;
            var storage = closedReferenceParameters && fieldType is GenericParameterTypeAnalysisContext
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_VAR } parameter && ReferenceEquals(parameter.Owner, definition)
                ? (pointerSize, pointerSize) : GetSizeAndAlignment(fieldType, pointerSize);
            if (storage is not var (size, alignment))
                return null;

            offset = (offset + alignment - 1) & ~(alignment - 1);

            if (offset == targetOffset)
                return field;

            offset += size;
        }

        return null;
    }

    internal static (long Size, long Alignment)? GetSizeAndAlignment(TypeAnalysisContext fieldType, int pointerSize)
    {
        // TODO support user-defined value types
        // A VAR is substituted before runtime field layout. Its open definition
        // does not establish reference storage or any universal pointer extent.
        if (fieldType is GenericParameterTypeAnalysisContext)
            return null;
        if (fieldType is PointerTypeAnalysisContext || !fieldType.IsValueType)
            return (pointerSize, pointerSize);

        if (fieldType is GenericInstanceTypeAnalysisContext constructed)
            return ClosedGenericValueLayoutProof.Find(constructed) is { } layout
                ? (layout.Size, layout.Alignment) : null;

        if (fieldType.IsEnumType)
            return GetEnumStorage(fieldType);

        // A value type from another assembly can use a framework-looking name.
        // Only the application's canonical primitive contexts establish these
        // storage widths; arbitrary structs still need an independent layout.
        var system = fieldType.AppContext.SystemTypes;
        if (ReferenceEquals(fieldType, system.SystemBooleanType) ||
            ReferenceEquals(fieldType, system.SystemByteType) ||
            ReferenceEquals(fieldType, system.SystemSByteType))
            return (1, 1);
        if (ReferenceEquals(fieldType, system.SystemInt16Type) ||
            ReferenceEquals(fieldType, system.SystemUInt16Type) ||
            ReferenceEquals(fieldType, system.SystemCharType))
            return (2, 2);
        if (ReferenceEquals(fieldType, system.SystemInt32Type) ||
            ReferenceEquals(fieldType, system.SystemUInt32Type) ||
            ReferenceEquals(fieldType, system.SystemSingleType))
            return (4, 4);
        if (ReferenceEquals(fieldType, system.SystemInt64Type) ||
            ReferenceEquals(fieldType, system.SystemUInt64Type) ||
            ReferenceEquals(fieldType, system.SystemDoubleType))
            return (8, 8);
        if (ReferenceEquals(fieldType, system.SystemIntPtrType) ||
            ReferenceEquals(fieldType, system.SystemUIntPtrType))
            return (pointerSize, pointerSize);
        return null;
    }

    // Preserve the existing closed reference-substitution path. A raw VAR in an
    // open definition is unknown; registered CLASS/OBJECT/STRING/array arguments
    // independently establish pointer storage for this particular instance.
    private static FieldAnalysisContext? FindClosedReferenceFieldAtOffset(
        GenericInstanceTypeAnalysisContext instance, long targetOffset)
    {
        try
        {
            var app = instance.AppContext;
            var definition = instance.GenericType;
            if (!instance.HasUnchangedOriginalRawType || instance.OriginalRawType is not { } raw ||
                !app.Binary.TryGetTypeVirtualAddress(raw, out var rawAddress) ||
                ClosedGenericValueLayoutProof.ReadUnchangedType(app, rawAddress) == null ||
                definition.IsValueType || definition.Definition is not { GenericContainer: { } container,
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true } original ||
                container.isGenericMethod || !ReferenceEquals(container.TypeOwner, original) ||
                container.genericParameterCount is < 1 or > 64 ||
                instance.GenericArguments.Count != container.genericParameterCount ||
                definition.GenericParameters.Count != container.genericParameterCount ||
                !definition.Fields.Select(field => field.BackingData?.Field).SequenceEqual(original.Fields!) ||
                !ClosedGenericValueLayoutProof.ReadData(app, raw.Data.GenericClass, 32, out _))
                return null;
            var generic = raw.GetGenericClass();
            if (!ClosedGenericValueLayoutProof.CanonicalPointer(app, rawAddress, raw.Data.GenericClass) ||
                !ClosedGenericValueLayoutProof.CanonicalPointer(app, raw.Data.GenericClass, generic.V27TypePointer) ||
                !ClosedGenericValueLayoutProof.CanonicalPointer(app, raw.Data.GenericClass + 8, generic.Context.class_inst) ||
                generic.CachedClass != 0 ||
                !ClosedGenericValueLayoutProof.Unrelocated(app, raw.Data.GenericClass + 24, 8) ||
                !ClosedGenericValueLayoutProof.WritableCacheSlot(app, raw.Data.GenericClass + 24) ||
                generic.Context.method_inst != 0 ||
                !ClosedGenericValueLayoutProof.Unrelocated(app, raw.Data.GenericClass + 16, 8) ||
                ClosedGenericValueLayoutProof.ReadUnchangedType(app, generic.V27TypePointer) is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } baseType ||
                !ReferenceEquals(baseType.AsClass(), original) ||
                !ClosedGenericValueLayoutProof.ReadData(app, generic.Context.class_inst, 16, out _) ||
                !ClosedGenericValueLayoutProof.Unrelocated(app, generic.Context.class_inst, 8))
                return null;
            var inst = generic.Context.ClassInst!;
            if (!ClosedGenericValueLayoutProof.CanonicalPointer(app, generic.Context.class_inst + 8, inst.pointerStart) ||
                inst.pointerCount != (ulong)instance.GenericArguments.Count ||
                !ClosedGenericValueLayoutProof.ReadData(app, inst.pointerStart, checked((uint)inst.pointerCount * 8), out _))
                return null;
            var pointers = inst.Pointers;
            for (var i = 0; i < pointers.Length; i++)
            {
                if (!ClosedGenericValueLayoutProof.CanonicalPointer(app, inst.pointerStart + (ulong)i * 8, pointers[i]) ||
                    ClosedGenericValueLayoutProof.ReadUnchangedType(app, pointers[i]) is not { } argument ||
                    argument.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                        Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_ARRAY) ||
                    instance.GenericArguments[i].Type != argument.Type || instance.GenericArguments[i].IsValueType ||
                    !HasFiniteDescriptor(app, pointers[i], new HashSet<ulong>(), 0))
                    return null;
                var canonical = app.ResolveIl2CppType(argument);
                var supplied = instance.GenericArguments[i];
                if (argument.Type is Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY)
                {
                    if (canonical == null || !SameReferenceArgument(supplied, canonical))
                        return null;
                }
                else if (!ReferenceEquals(supplied, canonical))
                    return null;
            }
            var seen = new HashSet<TypeAnalysisContext>();
            for (var type = definition; type != null; type = type.BaseType)
            {
                if (!seen.Add(type) || type.Definition?.RawBaseType is { } parent && parent.Type is not
                        (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT) ||
                    !ReferenceEquals(type, definition) && type.Fields.Any(field => !field.IsStatic))
                    return null;
            }
            var pointerSize = app.Binary.PointerSizeBytes;
            var offset = 2L * pointerSize;
            FieldAnalysisContext? result = null;
            foreach (var field in definition.Fields)
            {
                if (field.IsStatic)
                    continue;
                var fieldRaw = field.BackingData?.Field.RawFieldType;
                if (fieldRaw is not { NumMods: 0, Byref: 0, Pinned: 0 } || field.OverrideFieldType != null)
                    return null;
                (long Size, long Alignment)? storage;
                if (fieldRaw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VAR)
                {
                    var parameter = fieldRaw.GetGenericParameterDef();
                    if (!ReferenceEquals(parameter.Owner, container) || parameter.genericParameterIndexInOwner >= pointers.Length ||
                        !ReferenceEquals(field.FieldType, definition.GenericParameters[parameter.genericParameterIndexInOwner]))
                        return null;
                    storage = (pointerSize, pointerSize);
                }
                else if (fieldRaw.Type is Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or
                    Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or Il2CppTypeEnum.IL2CPP_TYPE_MVAR)
                    return null;
                else
                    storage = GetSizeAndAlignment(field.FieldType, pointerSize);
                if (storage is not { } sizes)
                    return null;
                offset = checked((offset + sizes.Alignment - 1) & -sizes.Alignment);
                if (offset == targetOffset)
                    result = field;
                offset = checked(offset + sizes.Size);
            }
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static bool HasFiniteDescriptor(ApplicationAnalysisContext app, ulong pointer, HashSet<ulong> seen, int depth)
    {
        if (depth > 16 || !seen.Add(pointer) || ClosedGenericValueLayoutProof.ReadUnchangedType(app, pointer) is not { } raw)
            return false;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY)
            return ClosedGenericValueLayoutProof.CanonicalPointer(app, pointer, raw.Data.Type) &&
                HasFiniteDescriptor(app, raw.Data.Type, seen, depth + 1);
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_ARRAY)
        {
            if (!ClosedGenericValueLayoutProof.ReadData(app, raw.Data.Array, 32, out _))
                return false;
            var array = raw.GetArrayType();
            return ClosedGenericValueLayoutProof.CanonicalPointer(app, pointer, raw.Data.Array) &&
                ClosedGenericValueLayoutProof.CanonicalPointer(app, raw.Data.Array, array.etype) &&
                ClosedGenericValueLayoutProof.Unrelocated(app, raw.Data.Array + 8, 24) &&
                array.rank is > 0 and <= 32 && array.numsizes == 0 && array.numlobounds == 0 &&
                HasFiniteDescriptor(app, array.etype, seen, depth + 1);
        }
        return raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING or
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I1 or
            Il2CppTypeEnum.IL2CPP_TYPE_U1 or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_I8 or
            Il2CppTypeEnum.IL2CPP_TYPE_U8 or Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8;
    }

    internal static bool SameReferenceArgument(TypeAnalysisContext supplied, TypeAnalysisContext canonical, int depth = 0)
    {
        if (depth > 16 || supplied.Type != canonical.Type)
            return false;
        if (supplied is WrappedTypeAnalysisContext || canonical is WrappedTypeAnalysisContext)
        {
            if (supplied is not WrappedTypeAnalysisContext first || canonical is not WrappedTypeAnalysisContext second ||
                supplied.GetType() != canonical.GetType() ||
                supplied is ArrayTypeAnalysisContext array && canonical is ArrayTypeAnalysisContext expected && array.Rank != expected.Rank)
                return false;
            return SameReferenceArgument(first.ElementType, second.ElementType, depth + 1);
        }
        // Namespace/name equality cannot distinguish elements from different assemblies.
        return ReferenceEquals(supplied, canonical);
    }

    private static (long Size, long Alignment)? GetEnumStorage(TypeAnalysisContext type)
    {
        // Resolve only an original canonical integer backing. A mutated enum
        // backing can otherwise recurse through itself before layout rejects it.
        if (type.Definition is not { IsEnumType: true, EnumUnderlyingType:
                { NumMods: 0, Byref: 0, Pinned: 0 } raw } definition ||
            type.OverrideEnumUnderlyingType != null ||
            type.Fields.Where(field => !field.IsStatic).ToArray() is not [var backing] ||
            backing.Name != "value__" || backing.OverrideFieldType != null ||
            !ReferenceEquals(backing.DeclaringType, type) ||
            !ReferenceEquals(backing.BackingData?.Field.DeclaringType, definition) ||
            backing.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } backingRaw || backingRaw.Type != raw.Type)
            return null;
        var system = type.AppContext.SystemTypes;
        var primitive = raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I1 => system.SystemSByteType,
            Il2CppTypeEnum.IL2CPP_TYPE_U1 => system.SystemByteType,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 => system.SystemInt16Type,
            Il2CppTypeEnum.IL2CPP_TYPE_U2 => system.SystemUInt16Type,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => system.SystemInt32Type,
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => system.SystemUInt32Type,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 => system.SystemInt64Type,
            Il2CppTypeEnum.IL2CPP_TYPE_U8 => system.SystemUInt64Type,
            _ => null,
        };
        if (primitive == null || !ReferenceEquals(type.EnumUnderlyingType, primitive) ||
            !ReferenceEquals(backing.FieldType, primitive))
            return null;
        var size = raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => 4,
            _ => 8,
        };
        return (size, size);
    }
}
