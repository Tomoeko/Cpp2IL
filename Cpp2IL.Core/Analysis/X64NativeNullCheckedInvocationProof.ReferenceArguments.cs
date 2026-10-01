using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Instruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private sealed record ReferenceFieldArgument(Origin Origin, TypeAnalysisContext Type);

    internal static bool IsReferenceFieldArgumentCapture(MethodAnalysisContext caller,
        LocalVariable value, Instruction definition) =>
        value.Type is { } type && TryReferenceFieldArgument(caller, value, definition, type,
            out _, definition);

    // A field value is captured once. Following its pure reference copies does
    // not permit replacing it with a later read, a producer or a guessed pointer.
    private static bool TryReferenceFieldArgument(MethodAnalysisContext caller, IOperand operand,
        Instruction use, TypeAnalysisContext type, out ReferenceFieldArgument argument,
        Instruction? capture = null)
    {
        argument = null!;
        if (!OrdinaryClass(type) || operand is not LocalVariable { Type: { } valueType } value ||
            !ReferenceEquals(valueType, type)) return false;
        Origin origin;
        if (capture == null)
        {
            if (!TryOrigin(caller, value, use, out origin)) return false;
        }
        else
        {
            // Traversal is considering this definition itself, rather than a
            // use after it. The complete invocation proof binds its native read.
            if (capture is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, NativeAddress: not null,
                    Operands: [LocalVariable destination, FieldReference access] } ||
                !ReferenceEquals(destination, value) ||
                access.Field.BackingData?.Field.RawFieldType is not { Data: not null } ||
                !ReferenceUpcast(access.Field.FieldType, type) ||
                !TryOrigin(caller, access.Local, capture, out var owner) ||
                owner.Definition != null || owner.Entry != -1 ||
                !ReferenceEquals(owner.Type, caller.DeclaringType)) return false;
            origin = new(-2, access.Field.FieldType, capture, Field: access.Field,
                SourceEntry: -1, Offset: access.Offset,
                ReferenceWidened: !ReferenceEquals(access.Field.FieldType, type));
        }
        if (origin is not { Field: { } field, Definition: { NativeAddress: not null } definition,
                SourceEntry: -1 } || !ReferenceEquals(field.DeclaringType, caller.DeclaringType) ||
            !ReferenceEquals(field.FieldType, origin.Type) || !ReferenceUpcast(origin.Type, type) ||
            !AccessibleField(caller, field) || Escaped(caller, value) ||
            definition.Operands is not [LocalVariable captured, FieldReference fieldAccess] ||
            !ReferenceEquals(fieldAccess.Field, field) || !ReferenceUpcast(origin.Type, captured.Type) ||
            fieldAccess.Offset != origin.Offset || field.Offset != origin.Offset ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(fieldAccess) ||
            !TryOrigin(caller, fieldAccess.Local, definition, out var fieldOwner) ||
            fieldOwner.Definition != null || fieldOwner.Entry != -1 ||
            !ReferenceEquals(fieldOwner.Type, caller.DeclaringType)) return false;
        argument = new(origin, type);
        return true;
    }

    // Check retained raw descriptors before resolving a mutable field or
    // parameter type. Missing class data cannot be treated as a valid capture,
    // and lazy resolution would otherwise throw before proof rejection.
    private static bool ReferenceArgumentDescriptorsValid(List<Site> sites) => sites.All(site =>
        site.Arguments.Select((argument, index) => argument.Enum is { } enumArgument ?
            index < site.Target.Parameters.Count && site.Target.Parameters[index].Definition?.RawType is { } raw &&
            IsSignedEnumArgumentType(enumArgument.Type) && EnumDescriptor(enumArgument.Type, raw) :
            argument.Reference == null && argument.NestedReference == null ||
            index < site.Target.Parameters.Count &&
            site.Target.Parameters[index].Definition?.RawType is { Data: not null } &&
            (argument.Reference?.Origin.Field?.BackingData?.Field.RawFieldType is { Data: not null } ||
             argument.NestedReference is { } nested &&
             nested.Field.BackingData?.Field.RawFieldType is { Data: not null } &&
             nested.Owner.Field?.BackingData?.Field.RawFieldType is { Data: not null })).All(valid => valid));

    // Captures before a guard must require the same proof as captures encountered
    // behind it. A typed target alone does not bind the argument's native value.
    internal static bool HasReferenceFieldArguments(MethodAnalysisContext caller, Instruction invocation)
    {
        if (!invocation.IsCall || invocation.Operands.Count == 0 ||
            invocation.Operands[0] is not MethodAnalysisContext target) return false;
        var start = invocation.OpCode == OpCode.Call ? 3 : 2;
        return target.Parameters.Select((parameter, index) =>
            index + start < invocation.Operands.Count && OrdinaryClass(parameter.ParameterType) &&
            HasFieldOrigin(invocation.Operands[index + start], invocation)).Any(captured => captured);

        bool HasFieldOrigin(IOperand operand, Instruction use)
        {
            var visited = new HashSet<LocalVariable>();
            while (operand is LocalVariable value && visited.Add(value) &&
                   ReachingDefinition(caller, value, use, out var definition))
            {
                if (definition?.Operands is [LocalVariable, FieldReference]) return true;
                if (definition is not { OpCode: OpCode.Move, Operands: [LocalVariable, LocalVariable copied] })
                    return false;
                operand = copied;
                use = definition;
            }
            return false;
        }
    }

    private static bool TryReferenceArgumentDeclarations(MethodAnalysisContext target, Argument[] arguments,
        out ValueKey? declarations)
    {
        declarations = null;
        var fields = new List<ValueKey>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].NestedReference is { } nested)
            {
                if (!TryNestedReferenceDeclarations(target, index, nested, out var nestedDeclarations)) return false;
                fields.Add(nestedDeclarations);
                continue;
            }
            if (arguments[index].Reference is not { } reference) continue;
            var origin = reference.Origin;
            var field = origin.Field!;
            var parameter = target.Parameters[index];
            if (field.BackingData?.Field is not { } definition || parameter.Definition is not { } rawParameter ||
                origin.Definition?.Destination is not LocalVariable captured || captured.Type == null ||
                !ReferenceHierarchyKey(origin.Type, reference.Type, out var assignment) ||
                !ReferenceHierarchyKey(origin.Type, captured.Type, out var capture) ||
                !ReferenceDeclarationKey(field.DeclaringType, out var owner) ||
                !ReferenceEquals(parameter.ParameterType, reference.Type) || !OriginalParameter(target, index))
                return false;
            fields.Add(new("reference-argument", index,
                [new("field", field, [new("definition", definition, []),
                    new("name-index", definition.nameIndex, []), new("type-index", definition.typeIndex.Value, []),
                    new("token", definition.token, []), new("name", field.Name, []),
                    new("attributes", field.Attributes, []), new("offset", field.Offset, []),
                    new("type", field.FieldType, []), ReferenceRawTypeKey(definition.RawFieldType)]),
                    new("parameter", rawParameter, [new("type", parameter.ParameterType, []),
                        new("index", parameter.ParameterIndex, []), new("attributes", parameter.Attributes, []),
                        new("name-index", rawParameter.nameIndex, []), new("type-index", rawParameter.typeIndex.Value, []),
                        new("token", rawParameter.token, []),
                        ReferenceRawTypeKey(rawParameter.RawType)]), assignment, capture, owner]));
        }
        if (fields.Count != 0) declarations = new("reference-arguments", target, fields.ToArray());
        return true;
    }

    private static bool ReferenceArgumentDeclarationsRetained(MethodAnalysisContext target, Argument[] arguments,
        ValueKey? retained) => TryReferenceArgumentDeclarations(target, arguments, out var current) &&
        (retained == null ? current == null : current != null && SameKey(retained, current));

    private static bool ReferenceArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var fields = sites.SelectMany(site => site.Arguments).Select(argument => argument.Reference)
            .OfType<ReferenceFieldArgument>().Distinct().ToArray();
        var instructions = caller.ControlFlowGraph!.Instructions.ToArray();
        foreach (var field in fields)
        {
            if (field.Origin.Definition!.Destination is not LocalVariable captured) return false;
            var copies = new HashSet<LocalVariable> { captured };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var instruction in instructions)
                    if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                            CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } && copies.Contains(source))
                    {
                        if (source.Type == null || !ReferenceUpcast(source.Type, destination.Type) ||
                            Escaped(caller, destination)) return false;
                        changed |= copies.Add(destination);
                    }
            }
            foreach (var instruction in instructions)
                foreach (var read in OperandEffects.ReadLocals(instruction).Where(copies.Contains))
                {
                    if (instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0,
                            CallSemantics: CallSemantics.Direct,
                            Operands: [LocalVariable destination, LocalVariable source] } &&
                        ReferenceEquals(source, read) && source.Type != null &&
                        ReferenceUpcast(source.Type, destination.Type)) continue;
                    if (sites.Any(site => ReferenceEquals(site.Invocation, instruction) &&
                        site.Arguments.Select((argument, index) => argument.Reference == field &&
                            ReferenceEquals(instruction.Operands[(instruction.OpCode == OpCode.Call ? 3 : 2) + index], read) &&
                            TryReferenceFieldArgument(caller, read, instruction, field.Type, out var current) &&
                            current == field).Any(matches => matches))) continue;
                    return false;
                }
        }
        return true;
    }
}
