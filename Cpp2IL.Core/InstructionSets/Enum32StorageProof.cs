using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Checks the managed identity and four-byte storage of an unchanged enum.</summary>
internal static class Enum32StorageProof
{
    internal static bool IsUnchanged(TypeAnalysisContext element)
    {
        if (element.Definition is not
                { IsEnumType: true, GenericContainer: null,
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !element.IsValueType || element.IsGenericInstance ||
            element.GenericParameters.Count != 0 ||
            element.OverrideEnumUnderlyingType != null ||
            element.Name != element.DefaultName ||
            element.Namespace != element.DefaultNamespace ||
            element.Attributes != element.DefaultAttributes ||
            (element.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            !ReferenceEquals(element.BaseType, element.DefaultBaseType) ||
            !ReferenceEquals(element.BaseType, element.AppContext.SystemTypes.EnumType))
            return false;

        var underlying = element.EnumUnderlyingType;
        var system = element.AppContext.SystemTypes;
        var rawKind = ReferenceEquals(underlying, system.SystemInt32Type)
            ? Il2CppTypeEnum.IL2CPP_TYPE_I4
            : ReferenceEquals(underlying, system.SystemUInt32Type)
                ? Il2CppTypeEnum.IL2CPP_TYPE_U4 : Il2CppTypeEnum.IL2CPP_TYPE_END;
        if (rawKind == Il2CppTypeEnum.IL2CPP_TYPE_END ||
            !ReferenceEquals(underlying, element.DefaultEnumUnderlyingType) ||
            definition.EnumUnderlyingType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } rawUnderlying ||
            rawUnderlying.Type != rawKind)
            return false;

        // Native storage alone does not bind an enum. Preserve the enum identity
        // and its unique runtime backing field before using typed ldelem.
        var backing = element.Fields.Where(field => !field.IsStatic).ToArray();
        return backing is [{ } value] && value.Name == "value__" &&
               value.Name == value.DefaultName &&
               ReferenceEquals(value.DeclaringType, element) &&
               ReferenceEquals(value.BackingData?.Field.DeclaringType, definition) &&
               value.Attributes == value.DefaultAttributes &&
               value.Attributes == (FieldAttributes.Public | FieldAttributes.SpecialName |
                                    FieldAttributes.RTSpecialName) &&
               value.Offset == 0 && value.Offset == value.DefaultOffset &&
               value.OverrideFieldType == null &&
               ReferenceEquals(value.FieldType, underlying) &&
               value.BackingData?.Field.RawFieldType is
                   { NumMods: 0, Byref: 0, Pinned: 0 } rawBacking &&
               rawBacking.Type == rawKind;
    }
}
