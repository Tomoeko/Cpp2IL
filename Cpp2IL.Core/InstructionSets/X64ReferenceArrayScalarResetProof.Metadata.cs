using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ReferenceArrayScalarResetProof
{
    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } raw } &&
        (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS || raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
            ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType)) &&
        HasOriginalBaseDescriptor(type) &&
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance && type.GenericParameters.Count == 0 &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace && type.Attributes == type.DefaultAttributes &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) && HasOriginalEnclosingChain(type) &&
        type.Fields.Count == type.Definition.FieldCount &&
        type.Fields.Select(field => field.BackingData?.Field).SequenceEqual(type.Definition.Fields!) &&
        type.Methods.Count == type.Definition.MethodCount &&
        type.Methods.Select(method => method.Definition).SequenceEqual(type.Definition.Methods!);

    private static bool HasOriginalBaseDescriptor(TypeAnalysisContext type)
    {
        var definition = type.Definition!;
        if (ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType))
            return definition.ParentIndex.IsNull && definition.RawBaseType == null;
        return definition.RawBaseType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                Data: not null, NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool HasOriginalEnclosingChain(TypeAnalysisContext type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.DeclaringType)
            if (!visited.Add(current) || current.Definition is not { } definition || current.IsGenericInstance ||
                current.GenericParameters.Count != 0 || !ReferenceEquals(current.DeclaringType?.Definition, definition.DeclaringType) ||
                current.DeclaringType == null && !definition.DeclaringTypeIndex.IsNull ||
                current.DeclaringType is { } parent &&
                (!ReferenceEquals(parent.DeclaringAssembly, current.DeclaringAssembly) ||
                 parent.NestedTypes.Count(member => ReferenceEquals(member, current)) != 1))
                return false;
        return true;
    }

    internal static bool AccessibleElement(TypeAnalysisContext caller, TypeAnalysisContext element)
    {
        if (!HasOriginalEnclosingChain(caller) || !HasOriginalEnclosingChain(element) ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringAssembly, element.DeclaringAssembly))
            return false;
        var sameAssembly = ReferenceEquals(caller.DeclaringAssembly, element.DeclaringAssembly);
        var callerBodies = new HashSet<TypeAnalysisContext>();
        for (var current = caller; current != null; current = current.DeclaringType) callerBodies.Add(current);
        for (var current = element; current != null; current = current.DeclaringType)
        {
            if (callerBodies.Contains(current)) return true;
            var access = current.Visibility;
            if (current.DeclaringType == null)
            {
                if (access != TypeAttributes.Public && !(sameAssembly && access == TypeAttributes.NotPublic)) return false;
            }
            else if (access != TypeAttributes.NestedPublic &&
                     !(sameAssembly && access is TypeAttributes.NestedAssembly or TypeAttributes.NestedFamORAssem) &&
                     !(sameAssembly && callerBodies.Contains(current.DeclaringType) && access is
                         TypeAttributes.NestedPrivate or TypeAttributes.NestedFamily or TypeAttributes.NestedFamANDAssem))
                return false;
        }
        // Protected access through an unrelated derived body requires a separate proof.
        return true;
    }

    private static bool CaptureChain(TypeAnalysisContext type, List<object> values, HashSet<TypeAnalysisContext> seen)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!visited.Add(current) || !OrdinaryClass(current)) return false;
            if (!seen.Add(current)) continue;
            var definition = current.Definition!;
            values.AddRange([current, current.Name, current.Namespace, current.Attributes, current.BaseType!,
                current.DeclaringAssembly, (object?)current.DeclaringType ?? DBNull.Value, definition.DeclaringTypeIndex,
                definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Flags, definition.Bitfield,
                definition.ByvalTypeIndex, definition.ParentIndex, definition.GenericContainerIndex, definition.FirstFieldIdx,
                definition.FieldCount, definition.FirstMethodIdx, definition.MethodCount, definition.RawSizes.instance_size,
                definition.RawSizes.native_size, definition.RawSizes.static_fields_size, definition.RawSizes.thread_static_fields_size]);
            X64SmallAggregateFieldGetterProof.CaptureRawType(definition.RawType, values);
            if (definition.RawBaseType is { } rawBase)
                X64SmallAggregateFieldGetterProof.CaptureRawType(rawBase, values);
            foreach (var member in current.Methods)
            {
                if (member.Definition is not { } original || member.Name != member.DefaultName ||
                    member.Attributes != member.DefaultAttributes || member.ImplAttributes != member.DefaultImplAttributes ||
                    !ReferenceEquals(member.DeclaringType, current) || !ReferenceEquals(original.DeclaringType, definition))
                    return false;
                values.AddRange([member, member.Name, member.Attributes, member.ImplAttributes, member.UnderlyingPointer,
                    original.nameIndex, original.token, original.flags, original.iflags, original.declaringTypeIdx,
                    original.returnTypeIdx, original.parameterStart, original.parameterCount, original.genericContainerIndex]);
            }
            foreach (var field in current.Fields)
            {
                if (field.BackingData?.Field is not { RawFieldType: { } raw } original ||
                    field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                    field.Offset != field.DefaultOffset || field.OverrideFieldType != null ||
                    !ReferenceEquals(field.DeclaringType, current) || !ReferenceEquals(original.DeclaringType, definition) ||
                    !CaptureRaw(raw, values, new HashSet<Il2CppType>(), 0))
                    return false;
                values.AddRange([field, field.Name, field.Attributes, field.Offset, original.nameIndex,
                    original.token, original.typeIndex]);
            }
            // Array wrapper allocations are not identities. Raw chains above retain
            // every array layer; retain only the canonical final storage context.
            foreach (var field in current.Fields)
            {
                var storage = field.FieldType;
                while (storage is SzArrayTypeAnalysisContext array) storage = array.ElementType;
                values.AddRange([storage, storage.Name, storage.Namespace, storage.Attributes, storage.Type]);
                if (storage.Definition is { RawType.Data: not null } stored)
                {
                    values.AddRange([stored.NameIndex, stored.NamespaceIndex, stored.Flags, stored.Bitfield,
                        stored.RawSizes.instance_size, stored.RawSizes.native_size,
                        stored.RawSizes.static_fields_size, stored.RawSizes.thread_static_fields_size]);
                    X64SmallAggregateFieldGetterProof.CaptureRawType(stored.RawType, values);
                }
            }
            if (current.DeclaringType is { } enclosing && !CaptureChain(enclosing, values, seen)) return false;
        }
        return visited.Contains(type.AppContext.SystemTypes.SystemObjectType);
    }

    private static bool CaptureRaw(Il2CppType raw, List<object> values, HashSet<Il2CppType> visited, int depth)
    {
        if (depth > 16 || raw.Data == null || !visited.Add(raw) || raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0 ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or
                Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_BYREF or
                Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR)
            return false;
        X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
        return raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY || CaptureRaw(raw.GetEncapsulatedType(), values, visited, depth + 1);
    }
}
