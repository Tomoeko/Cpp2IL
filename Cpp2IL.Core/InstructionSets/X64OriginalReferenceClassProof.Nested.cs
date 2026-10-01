using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64OriginalReferenceClassProof
{
    // This deliberately separate entry point admits original ordinary nested
    // declarations. Existing top-level proof consumers keep their prior policy.
    internal static bool TryGetNestedOrdinaryClosure(TypeAnalysisContext type,
        out TypeAnalysisContext[] closure)
    {
        closure = [];
        var app = type.AppContext;
        var complete = new HashSet<TypeAnalysisContext>();
        var active = new HashSet<TypeAnalysisContext>();
        var order = new List<TypeAnalysisContext>();
        if (!Visit(type)) return false;
        closure = order.ToArray();
        return true;

        bool Visit(TypeAnalysisContext current)
        {
            if (complete.Contains(current)) return true;
            if (!active.Add(current) || order.Count >= 128 || !ReferenceEquals(current.AppContext, app) ||
                current.Definition is not { GenericContainerIndex: { IsNull: true },
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true } definition ||
                !OriginalType(app, definition) || !ValidTypeIndex(app, definition.ByvalTypeIndex.Value) ||
                !definition.ParentIndex.IsNull && !ValidTypeIndex(app, definition.ParentIndex.Value) ||
                !ReferenceEquals(ResolveClass(app, definition.RawType), current) ||
                current.IsValueType || current.IsInterface || current.IsGenericInstance || current.GenericParameters.Count != 0 ||
                current.Name != current.DefaultName || current.Namespace != current.DefaultNamespace ||
                current.Attributes != current.DefaultAttributes || current.OverrideBaseType != null ||
                (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                !CanonicalAssembly(current.DeclaringAssembly) ||
                !HasLifecycleEligibility(type, current, false) || !ConstantMembers(current, false) ||
                !OriginalNestedMembership(current) || !TryOriginalEnclosing(current, out var enclosing))
                return false;
            order.Add(current);
            if (enclosing != null && !Visit(enclosing)) return false;
            if (ReferenceEquals(current, app.SystemTypes.SystemObjectType))
            {
                if (!definition.ParentIndex.IsNull || definition.RawBaseType != null || enclosing != null) return false;
            }
            else if (ResolveClass(app, definition.RawBaseType) is not { } parent ||
                     !ReferenceEquals(current.BaseType, parent) || !Visit(parent))
                return false;
            active.Remove(current);
            complete.Add(current);
            return true;
        }
    }

    private static bool TryOriginalEnclosing(TypeAnalysisContext type, out TypeAnalysisContext? enclosing)
    {
        enclosing = null;
        var definition = type.Definition!;
        if (definition.DeclaringTypeIndex.IsNull)
            return type.DeclaringType == null && (type.Attributes & TypeAttributes.VisibilityMask) is
                TypeAttributes.Public or TypeAttributes.NotPublic;
        if (!ValidTypeIndex(type.AppContext, definition.DeclaringTypeIndex.Value) ||
            ResolveClass(type.AppContext, type.AppContext.Binary.GetType(definition.DeclaringTypeIndex)) is not { } owner ||
            !ReferenceEquals(owner, type.DeclaringType) || !ReferenceEquals(owner.DeclaringAssembly, type.DeclaringAssembly) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            owner.NestedTypes.Count(member => ReferenceEquals(member, type)) != 1 ||
            (type.Attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NotPublic)
            return false;
        enclosing = owner;
        return true;
    }

    private static bool OriginalNestedMembership(TypeAnalysisContext type)
    {
        var app = type.AppContext;
        var definition = type.Definition!;
        var originalHeader = app.Metadata.ReadReadable<Il2CppGlobalMetadataHeader>(0);
        var original = originalHeader.nestedTypes;
        var section = app.Metadata.metadataHeader.nestedTypes;
        if (app.MetadataVersion != 29 || section.Offset != original.Offset || section.Size != original.Size ||
            section.Offset < 0 || section.Size < 0 || section.Size % sizeof(uint) != 0 ||
            section.Offset > app.Metadata.Length - section.Size ||
            type.NestedTypes.Count != definition.NestedTypeCount)
            return false;
        if (definition.NestedTypeCount == 0) return true;
        var start = definition.NestedTypesStart.Value;
        var count = section.Size / sizeof(uint);
        if (start < 0 || start > count - definition.NestedTypeCount) return false;
        var members = new HashSet<TypeAnalysisContext>();
        for (ushort ordinal = 0; ordinal < definition.NestedTypeCount; ordinal++)
        {
            var index = app.Metadata.GetNestedTypeIndicesFromOffset(definition.NestedTypesStart, ordinal).Value;
            if (index < 0 || index >= app.Metadata.TypeDefinitionCount ||
                !OriginalRow(app, section, start + ordinal, count, [unchecked((uint)index)]) ||
                type.NestedTypes[ordinal] is not { Definition: { } childDefinition } child ||
                !members.Add(child) || !ReferenceEquals(app.Metadata.typeDefs[index], childDefinition) ||
                !OriginalType(app, childDefinition) || !ReferenceEquals(child.AppContext, app) ||
                !ReferenceEquals(child.DeclaringAssembly, type.DeclaringAssembly) ||
                !ValidTypeIndex(app, childDefinition.ByvalTypeIndex.Value) ||
                !ReferenceEquals(ResolveClass(app, childDefinition.RawType), child) ||
                !TryOriginalEnclosing(child, out var owner) || !ReferenceEquals(owner, type))
                return false;
        }
        return true;
    }

    // Bind layout cache consumers to their own file-backed registration slots.
    // This proves current input values, not runtime immutability or the initial
    // provenance of an unused pointer table's individual payloads.
    internal static bool OriginalInstanceFieldLayout(TypeAnalysisContext type, out byte[] snapshot,
        bool unboxValueTypeOffsets = false)
    {
        snapshot = [];
        var app = type.AppContext;
        if (app.MetadataVersion != 29 || app.Binary is not PE pe || pe.PointerSizeBytes != sizeof(ulong) ||
            !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            !pe.TryGetGenericMethodTableRegistration(out var origin) || type.Definition is not { } definition ||
            !NativeData(pe, origin.MetadataRegistrationAddress, Il2CppMetadataRegistration.GetStructSize(false, 29)))
            return false;
        var registration = pe.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(origin.MetadataRegistrationAddress);
        var ordinal = definition.TypeIndex.Value;
        if (ordinal < 0 || ordinal >= app.Metadata.TypeDefinitionCount ||
            !ReferenceEquals(app.Metadata.typeDefs[ordinal], definition) ||
            registration.fieldOffsetsCount != app.Metadata.TypeDefinitionCount ||
            registration.typeDefinitionsSizesCount != app.Metadata.TypeDefinitionCount ||
            pe.TypeDefinitionSizePointers.Length != app.Metadata.TypeDefinitionCount)
            return false;
        var offsetsSlot = checked(registration.fieldOffsetListAddress + (ulong)ordinal * sizeof(ulong));
        var sizesSlot = checked(registration.typeDefinitionsSizes + (ulong)ordinal * sizeof(ulong));
        if (!NativeData(pe, offsetsSlot, sizeof(ulong)) || !NativeData(pe, sizesSlot, sizeof(ulong))) return false;
        var offsets = pe.ReadPointerAtVirtualAddress(offsetsSlot);
        var sizes = pe.ReadPointerAtVirtualAddress(sizesSlot);
        if (pe.TypeDefinitionSizePointers[ordinal] != sizes || !NativeData(pe, sizes, 4 * sizeof(uint)) ||
            type.Fields.Count != definition.FieldCount ||
            type.Fields.Count > 0 && !NativeData(pe, offsets, (long)type.Fields.Count * sizeof(int)))
            return false;
        var captured = new List<byte>();
        Capture(origin.MetadataRegistrationAddress, Il2CppMetadataRegistration.GetStructSize(false, 29));
        Capture(offsetsSlot, sizeof(ulong)); Capture(sizesSlot, sizeof(ulong)); Capture(sizes, 4 * sizeof(uint));
        for (var index = 0; index < type.Fields.Count; index++)
        {
            if (type.Fields[index].BackingData is not { Field.RawFieldType: { } rawType } field ||
                field.IndexInParent != index || field.Attributes != (FieldAttributes)rawType.Attrs)
                return false;
            var address = checked(offsets + (ulong)index * sizeof(int));
            var raw = pe.MapVirtualAddressToRaw(address, false);
            var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(pe.GetRawBinaryContent().Slice(checked((int)raw), sizeof(int)));
            if (unboxValueTypeOffsets && definition.IsValueType && !type.Fields[index].IsStatic)
            {
                // Registration offsets address a boxed instance. The managed
                // field context uses unboxed offsets; the object header must
                // not become a second valid encoding of unboxed offset zero.
                var headerBytes = checked(2 * pe.PointerSizeBytes);
                if (value < headerBytes) return false;
                value -= headerBytes;
            }
            if (value != type.Fields[index].DefaultOffset) return false;
            Capture(address, sizeof(int));
        }
        snapshot = captured.ToArray();
        return true;

        void Capture(ulong address, int count)
        {
            var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
            captured.AddRange(pe.GetRawBinaryContent().Slice(offset, count).ToArray());
        }
    }
}
