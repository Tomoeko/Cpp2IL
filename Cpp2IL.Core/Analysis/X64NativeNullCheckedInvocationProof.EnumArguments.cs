using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using NativeRegister = Iced.Intel.Register;
using NativeSource = Cpp2IL.Core.Analysis.X64NativeInvocationValues.Source;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private sealed record EnumArgument(TypeAnalysisContext Type);

    // This predicate only selects the argument route. Admission and saved
    // freshness additionally bind the complete original enum declaration.
    internal static bool IsSignedEnumArgumentType(TypeAnalysisContext type) =>
        type.Definition is { IsEnumType: true, RawType.Data: not null, RawBaseType.Data: not null,
            EnumUnderlyingType: { Data: not null, Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 } } &&
        type.Type == Il2CppTypeEnum.IL2CPP_TYPE_ENUM && Enum32StorageProof.IsUnchanged(type) &&
        ReferenceEquals(type.EnumUnderlyingType, type.AppContext.SystemTypes.SystemInt32Type);

    // Selecting this pure setup does not admit the invocation. The complete
    // proof must still bind the value to its original parameter and native bits.
    internal static bool IsEnumArgumentSetup(MethodAnalysisContext caller, Instruction instruction) =>
        instruction is { NativeAddress: not null, OpCode: OpCode.Add or OpCode.Subtract,
            IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
            Operands: [LocalVariable { Type: { } type } destination, Immediate { Value: 0 },
                Immediate { Value: >= int.MinValue and <= int.MaxValue }] } &&
        !caller.ParameterLocals.Contains(destination) && !Escaped(caller, destination) &&
        TryEnumDeclaration(type, out _);

    private static bool OriginalEnumParameter(MethodAnalysisContext method, int index)
    {
        if (index < 0 || index >= method.Parameters.Count || method.Definition is not { } definition ||
            !X64OriginalReferenceClassProof.OriginalMethod(method.AppContext, definition)) return false;
        var parameter = method.Parameters[index];
        var type = parameter.ParameterType;
        return IsSignedEnumArgumentType(type) && parameter.ParameterIndex == index &&
               ReferenceEquals(parameter.DeclaringMethod, method) && !parameter.IsRef &&
               parameter.OverrideParameterType == null && !parameter.UseOverrideDefaultValue &&
               parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes &&
               (parameter.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) == 0 &&
               ReferenceEquals(type, parameter.DefaultParameterType) &&
               parameter.Definition is { RawType: { } raw } original &&
               definition.InternalParameterData is { } originals && originals.Length == method.Parameters.Count &&
               ReferenceEquals(originals[index], original) &&
               X64OriginalReferenceClassProof.OriginalParameter(method.AppContext, definition, index, original) &&
               EnumDescriptor(type, raw);
    }

    private static bool EnumDescriptor(TypeAnalysisContext type, Il2CppType raw) =>
        raw is { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, Data: not null,
            NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } &&
        type.Definition is { TypeIndex.Value: >= 0 } definition &&
        raw.Datapoint == (ulong)definition.TypeIndex.Value && raw.Data.Dummy == raw.Datapoint &&
        X64OriginalReferenceClassProof.RetainedDescriptor(type.AppContext, raw) &&
        ReferenceEquals(type.AppContext.ResolveIl2CppType(raw), type);

    private static bool TryEnumArgument(MethodAnalysisContext caller, IOperand operand, Instruction use,
        TypeAnalysisContext type, out NativeSource source, out Argument argument)
    {
        source = default;
        argument = default;
        if (!IsSignedEnumArgumentType(type)) return false;
        var visited = new HashSet<LocalVariable>();
        while (operand is LocalVariable local && visited.Add(local))
        {
            if (Escaped(caller, local) || !ReachingDefinition(caller, local, use, out var definition)) return false;
            if (definition == null)
            {
                if (!ReferenceEquals(local.Type, type) || !EntryIndex(caller, local, out var index) ||
                    !OriginalEnumParameter(caller, index) || !Incoming(caller, index, type, out var register)) return false;
                source = new(register);
                argument = new(index, null, Enum: new(type));
                return true;
            }
            // Native literal materialization can retain an I4 temporary. It
            // does not establish a conversion from a different enum or U4.
            var literalTemporary = ReferenceEquals(local.Type, type) ||
                ReferenceEquals(local.Type, caller.AppContext.SystemTypes.SystemInt32Type);
            if (literalTemporary && definition is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, Operands: [LocalVariable literalDestination, Immediate literalValue] } &&
                ReferenceEquals(literalDestination, local)) operand = literalValue;
            else if (ReferenceEquals(local.Type, type) && definition is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                         CallSemantics: CallSemantics.Direct, Operands: [LocalVariable copyDestination, LocalVariable copied] } &&
                     ReferenceEquals(copyDestination, local) && ReferenceEquals(copied.Type, type)) operand = copied;
            else if (literalTemporary && definition is { OpCode: OpCode.Add or OpCode.Subtract, IntegerBitWidth: 32,
                         CallSemantics: CallSemantics.Direct, Operands: [LocalVariable arithmeticDestination,
                             Immediate { Value: 0 }, Immediate { Value: >= int.MinValue and <= int.MaxValue } constant] } &&
                     ReferenceEquals(arithmeticDestination, local))
                operand = new Immediate(definition.OpCode == OpCode.Add ? constant.Value : unchecked(-(int)constant.Value));
            else return false;
            use = definition;
        }
        if (operand is not Immediate { Value: >= int.MinValue and <= int.MaxValue } literal) return false;
        // Every I4 bit pattern is a valid enum value, including unnamed values.
        source = new(NativeRegister.None, Literal: literal.UnsignedValue);
        argument = new(null, literal.Value, Enum: new(type));
        return true;
    }

    private static bool TryEnumInvocationDeclarations(MethodAnalysisContext caller, MethodAnalysisContext target,
        out ValueKey? declarations)
    {
        declarations = null;
        if (!caller.AppContext.MethodsByAddress.TryGetValue(caller.UnderlyingPointer, out var aliases)) return false;
        var methods = aliases.Append(target).Distinct().ToArray();
        var enums = new Dictionary<TypeAnalysisContext, ValueKey>();
        var parameters = new List<ValueKey>();
        foreach (var method in methods)
            for (var index = 0; index < method.Parameters.Count; index++)
            {
                var parameter = method.Parameters[index];
                var type = parameter.ParameterType;
                if (type.Type != Il2CppTypeEnum.IL2CPP_TYPE_ENUM) continue;
                if (!OriginalEnumParameter(method, index)) return false;
                if (!enums.TryGetValue(type, out var enumKey))
                {
                    if (!TryEnumDeclaration(type, out enumKey)) return false;
                    enums.Add(type, enumKey);
                }
                var original = parameter.Definition!;
                parameters.Add(new("enum-parameter", parameter,
                    [new("method", method, []), new("definition", original, []), new("index", index, []),
                        new("name", parameter.Name, []), new("attributes", parameter.Attributes, []),
                        new("name-index", original.nameIndex, []), new("token", original.token, []),
                        new("type-index", original.typeIndex.Value, []), ReferenceRawTypeKey(original.RawType), enumKey]));
            }
        if (parameters.Count != 0) declarations = new("enum-invocation-declarations", caller, parameters.ToArray());
        return true;
    }

    private static bool EnumInvocationDeclarationsRetained(MethodAnalysisContext caller, MethodAnalysisContext target,
        ValueKey? retained) => TryEnumInvocationDeclarations(caller, target, out var current) &&
        (retained == null ? current == null : current != null && SameKey(retained, current));

    private static bool TryEnumDeclaration(TypeAnalysisContext type, out ValueKey key)
    {
        key = null!;
        var app = type.AppContext;
        if (!IsSignedEnumArgumentType(type) || type.Definition is not { HasCctor: false } definition ||
            app.MetadataVersion != 29 || app.Binary.PointerSizeBytes != sizeof(ulong) ||
            !X64OriginalReferenceClassProof.OriginalType(app, definition) ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(type.DeclaringAssembly) ||
            !EnumDescriptor(type, definition.RawType) ||
            definition.RawBaseType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, ValueType: 0,
                NumMods: 0, Byref: 0, Pinned: 0 } rawBase ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, rawBase) ||
            !ReferenceEquals(app.ResolveIl2CppType(rawBase), app.SystemTypes.EnumType) ||
            definition.EnumUnderlyingType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, ValueType: 1,
                NumMods: 0, Byref: 0, Pinned: 0 } rawUnderlying ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, rawUnderlying) ||
            !ReferenceEquals(app.ResolveIl2CppType(rawUnderlying), app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(definition.DeclaringType, type.DeclaringType?.Definition) ||
            !type.DeclaringAssembly.Types.Contains(type) || type.Fields.Count != definition.FieldCount ||
            definition.Fields is not { } originalFields || !originalFields.SequenceEqual(type.Fields.Select(field => field.BackingData?.Field)) ||
            type.Methods.Count != definition.MethodCount || definition.Methods is not { } originalMethods ||
            !originalMethods.SequenceEqual(type.Methods.Select(method => method.Definition)) ||
            type.Methods.Any(method => method.Name == ".cctor") ||
            !EnumOriginalString(app.Metadata, definition.NameIndex, type.Name) ||
            !EnumOriginalString(app.Metadata, definition.NamespaceIndex, type.Namespace)) return false;
        var enclosing = new List<ValueKey>();
        var visited = new HashSet<TypeAnalysisContext>();
        for (var owner = type.DeclaringType; owner != null; owner = owner.DeclaringType)
        {
            if (!visited.Add(owner) || !ReferenceDeclarationKey(owner, out var declaration) ||
                owner.Definition is not { } original || !X64OriginalReferenceClassProof.OriginalType(app, original) ||
                owner.NestedTypes.Count(nested => ReferenceEquals(nested, type)) != (ReferenceEquals(owner, type.DeclaringType) ? 1 : 0))
                return false;
            enclosing.Add(declaration);
        }
        var fields = new List<ValueKey>();
        for (var index = 0; index < type.Fields.Count; index++)
        {
            var field = type.Fields[index];
            if (field.BackingData is not { Field: { RawFieldType: { } raw } original } backing ||
                backing.IndexInParent != index || !ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(original.DeclaringType, definition) || !X64OriginalReferenceClassProof.OriginalField(app, original) ||
                !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
                field.Name != field.DefaultName || !EnumOriginalString(app.Metadata, original.nameIndex, field.Name) ||
                field.Attributes != field.DefaultAttributes || field.Attributes != (FieldAttributes)raw.Attrs ||
                field.OverrideFieldType != null || field.UseOverrideConstantValue || field.OverrideStaticArrayInitialValue != null ||
                field.Offset != field.DefaultOffset) return false;
            ValueKey? constant = null;
            if (field.IsStatic)
            {
                if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault)) !=
                        (FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault) ||
                    !EnumDescriptor(type, raw) || !ReferenceEquals(field.FieldType, type) ||
                    !TryEnumConstant(field, out var constantKey)) return false;
                constant = constantKey;
            }
            else if (field.Name != "value__" || field.Offset != 0 || raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
                     raw.ValueType != 1 || raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0 ||
                     !ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt32Type) ||
                     original.DefaultValue != null) return false;
            fields.Add(new("enum-field", field,
                [new("definition", original, []), new("ordinal", index, []), new("name", field.Name, []),
                    new("attributes", field.Attributes, []), new("offset", field.Offset, []), new("type", field.FieldType, []),
                    new("name-index", original.nameIndex, []), new("type-index", original.typeIndex.Value, []),
                    new("token", original.token, []), ReferenceRawTypeKey(raw), constant ?? new("no-constant", null, [])]));
        }
        // Bind boxed native value__ to unboxed managed offset zero and retain
        // every native slot and size, including nonstorage literal slots.
        if (!X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(type, out var layout, true)) return false;
        key = new("signed-i4-enum", type,
            [ReferenceDeclarationFacts(type), new("underlying", type.EnumUnderlyingType, []),
                new("field-start", definition.FirstFieldIdx.Value, []), new("field-count", definition.FieldCount, []),
                new("method-start", definition.FirstMethodIdx.Value, []), new("method-count", definition.MethodCount, []),
                ReferenceRawTypeKey(rawUnderlying), new("native-layout", Convert.ToBase64String(layout), []),
                new("metadata-sections", null, EnumMetadataSections(app.Metadata)),
                new("fields", null, fields.ToArray()), new("enclosing", null, enclosing.ToArray())]);
        return true;
    }

    private static bool TryEnumConstant(FieldAnalysisContext field, out ValueKey key)
    {
        key = null!;
        var app = field.AppContext;
        var metadata = app.Metadata;
        if (field.BackingData?.Field is not { DefaultValue: { } cached } original ||
            field.ConstantValue is not int constant || cached.fieldIndex.Value != original.FieldIndex.Value ||
            cached.typeIndex.Value < 0 || cached.dataIndex.Value < 0) return false;
        var header = metadata.ReadReadable<Il2CppGlobalMetadataHeader>(0);
        var section = header.fieldDefaultValues;
        var data = header.fieldAndParameterDefaultValueData;
        if (!EnumOriginalSection(metadata, section, metadata.metadataHeader.fieldDefaultValues) ||
            !EnumOriginalSection(metadata, data, metadata.metadataHeader.fieldAndParameterDefaultValueData) ||
            section.Size % (3 * sizeof(int)) != 0 || section.Size / (3 * sizeof(int)) > 1_000_000 ||
            cached.dataIndex.Value >= data.Size) return false;
        var rows = metadata.ReadClassArrayAtRawAddr<int>(section.Offset, section.Size / sizeof(int));
        var ordinal = -1;
        for (var index = 0; index < rows.Length; index += 3)
            if (rows[index] == original.FieldIndex.Value)
            {
                if (ordinal >= 0 || rows[index + 1] != cached.typeIndex.Value || rows[index + 2] != cached.dataIndex.Value)
                    return false;
                ordinal = index / 3;
            }
        if (ordinal < 0 || app.Binary.GetType(cached.typeIndex) is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } raw ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            !ReferenceEquals(app.ResolveIl2CppType(raw), app.SystemTypes.SystemInt32Type)) return false;
        var offset = data.Offset + (long)cached.dataIndex.Value;
        if (metadata.ReadUnityCompressedIntAtRawAddr(offset, out var bytesRead) != constant ||
            bytesRead is < 1 or > 5 || bytesRead > data.Size - cached.dataIndex.Value) return false;
        key = new("enum-constant", cached,
            [new("row", ordinal, []), new("field-index", cached.fieldIndex.Value, []),
                new("type-index", cached.typeIndex.Value, []), new("data-index", cached.dataIndex.Value, []),
                new("value", constant, []), ReferenceRawTypeKey(raw),
                new("data", Convert.ToBase64String(metadata.ReadByteArrayAtRawAddress(offset, bytesRead)), [])]);
        return true;
    }

    private static bool EnumOriginalString(Il2CppMetadata metadata, int index, string value)
    {
        var header = metadata.ReadReadable<Il2CppGlobalMetadataHeader>(0);
        if (!EnumOriginalSection(metadata, header.@string, metadata.metadataHeader.@string) ||
            index < 0 || index >= header.@string.Size) return false;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length >= header.@string.Size - index) return false;
        var original = metadata.ReadByteArrayAtRawAddress(header.@string.Offset + (long)index, bytes.Length + 1);
        return original[^1] == 0 && original.AsSpan(0, bytes.Length).SequenceEqual(bytes);
    }

    private static ValueKey[] EnumMetadataSections(Il2CppMetadata metadata) =>
        new[] { metadata.metadataHeader.@string, metadata.metadataHeader.typeDefinitions,
            metadata.metadataHeader.fields, metadata.metadataHeader.fieldDefaultValues,
            metadata.metadataHeader.fieldAndParameterDefaultValueData }.Select(section =>
            new ValueKey("metadata-section", null, [new("offset", section.Offset, []), new("size", section.Size, [])])).ToArray();

    private static bool EnumOriginalSection(Il2CppMetadata metadata, Il2CppGlobalMetadataSectionHeader original,
        Il2CppGlobalMetadataSectionHeader current) => original.Offset == current.Offset && original.Size == current.Size &&
        original.Offset >= 0 && original.Size >= 0 && original.Offset <= metadata.Length - original.Size;
}
