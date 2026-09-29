using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Checks original enum identity before using its byte or word integer storage.</summary>
internal static class EnumIntegerStorageProof
{
    internal sealed record Storage(TypeAnalysisContext Enum, TypeAnalysisContext Underlying,
        FieldAnalysisContext Backing, int Width, bool Signed);

    internal static Storage? Find(TypeAnalysisContext? type)
    {
        if (type is not { Definition: { IsEnumType: true, GenericContainer: null,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true, HasCctor: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } } definition } ||
            !type.IsValueType || type.IsGenericInstance || type.GenericParameters.Count != 0 ||
            type.OverrideEnumUnderlyingType != null || type.Name != type.DefaultName ||
            type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            definition.RawBaseType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            !ReferenceEquals(type.BaseType, type.AppContext.SystemTypes.EnumType) ||
            definition.EnumUnderlyingType is not { NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
                    Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 } raw)
            return null;
        // Test the raw kind before wrapper resolution. Unsupported descriptors
        // must not recurse through a modified or cyclic underlying type.
        var system = type.AppContext.SystemTypes;
        var expected = raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I1 => system.SystemSByteType,
            Il2CppTypeEnum.IL2CPP_TYPE_U1 => system.SystemByteType,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 => system.SystemInt16Type,
            _ => system.SystemUInt16Type,
        };
        if (!ReferenceEquals(type.EnumUnderlyingType, expected) ||
            !ReferenceEquals(type.DefaultEnumUnderlyingType, expected) ||
            type.Fields.Where(field => !field.IsStatic).ToArray() is not [var backing] ||
            backing.Name != "value__" || backing.Name != backing.DefaultName ||
            !ReferenceEquals(backing.DeclaringType, type) ||
            !ReferenceEquals(backing.BackingData?.Field.DeclaringType, definition) ||
            backing.Attributes != backing.DefaultAttributes ||
            backing.Attributes != (FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName) ||
            backing.Offset != 0 || backing.Offset != backing.DefaultOffset || backing.OverrideFieldType != null ||
            backing.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } backingRaw ||
            backingRaw.Type != raw.Type || !ReferenceEquals(backing.FieldType, expected) ||
            type.Methods.Any(method => method.Name == ".cctor"))
            return null;
        foreach (var field in type.Fields.Where(field => field.IsStatic))
            if (!ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(field.BackingData?.Field.DeclaringType, definition) ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                (field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) !=
                    (FieldAttributes.Static | FieldAttributes.Literal) || field.OverrideFieldType != null ||
                field.Offset != field.DefaultOffset || field.UseOverrideConstantValue ||
                field.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0 } literalRaw ||
                literalRaw.Data.Dummy != definition.RawType.Data.Dummy || !ReferenceEquals(field.FieldType, type))
                return null;
        return new Storage(type, expected, backing,
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 ? 8 : 16,
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_I2);
    }

    internal static bool HasWidth(TypeAnalysisContext? type, int width) => Find(type) is { } storage && storage.Width == width;

    internal static void Capture(Storage storage, List<object> values)
    {
        var type = storage.Enum;
        var definition = type.Definition!;
        values.AddRange([type, type.Name, type.Namespace, type.Attributes, type.BaseType!, storage.Underlying,
            definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Bitfield,
            definition.ByvalTypeIndex, definition.ParentIndex, definition.FirstFieldIdx, definition.FieldCount,
            type.DeclaringAssembly, (object?)type.DeclaringType ?? DBNull.Value, definition.DeclaringTypeIndex]);
        CaptureRaw(definition.RawType, values);
        CaptureRaw(definition.RawBaseType!, values);
        CaptureRaw(definition.EnumUnderlyingType!, values);
        foreach (var field in type.Fields)
        {
            values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType,
                field.BackingData!.Field.nameIndex, field.BackingData.Field.token, field.BackingData.Field.typeIndex]);
            // A boxed primitive value is immutable; retain constants without
            // replacing the original enum declaration with its storage type.
            values.Add(field.ConstantValue ?? DBNull.Value);
            CaptureRaw(field.BackingData.Field.RawFieldType!, values);
        }
    }

    internal static void CaptureRaw(Il2CppType raw, List<object> values) => values.AddRange(
        [raw.Bits, raw.Datapoint, raw.Data.Dummy, raw.Attrs, raw.Type, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType]);
}
