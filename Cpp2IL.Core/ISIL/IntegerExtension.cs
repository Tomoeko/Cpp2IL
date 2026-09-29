using System;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.ISIL;

/// <summary>Validated metadata and managed storage rules for explicit integer extension.</summary>
internal readonly record struct IntegerExtension(int SourceBits, int ResultBits, bool Signed)
{
    public static bool TryGet(Instruction instruction, out IntegerExtension extension)
    {
        extension = default;
        if (instruction.OpCode != OpCode.IntegerExtend || instruction.IntegerBitWidth != 0 ||
            instruction.Operands is not [LocalVariable, LocalVariable,
                Immediate { Value: 8 or 16 or 32 } source, Immediate { Value: 32 or 64 } result,
                Immediate { Value: 0 or 1 } signed])
            return false;
        extension = new((int)source.Value, (int)result.Value, signed.Value == 1);
        return true;
    }

    public bool HasCanonicalTypes(Instruction instruction, ApplicationAnalysisContext app) =>
        instruction.Operands is [LocalVariable destination, LocalVariable source, _, _, _] &&
        (StorageBits(source.Type, app.SystemTypes) >= SourceBits ||
         SourceBits == 8 && ResultBits == 32 && !Signed &&
         (ReferenceEquals(source.Type, app.SystemTypes.SystemBooleanType) ||
          IsCanonicalUnsignedByteEnum(source.Type, app.SystemTypes))) &&
        StorageBits(destination.Type, app.SystemTypes) == ResultBits;

    // An unchanged unsigned-byte enum has exactly the low eight data bits that
    // MOVZX or a closed byte-lane proof can read. Do not treat an arbitrary enum
    // name, mutable underlying type, or signed enum as canonical storage.
    internal static bool IsCanonicalUnsignedByteEnum(TypeAnalysisContext? type,
        SystemTypesContext types) =>
        type is { IsEnumType: true, IsGenericInstance: false } enumType &&
        enumType.GenericParameters.Count == 0 &&
        enumType.Definition is { IsEnumType: true, GenericContainer: null,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        enumType.Name == enumType.DefaultName &&
        enumType.Namespace == enumType.DefaultNamespace &&
        enumType.Attributes == enumType.DefaultAttributes &&
        ReferenceEquals(enumType.BaseType, enumType.DefaultBaseType) &&
        enumType.OverrideEnumUnderlyingType == null &&
        ReferenceEquals(enumType.EnumUnderlyingType, types.SystemByteType) &&
        ReferenceEquals(enumType.DefaultEnumUnderlyingType, enumType.EnumUnderlyingType) &&
        enumType.Fields.Where(field => !field.IsStatic).ToArray() is [var value] &&
        value.Name == "value__" && value.Name == value.DefaultName &&
        value.Attributes == value.DefaultAttributes &&
        value.Offset == value.DefaultOffset && value.OverrideFieldType == null &&
        ReferenceEquals(value.FieldType, types.SystemByteType) &&
        value.BackingData?.Field.RawFieldType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_U1,
                NumMods: 0, Byref: 0, Pinned: 0 };

    public static bool IsPureAndValid(Instruction instruction) =>
        TryGet(instruction, out var extension) &&
        instruction.Operands[0] is LocalVariable { Type: { } type } &&
        extension.HasCanonicalTypes(instruction, type.AppContext);

    public TypeAnalysisContext ResultType(SystemTypesContext types) => (ResultBits, Signed) switch
    {
        (32, true) => types.SystemInt32Type,
        (32, false) => types.SystemUInt32Type,
        (64, true) => types.SystemInt64Type,
        _ => types.SystemUInt64Type,
    };

    // Identity checks exclude enum storage, native integers, Boolean and same-named substitutes.
    internal static int StorageBits(TypeAnalysisContext? type, SystemTypesContext types)
    {
        if (ReferenceEquals(type, types.SystemSByteType) || ReferenceEquals(type, types.SystemByteType)) return 8;
        if (ReferenceEquals(type, types.SystemInt16Type) || ReferenceEquals(type, types.SystemUInt16Type)) return 16;
        if (ReferenceEquals(type, types.SystemInt32Type) || ReferenceEquals(type, types.SystemUInt32Type)) return 32;
        if (ReferenceEquals(type, types.SystemInt64Type) || ReferenceEquals(type, types.SystemUInt64Type)) return 64;
        return 0;
    }
}
