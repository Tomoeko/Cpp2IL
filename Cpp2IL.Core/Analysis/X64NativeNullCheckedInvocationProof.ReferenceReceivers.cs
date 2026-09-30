using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    // A reference copy can widen to an original ancestor without changing any
    // pointer bits. It cannot narrow, box, or introduce interface/generic dispatch.
    private static bool ReferenceUpcast(TypeAnalysisContext source, TypeAnalysisContext? destination)
    {
        if (destination == null || !ReferenceEquals(source.AppContext, destination.AppContext) ||
            !OrdinaryClass(destination) || !NullCheckedCall.HasUnchangedReferenceBase(source, destination))
            return false;
        if (ReferenceEquals(source, destination)) return true;
        var visited = new HashSet<TypeAnalysisContext>();
        for (var type = source; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || !OrdinaryClass(type) ||
                type.Definition?.GenericContainerIndex.IsNull != true) return false;
            if (ReferenceEquals(type, destination)) return true;
        }
        return false;
    }

    private static bool TryReceiverDeclarations(MethodAnalysisContext caller, Origin origin,
        MethodAnalysisContext target, out ValueKey? declarations)
    {
        declarations = null;
        if (origin.Producer != null && origin.Definition is { } producer &&
            TryFieldReferenceProducer(caller, producer, out var provider))
            return TryReferenceProducerDeclarations(origin, provider, target, out declarations);
        if (origin.Field is not { } field) return true;
        // Preserve the existing exact receiver route. The new snapshot owns
        // only the ancestry consumed by a widened field capture or copy.
        if (!origin.ReferenceWidened) return true;
        if (field.BackingData?.Field is not { } definition ||
            !ReferenceEquals(field.FieldType, origin.Type) ||
            target.DeclaringType is not { } targetOwner ||
            !ReferenceHierarchyKey(origin.Type, targetOwner, out var receiver) ||
            !ReferenceDeclarationKey(field.DeclaringType, out var owner)) return false;
        declarations = new("reference-receiver", field,
            [new("field-definition", definition, []), new("name-index", definition.nameIndex, []),
                new("type-index", definition.typeIndex.Value, []), new("token", definition.token, []),
                new("name", field.Name, []), new("attributes", field.Attributes, []),
                new("offset", field.Offset, []), new("field-type", field.FieldType, []),
                ReferenceRawTypeKey(definition.RawFieldType), receiver, owner]);
        return true;
    }

    private static bool ReceiverDeclarationsRetained(MethodAnalysisContext caller, Origin origin,
        MethodAnalysisContext target, ValueKey? captured) =>
        TryReceiverDeclarations(caller, origin, target, out var current) &&
        (captured == null ? current == null : current != null && SameKey(captured, current));

    private static bool ReferenceReceiverUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var inherited = sites.Where(site => site.Receiver.ReferenceWidened).ToArray();
        if (inherited.Length == 0) return true;
        var copies = new HashSet<LocalVariable>(inherited.Select(site => site.Receiver.Definition!.Destination)
            .OfType<LocalVariable>());
        var instructions = caller.ControlFlowGraph!.Instructions.ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var instruction in instructions)
                if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable destination, LocalVariable source] } && copies.Contains(source))
                {
                    if (source.Type == null || !ReferenceUpcast(source.Type, destination.Type)) return false;
                    changed |= copies.Add(destination);
                }
        }
        foreach (var instruction in instructions)
            foreach (var read in OperandEffects.ReadLocals(instruction).Where(copies.Contains))
            {
                if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable destination, LocalVariable source] } &&
                    ReferenceEquals(source, read) && source.Type != null && ReferenceUpcast(source.Type, destination.Type))
                    continue;
                if (sites.Any(site => ReferenceEquals(site.Invocation, instruction) &&
                    ReferenceEquals(instruction.Operands[instruction.OpCode == OpCode.Call ? 2 : 1], read) &&
                    TryOrigin(caller, read, instruction, out var origin) && origin == site.Receiver)) continue;
                if (instruction is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, IntegerBitWidth: 64,
                        Operands: [LocalVariable, LocalVariable checkedValue, Immediate { Value: 0 }] } &&
                    ReferenceEquals(read, checkedValue) && inherited.Any(site => site.Comparison == instruction.NativeAddress))
                    continue;
                return false;
            }
        return true;
    }

    private static bool ReferenceHierarchyKey(TypeAnalysisContext source, TypeAnalysisContext target, out ValueKey key)
    {
        key = null!;
        var hierarchy = new List<ValueKey>();
        var visited = new HashSet<TypeAnalysisContext>();
        for (var type = source; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || !ReferenceDeclarationKey(type, out var declaration)) return false;
            hierarchy.Add(declaration);
            if (ReferenceEquals(type, target))
            {
                key = new("reference-hierarchy", source, hierarchy.ToArray());
                return true;
            }
        }
        return false;
    }

    private static bool ReferenceDeclarationKey(TypeAnalysisContext type, out ValueKey key)
    {
        key = null!;
        if (!OrdinaryClass(type) || type.Definition is not { } definition ||
            !definition.GenericContainerIndex.IsNull) return false;
        var enclosing = new List<ValueKey>();
        var visited = new HashSet<TypeAnalysisContext>();
        for (var owner = type.DeclaringType; owner != null; owner = owner.DeclaringType)
        {
            if (!visited.Add(owner) || !OrdinaryClass(owner) || owner.Definition is not { } parent ||
                !parent.GenericContainerIndex.IsNull) return false;
            enclosing.Add(ReferenceDeclarationFacts(owner));
        }
        key = new("reference-declaration", type,
            [ReferenceDeclarationFacts(type), new("enclosing", null, enclosing.ToArray())]);
        return true;
    }

    // DefaultBaseType is resolved from mutable raw metadata, so comparing it
    // with BaseType alone cannot detect a rebased declaration after admission.
    private static ValueKey ReferenceDeclarationFacts(TypeAnalysisContext type)
    {
        var definition = type.Definition!;
        return new("type-definition", definition,
            [new("assembly", type.DeclaringAssembly, []), new("name", type.Name, []),
                new("namespace", type.Namespace, []), new("attributes", type.Attributes, []),
                new("declaring-type", type.DeclaringType, []), new("base-type", type.BaseType, []),
                new("name-index", definition.NameIndex, []), new("namespace-index", definition.NamespaceIndex, []),
                new("type-index", definition.ByvalTypeIndex.Value, []),
                new("declaring-index", definition.DeclaringTypeIndex.Value, []),
                new("base-index", definition.ParentIndex.Value, []),
                new("generic-index", definition.GenericContainerIndex.Value, []),
                new("flags", definition.Flags, []), new("bitfield", definition.Bitfield, []),
                new("token", definition.Token, []), new("interface-count", definition.InterfacesCount, []),
                new("interface-index", definition.InterfacesStart.Value, []),
                ReferenceRawTypeKey(definition.RawType), ReferenceRawTypeKey(definition.RawBaseType)]);
    }

    private static ValueKey ReferenceRawTypeKey(Il2CppType? type) => new("reference-raw-type", type,
        [new("datapoint", type?.Datapoint, []), new("bits", type?.Bits, []), new("data", type?.Data.Dummy, []),
            new("attributes", type?.Attrs, []), new("kind", type?.Type, []), new("modifiers", type?.NumMods, []),
            new("byref", type?.Byref, []), new("pinned", type?.Pinned, []), new("valuetype", type?.ValueType, [])]);
}
