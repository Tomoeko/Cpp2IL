using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64VirtualScalarZeroReturnProof
{
    private static bool CaptureDispatch(MethodAnalysisContext selected, List<object> inputs, List<TypeAnalysisContext> types)
    {
        var app = selected.AppContext;
        if (selected.DeclaringType is not { } owner || !OriginalDispatchSections(app, inputs)) return false;
        var hierarchy = new List<TypeAnalysisContext>();
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = owner; current != null; current = current.BaseType)
        {
            if (!seen.Add(current) || hierarchy.Count >= 64 || current.IsInterface || !CaptureType(current, inputs)) return false;
            hierarchy.Add(current); types.Add(current);
        }
        if (hierarchy.Count == 0 || !ReferenceEquals(hierarchy[^1], app.SystemTypes.SystemObjectType)) return false;

        var allInterfaces = new HashSet<TypeAnalysisContext>();
        var active = new HashSet<TypeAnalysisContext>();
        var directByType = new Dictionary<TypeAnalysisContext, TypeAnalysisContext[]>();
        foreach (var current in hierarchy)
        {
            if (!DirectInterfaces(current, out var direct)) return false;
            foreach (var contract in direct) if (!Visit(contract)) return false;
        }
        // A nested declaration also retains its original enclosing identity.
        for (var enclosing = owner.DeclaringType; enclosing != null; enclosing = enclosing.DeclaringType)
        {
            if (!seen.Add(enclosing) || seen.Count > 128 || !CaptureType(enclosing, inputs)) return false;
            types.Add(enclosing);
        }

        var domainByType = new Dictionary<TypeAnalysisContext, MethodAnalysisContext[]>();
        foreach (var contract in allInterfaces)
        {
            var methods = contract.Methods.OrderBy(method => method.Definition!.slot).ToArray();
            for (var slot = 0; slot < methods.Length; slot++)
                if (methods[slot].Definition!.slot != slot || !methods[slot].IsVirtual || methods[slot].IsStatic ||
                    !methods[slot].IsAbstract || methods[slot].Definition!.GenericContainer != null) return false;
            domainByType.Add(contract, methods);
        }
        MethodAnalysisContext[]? ownerTable = null;
        List<(TypeAnalysisContext Interface, int Offset)>? ownerOffsets = null;
        foreach (var current in hierarchy)
        {
            if (!Vtable(current, out var table)) return false;
            var closure = new HashSet<TypeAnalysisContext>();
            for (var baseType = current; baseType != null; baseType = baseType.BaseType)
                foreach (var direct in directByType[baseType]) AddClosure(direct, closure);
            if (!Offsets(current, table, closure, out var offsets)) return false;
            if (ReferenceEquals(current, owner)) { ownerTable = table; ownerOffsets = offsets; }
        }
        if (ownerTable == null || ownerOffsets == null || selected.Definition is not { } selectedDefinition ||
            selectedDefinition.slot >= ownerTable.Length || !ReferenceEquals(ownerTable[selectedDefinition.slot], selected)) return false;

        var overridden = new List<MethodAnalysisContext>();
        var classSlots = new List<int>();
        for (var slot = 0; slot < ownerTable.Length; slot++)
        {
            if (!ReferenceEquals(ownerTable[slot], selected)) continue;
            var interfaceSlot = false;
            foreach (var (contract, offset) in ownerOffsets)
            {
                var relative = slot - offset;
                if (relative < 0 || relative >= domainByType[contract].Length) continue;
                interfaceSlot = true;
                var target = domainByType[contract][relative];
                // Same-name public implicit implementation is established here.
                // Original explicit MethodImpl row presence is unavailable.
                if (!SameSignature(selected, target) || selected.Name != target.Name) return false;
                overridden.Add(target);
            }
            if (!interfaceSlot) classSlots.Add(slot);
        }
        if (overridden.Distinct().Count() != overridden.Count || !selected.Overrides.SequenceEqual(overridden)) return false;
        MethodAnalysisContext? baseMethod = null;
        if (classSlots.Count > 1) return false;
        if (classSlots.Count == 1)
        {
            if (classSlots[0] != selectedDefinition.slot) return false;
            foreach (var current in hierarchy.Skip(1))
            {
                var matches = current.Methods.Where(method => method.Definition!.slot == classSlots[0]).ToArray();
                if (matches.Length > 1) return false;
                if (matches.Length == 0) continue;
                baseMethod = matches[0];
                break;
            }
        }
        if (!ReferenceEquals(selected.BaseMethod, baseMethod) ||
            selected.IsNewSlot && baseMethod != null || !selected.IsNewSlot &&
            (baseMethod == null || !baseMethod.IsVirtual || baseMethod.IsFinal || !SameSignature(selected, baseMethod) ||
             selected.Name != baseMethod.Name) || classSlots.Count == 0 && (!selected.IsNewSlot || overridden.Count == 0)) return false;
        inputs.AddRange([selected, baseMethod ?? (object)"no-base-method", classSlots.Count, overridden.Count]);
        foreach (var target in overridden) inputs.Add(target);
        return true;

        bool DirectInterfaces(TypeAnalysisContext type, out TypeAnalysisContext[] direct)
        {
            direct = [];
            var definition = type.Definition!;
            if (!SectionRows(app.Metadata.metadataHeader.interfaces, definition.InterfacesStart.Value, definition.InterfacesCount, 4)) return false;
            var raw = definition.RawInterfaces;
            if (raw.Length != definition.InterfacesCount) return false;
            var result = new List<TypeAnalysisContext>();
            for (var ordinal = 0; ordinal < raw.Length; ordinal++)
            {
                var index = definition.InterfacesStart.Value + ordinal;
                var original = app.Metadata.ReadClassArrayAtRawAddr<int>(app.Metadata.metadataHeader.interfaces.Offset + (long)index * 4, 1)[0];
                if (app.Metadata.GetInterfaceIndicesFromOffset(definition.InterfacesStart, checked((ushort)ordinal)).Value != original ||
                    !X64OriginalReferenceClassProof.ValidTypeIndex(app, original) ||
                    !ReferenceEquals(app.Binary.AllTypes[original], raw[ordinal]) || !CaptureDescriptor(app, raw[ordinal], inputs) ||
                    X64OriginalReferenceClassProof.ResolveClass(app, raw[ordinal]) is not { IsInterface: true } contract ||
                    result.Contains(contract)) return false;
                inputs.Add(index); inputs.Add(original); result.Add(contract);
            }
            if (!type.InterfaceContexts.SequenceEqual(result)) return false;
            direct = result.ToArray(); directByType.Add(type, direct);
            return true;
        }

        bool Visit(TypeAnalysisContext contract)
        {
            if (active.Contains(contract)) return false;
            if (allInterfaces.Contains(contract)) return true;
            if (allInterfaces.Count >= 256 || !CaptureType(contract, inputs) || !DirectInterfaces(contract, out var direct)) return false;
            active.Add(contract);
            foreach (var parent in direct) if (!Visit(parent)) return false;
            active.Remove(contract); allInterfaces.Add(contract); types.Add(contract);
            return true;
        }

        void AddClosure(TypeAnalysisContext contract, HashSet<TypeAnalysisContext> closure)
        {
            if (!closure.Add(contract)) return;
            foreach (var parent in directByType[contract]) AddClosure(parent, closure);
        }

        bool Vtable(TypeAnalysisContext type, out MethodAnalysisContext[] table)
        {
            table = [];
            var definition = type.Definition!;
            if (!SectionRows(app.Metadata.metadataHeader.vtableMethods, definition.VtableStart, definition.VtableCount, 4)) return false;
            var result = new List<MethodAnalysisContext>();
            for (var slot = 0; slot < definition.VtableCount; slot++)
            {
                var index = definition.VtableStart + slot;
                var encoded = app.Metadata.ReadClassArrayAtRawAddr<uint>(app.Metadata.metadataHeader.vtableMethods.Offset + (long)index * 4, 1)[0];
                var usage = MetadataUsage.DecodeMetadataUsage(encoded, 0, app.LibCpp2IlContext);
                if (app.Metadata.VTableMethodIndices[index] != encoded || usage is not { Type: MetadataUsageType.MethodDef, IsValid: true } ||
                    usage.RawValue >= app.Metadata.MethodDefinitionCount || usage.AsMethod() is not { } original ||
                    !ReferenceEquals(app.Metadata.methodDefs[usage.RawValue], original) ||
                    app.ResolveContextForMethod(original) is not { } method || !CaptureMethod(method, inputs)) return false;
                inputs.Add(index); inputs.Add(encoded); inputs.Add(method); result.Add(method);
            }
            table = result.ToArray();
            return true;
        }

        bool Offsets(TypeAnalysisContext type, MethodAnalysisContext[] table, HashSet<TypeAnalysisContext> closure,
            out List<(TypeAnalysisContext Interface, int Offset)> offsets)
        {
            offsets = [];
            var definition = type.Definition!;
            if (!SectionRows(app.Metadata.metadataHeader.interfaceOffsets, definition.InterfaceOffsetsStart.Value,
                    definition.InterfaceOffsetsCount, 8)) return false;
            var rows = definition.InterfaceOffsets;
            if (rows.Length != definition.InterfaceOffsetsCount) return false;
            var found = new HashSet<TypeAnalysisContext>();
            for (var ordinal = 0; ordinal < rows.Length; ordinal++)
            {
                var index = definition.InterfaceOffsetsStart.Value + ordinal;
                var original = app.Metadata.ReadClassArrayAtRawAddr<int>(app.Metadata.metadataHeader.interfaceOffsets.Offset + (long)index * 8, 2);
                var row = rows[ordinal];
                if (row.typeIndex.Value != original[0] || row.offset != original[1] ||
                    !X64OriginalReferenceClassProof.ValidTypeIndex(app, original[0]) ||
                    !CaptureDescriptor(app, row.Type, inputs) ||
                    X64OriginalReferenceClassProof.ResolveClass(app, row.Type) is not { } contract ||
                    !closure.Contains(contract) || !found.Add(contract) || row.offset < 0 || row.offset > table.Length) return false;
                var domain = domainByType[contract];
                if (row.offset > table.Length - domain.Length) return false;
                for (var slot = 0; slot < domain.Length; slot++)
                    if (!table[row.offset + slot].IsVirtual || !SameSignature(table[row.offset + slot], domain[slot])) return false;
                inputs.AddRange([row, index, original[0], original[1], contract]);
                offsets.Add((contract, row.offset));
            }
            // Empty interfaces occupy no slots. Shared offsets and repeated
            // inheritance paths are legitimate; interface identities are unique.
            return found.SetEquals(closure);
        }

        bool SectionRows(Il2CppGlobalMetadataSectionHeader section, int start, int count, int width) =>
            count == 0 ? start >= -1 && start <= section.Size / width :
            start >= 0 && count >= 0 && start <= section.Size / width - count;
    }

    private static bool SameSignature(MethodAnalysisContext first, MethodAnalysisContext second)
    {
        if (first.IsStatic != second.IsStatic || first.GenericParameters.Count != second.GenericParameters.Count ||
            first.Parameters.Count != second.Parameters.Count) return false;
        var left = new List<object>(); var right = new List<object>();
        if (!CaptureResolvedType(first.ReturnType, left) || !CaptureResolvedType(second.ReturnType, right)) return false;
        for (var ordinal = 0; ordinal < first.Parameters.Count; ordinal++)
            if (!CaptureResolvedType(first.Parameters[ordinal].ParameterType, left) ||
                !CaptureResolvedType(second.Parameters[ordinal].ParameterType, right)) return false;
        return left.SequenceEqual(right);
    }

    private static bool OriginalDispatchSections(ApplicationAnalysisContext app, List<object> inputs)
    {
        var metadata = app.Metadata;
        var original = metadata.ReadReadable<Il2CppGlobalMetadataHeader>(0);
        var current = metadata.metadataHeader;
        var sections = new[] { (current.interfaces, original.interfaces, 4), (current.vtableMethods, original.vtableMethods, 4),
            (current.interfaceOffsets, original.interfaceOffsets, 8), (current.properties, original.properties, 20) };
        foreach (var (cached, actual, width) in sections)
        {
            if (cached.Offset != actual.Offset || cached.Size != actual.Size || actual.Offset < 0 || actual.Size < 0 ||
                actual.Size % width != 0 || actual.Offset > metadata.Length - actual.Size) return false;
            inputs.Add(actual.Offset); inputs.Add(actual.Size);
        }
        return true;
    }
}
