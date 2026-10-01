using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GetterReferenceArgumentProof
{
    private static bool OrdinaryMethod(MethodAnalysisContext method, bool? specialName) =>
        method.Definition is { GenericContainer: null, RawReturnType: { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        OriginalDescriptor(definition.RawReturnType) && method.DeclaringType is { } owner &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        method.Name == method.DefaultName && method.Name is not (".ctor" or ".cctor") &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.GenericParameters.Count == 0 && !method.IsStatic && !method.IsVirtual &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (specialName == null || ((method.Attributes & MethodAttributes.SpecialName) != 0) == specialName) &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool OrdinaryClass(TypeAnalysisContext type)
    {
        if (type.Definition is not { GenericContainer: null } definition ||
            definition.ByvalTypeIndex.Value < 0 || definition.ByvalTypeIndex.Value >= type.AppContext.Binary.AllTypes.Length ||
            !definition.ParentIndex.IsNull && (definition.ParentIndex.Value < 0 ||
                definition.ParentIndex.Value >= type.AppContext.Binary.AllTypes.Length) ||
            !OriginalEnclosingType(type) ||
            !ReferenceEquals(ResolveClassOrObject(type.AppContext, definition.RawType), type) ||
            type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            type.IsValueType || type.IsInterface || type.IsGenericInstance || type.GenericParameters.Count != 0 ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType) || type.Fields.Count != definition.FieldCount ||
            !type.Fields.Select(field => field.BackingData?.Field).SequenceEqual(definition.Fields!) ||
            type.Methods.Count != definition.MethodCount || !type.Methods.Select(method => method.Definition).SequenceEqual(definition.Methods!) ||
            type.Properties.Count != definition.PropertyCount ||
            !type.Properties.Select(property => property.Definition).SequenceEqual(definition.Properties!) ||
            type.DeclaringType is { } parent && parent.NestedTypes.Count(member => ReferenceEquals(member, type)) != 1)
            return false;
        if (ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType))
            return definition.ParentIndex.IsNull && definition.RawBaseType == null && type.BaseType == null;
        return ResolveClassOrObject(type.AppContext, definition.RawBaseType) is { } baseType &&
            ReferenceEquals(type.BaseType, baseType);
    }

    private static bool OriginalEnclosingType(TypeAnalysisContext type)
    {
        var index = type.Definition!.DeclaringTypeIndex;
        if (index.IsNull) return type.DeclaringType == null;
        if (index.Value < 0 || index.Value >= type.AppContext.Binary.AllTypes.Length || type.DeclaringType == null)
            return false;
        return ReferenceEquals(ResolveClass(type.AppContext, type.AppContext.Binary.GetType(index)), type.DeclaringType);
    }

    private static bool AccessibleType(TypeAnalysisContext caller, TypeAnalysisContext target) =>
        X64ReferenceArrayScalarResetProof.AccessibleElement(caller, target);

    private static FieldAnalysisContext? FindReferenceField(TypeAnalysisContext owner, int offset)
    {
        var matches = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        if (matches is not [{ } field] || field.BackingData?.Field is not
                { RawFieldType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } raw } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || !ReferenceEquals(field.DeclaringType, owner) ||
            field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes || field.OverrideFieldType != null ||
            field.Offset != field.DefaultOffset || field.UseOverrideConstantValue || field.StaticArrayInitialValue.Length != 0 ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal | FieldAttributes.HasFieldRVA)) != 0 ||
            ResolveClass(owner.AppContext, raw) is not { } type || !ReferenceEquals(field.FieldType, type) || !OrdinaryClass(type) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(field,
                new LocalVariable("getter-argument-owner", new ManagedRegister(null, "rcx"), owner), offset))) return null;
        return field;
    }

    private static bool TryFindGetter(MethodAnalysisContext caller, TypeAnalysisContext owner,
        FieldAnalysisContext payload, List<object> values, out GetterLeaf leaf)
    {
        leaf = null!;
        var candidates = new List<GetterLeaf>();
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = owner; current != null; current = current.BaseType)
        {
            // A newly emitted managed accessor must not introduce an unproved
            // class initializer on this declaration chain.
            if (!visited.Add(current) || current.Definition?.HasCctor != false ||
                current.Methods.Any(method => method.Name == ".cctor")) return false;
            foreach (var member in current.Methods)
                if (ReadGetterLeaf(member) is { } candidate && ReferenceEquals(candidate.Field, payload))
                    candidates.Add(candidate);
        }
        // Enumerate matching method identities before checking public access or
        // property eligibility. An identical inaccessible/non-property getter
        // does not let the inlined MOV identify a different original member.
        if (candidates is not [{ } found] || !ReferenceEquals(found.Method.DeclaringType, owner) ||
            !OrdinaryMethod(found.Method, specialName: true) || found.Method.Visibility != MethodAttributes.Public ||
            !X64SmallAggregateFieldGetterProof.OriginalAbi(found.Method) ||
            !X64GuardedEnumParameterCallProof.AccessibleTarget(caller.DeclaringType!, found.Method) ||
            !CallResultNullGuardProof.HasUnambiguousTarget(found.Method, owner)) return false;
        var getter = found.Method;
        var properties = owner.Properties.Where(property => ReferenceEquals(property.Getter, getter)).ToArray();
        if (properties is not [{ } property] || property.Definition is not { } definition ||
            property.Setter != null || !definition.set.IsNull || !ReferenceEquals(definition.Getter, getter.Definition) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || property.Name != property.DefaultName ||
            getter.Name != "get_" + property.Name || property.Attributes != property.DefaultAttributes ||
            property.OverridePropertyType != null || property.IsStatic ||
            definition.RawPropertyType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } ||
            ResolveClass(caller.AppContext, definition.RawPropertyType) is not { } propertyType ||
            !ReferenceEquals(property.PropertyType, propertyType) || !ReferenceEquals(propertyType, payload.FieldType) ||
            !CaptureMethod(getter, values) ||
            !CaptureBindings(getter, values)) return false;
        values.AddRange([property, definition, property.Name, property.Attributes, property.PropertyType,
            definition.nameIndex, definition.token, definition.attrs, definition.get, definition.set]);
        leaf = found;
        return true;
    }

    private static bool TryFindCallee(MethodAnalysisContext caller, ulong address, TypeAnalysisContext receiver,
        TypeAnalysisContext payload, List<object> values, out MethodAnalysisContext callee)
    {
        callee = null!;
        if (!caller.AppContext.MethodsByAddress.TryGetValue(address, out var bindings) || bindings.Count == 0 ||
            bindings.Any(member => member.IsStatic || member.DeclaringType == null)) return false;
        var owners = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null; current = current.BaseType)
            if (!owners.Add(current) || current.Definition?.HasCctor != false ||
                current.Methods.Any(method => method.Name == ".cctor")) return false;
        var applicable = bindings.Where(member => member.DeclaringType != null && owners.Contains(member.DeclaringType)).ToArray();
        if (applicable is not [{ } target] || !OrdinaryMethod(target, specialName: false) || !target.IsVoid ||
            !CallResultNullGuardProof.HasUnambiguousTarget(target, receiver) ||
            !X64GuardedEnumParameterCallProof.AccessibleTarget(caller.DeclaringType!, target) ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringType!.DeclaringAssembly,
                target.DeclaringType!.DeclaringAssembly) ||
            target.Definition is not { InternalParameterData: [{ RawType: { Data: not null, NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } raw }] } definition ||
            target.Parameters is not [{ } parameter] || !ReferenceEquals(parameter.Definition, definition.InternalParameterData[0]) ||
            parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, target) || parameter.IsRef ||
            parameter.OverrideParameterType != null || parameter.Attributes != parameter.DefaultAttributes ||
            parameter.Name != parameter.DefaultName ||
            ResolveClass(caller.AppContext, raw) is not { } parameterType ||
            !ReferenceEquals(parameter.ParameterType, parameterType) || !ReferenceEquals(parameterType, payload) ||
            new X64CallingConventionResolver().ReturnsViaHiddenBuffer(target) ||
            new X64CallingConventionResolver().ResolveForParameters(target) is not
                [ManagedRegister { Name: "rcx", Version: -1 }, ManagedRegister { Name: "rdx", Version: -1 },
                    ManagedRegister { Name: "r8", Version: -1 }] || !CaptureBindings(target, values)) return false;
        callee = target;
        return true;
    }

    private static bool CaptureBindings(MethodAnalysisContext method, List<object> values)
    {
        if (!method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count(member => ReferenceEquals(member, method)) != 1) return false;
        values.Add(bindings.Count);
        foreach (var alias in bindings)
        {
            if (alias.DeclaringType == null || alias.UnderlyingPointer != method.UnderlyingPointer ||
                alias.Definition == null && alias is not ConcreteGenericMethodAnalysisContext) return false;
            values.AddRange([alias, alias.DeclaringType, alias.UnderlyingPointer, alias.Name, alias.Attributes,
                alias.ImplAttributes, (object?)alias.Definition ?? DBNull.Value]);
            if (alias.Definition is { } definition)
                values.AddRange([definition.token, definition.declaringTypeIdx, definition.returnTypeIdx,
                    definition.parameterStart, definition.parameterCount, definition.flags, definition.iflags]);
            if (alias is ConcreteGenericMethodAnalysisContext generic)
            {
                values.Add(generic.BaseMethodContext);
                if (!CaptureMethod(generic.BaseMethodContext, values)) return false;
                values.Add(generic.TypeGenericParameters.Count);
                foreach (var type in generic.TypeGenericParameters) values.Add(type);
                values.Add(generic.MethodGenericParameters.Count);
                foreach (var type in generic.MethodGenericParameters) values.Add(type);
            }
        }
        return true;
    }

    private static bool CaptureMethod(MethodAnalysisContext method, List<object> values)
    {
        if (method.Definition is not { RawReturnType: { Data: not null } rawReturn } definition || !OriginalDescriptor(rawReturn) ||
            definition.parameterCount != method.Parameters.Count ||
            (definition.InternalParameterData?.Length ?? 0) != method.Parameters.Count) return false;
        // Array/generic wrapper contexts can be freshly allocated on resolution.
        // Freeze retained descriptors rather than such incidental allocations;
        // the selected caller/getter/callee types are bound independently.
        values.AddRange([method, definition, method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes,
            (object?)method.OverrideReturnType ?? DBNull.Value, definition.nameIndex, definition.token, definition.flags,
            definition.iflags, definition.declaringTypeIdx, definition.returnTypeIdx, definition.parameterStart,
            definition.parameterCount, definition.genericContainerIndex, definition.slot]);
        X64SmallAggregateFieldGetterProof.CaptureRawType(rawReturn, values);
        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            if (parameter.Definition is not { RawType: { Data: not null } raw } original || !OriginalDescriptor(raw) ||
                !ReferenceEquals(original, definition.InternalParameterData![index])) return false;
            values.AddRange([parameter, parameter.ParameterIndex, parameter.Name, parameter.Attributes,
                (object?)parameter.OverrideParameterType ?? DBNull.Value,
                original, original.nameIndex, original.token, original.typeIndex]);
            X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
        }
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
            var assembly = current.DeclaringAssembly;
            if (assembly.Definition == null || assembly.Name != assembly.DefaultName || assembly.Version != assembly.DefaultVersion ||
                assembly.HashAlgorithm != assembly.DefaultHashAlgorithm ||
                assembly.Flags != assembly.DefaultFlags || (assembly.Culture ?? "") != (assembly.DefaultCulture ?? "") ||
                !(assembly.PublicKey ?? []).SequenceEqual(assembly.DefaultPublicKey ?? []) ||
                !(assembly.PublicKeyToken ?? []).SequenceEqual(assembly.DefaultPublicKeyToken ?? [])) return false;
            values.AddRange([current, current.Name, current.Namespace, current.Attributes, current.BaseType!,
                assembly, assembly.Definition, assembly.Name, assembly.Version, assembly.Flags,
                assembly.HashAlgorithm, (object?)assembly.Culture ?? DBNull.Value,
                assembly.Definition.ImageIndex, assembly.Definition.Token, assembly.Definition.ReferencedAssemblyStart,
                assembly.Definition.ReferencedAssemblyCount,
                (object?)current.DeclaringType ?? DBNull.Value, definition.DeclaringTypeIndex,
                definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Flags, definition.Bitfield,
                definition.ByvalTypeIndex, definition.ParentIndex, definition.GenericContainerIndex, definition.FirstFieldIdx,
                definition.FieldCount, definition.FirstMethodIdx, definition.MethodCount, definition.RawSizes.instance_size,
                definition.RawSizes.native_size, definition.RawSizes.static_fields_size, definition.RawSizes.thread_static_fields_size]);
            foreach (var value in assembly.PublicKey ?? []) values.Add(value);
            foreach (var value in assembly.PublicKeyToken ?? []) values.Add(value);
            X64SmallAggregateFieldGetterProof.CaptureRawType(definition.RawType, values);
            if (definition.RawBaseType is { } rawBase) X64SmallAggregateFieldGetterProof.CaptureRawType(rawBase, values);
            foreach (var member in current.Methods)
            {
                if (member.Definition is not { } original || !ReferenceEquals(original.DeclaringType, definition) ||
                    member.Name != member.DefaultName || member.Attributes != member.DefaultAttributes ||
                    member.ImplAttributes != member.DefaultImplAttributes || !CaptureMethod(member, values)) return false;
            }
            foreach (var field in current.Fields)
            {
                if (field.BackingData?.Field is not { RawFieldType: { Data: not null } raw } original ||
                    field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes || field.OverrideFieldType != null ||
                    field.Offset != field.DefaultOffset || !ReferenceEquals(original.DeclaringType, definition) || !OriginalDescriptor(raw)) return false;
                values.AddRange([field, field.Name, field.Attributes, field.Offset, original, original.nameIndex, original.token, original.typeIndex]);
                X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
            }
            values.Add(current.Properties.Count);
            foreach (var property in current.Properties)
            {
                if (property.Definition is not { } original || property.Name != property.DefaultName ||
                    property.Attributes != property.DefaultAttributes || property.OverridePropertyType != null) return false;
                values.AddRange([property, original, property.Name, property.Attributes, original.nameIndex, original.token,
                    original.attrs, original.get, original.set, (object?)property.Getter ?? DBNull.Value, (object?)property.Setter ?? DBNull.Value]);
            }
            var references = assembly.Definition.ReferencedAssemblies;
            values.Add(references.Length);
            foreach (var reference in references) values.Add(reference);
            if (current.DeclaringType is { } enclosing && !CaptureChain(enclosing, values, seen)) return false;
        }
        return visited.Contains(type.AppContext.SystemTypes.SystemObjectType);
    }

    // This proof is admitted only for the 2021.3 profile. Its type descriptors
    // use five modifier bits and the separate value-type bit at position 31.
    private static bool OriginalDescriptor(Il2CppType raw) => raw.Data != null && raw.Datapoint == raw.Data.Dummy &&
        raw.Attrs == (raw.Bits & 0xFFFF) && raw.Type == (Il2CppTypeEnum)((raw.Bits >> 16) & 0xFF) &&
        raw.NumMods == ((raw.Bits >> 24) & 0x1F) && raw.Byref == ((raw.Bits >> 29) & 1) &&
        raw.Pinned == ((raw.Bits >> 30) & 1) && raw.ValueType == (raw.Bits >> 31);

    private static TypeAnalysisContext? ResolveClass(ApplicationAnalysisContext app, Il2CppType raw)
    {
        if (!OriginalDescriptor(raw) || raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS || raw.ValueType != 0 ||
            raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0 || raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount)
            return null;
        var definition = app.Metadata.typeDefs[(int)raw.Data.Dummy];
        if (definition.DeclaringAssembly is not { } image || app.ResolveContextForAssembly(image) is not { } assembly ||
            !ReferenceEquals(assembly.Definition?.Image, image) ||
            assembly.GetTypeByDefinition(definition) is not { } type || !ReferenceEquals(type.AppContext, app) ||
            !ReferenceEquals(type.Definition, definition) || !ReferenceEquals(type.DeclaringAssembly, assembly) ||
            assembly.Types.Count(member => ReferenceEquals(member, type)) != 1) return null;
        return type;
    }

    private static TypeAnalysisContext? ResolveClassOrObject(ApplicationAnalysisContext app, Il2CppType? raw)
    {
        if (raw == null || !OriginalDescriptor(raw) || raw.ValueType != 0 ||
            raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0) return null;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS) return ResolveClass(app, raw);
        var type = app.SystemTypes.SystemObjectType;
        if (raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_OBJECT || type.Definition is not { } definition ||
            definition.ByvalTypeIndex.Value < 0 || definition.ByvalTypeIndex.Value >= app.Binary.AllTypes.Length ||
            definition.TypeIndex.Value < 0 || definition.RawType is not { } original || !OriginalDescriptor(original) ||
            original.Type != raw.Type || original.Data.Dummy != raw.Data.Dummy ||
            original.Data.Dummy != (ulong)definition.TypeIndex.Value) return null;
        return type;
    }
}
