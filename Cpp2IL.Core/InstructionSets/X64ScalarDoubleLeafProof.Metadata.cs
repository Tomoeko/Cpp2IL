using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Register = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ScalarDoubleLeafProof
{
    private static readonly X64CallingConventionResolver CallingConventions = new();

    private static bool CompleteAliases(MethodAnalysisContext method,
        X64GenericMethodTableProof.Evidence genericTables, out MethodAnalysisContext[] aliases)
    {
        aliases = [];
        var app = method.AppContext;
        if (Contains(genericTables.MethodPointers) || Contains(genericTables.Invokers) ||
            Contains(genericTables.AdjustorThunks) ||
            !app.Binary.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) || bindings.Count == 0 ||
            bindings.Any(alias => alias == null || !ReferenceEquals(alias.AppContext, app) ||
                                  alias.UnderlyingPointer != method.UnderlyingPointer) ||
            bindings.Distinct().Count() != bindings.Count || !bindings.Any(alias => ReferenceEquals(alias, method)))
            return false;
        // Independently enumerate original MethodDefs. A removed address-table
        // alias before capture cannot turn a shared body into a uniquely owned one.
        var original = new HashSet<MethodAnalysisContext>();
        foreach (var definition in app.Metadata.methodDefs)
        {
            if (definition.MethodPointer != method.UnderlyingPointer) continue;
            if (app.ResolveContextForMethod(definition) is not { } context ||
                !ReferenceEquals(context.Definition, definition) || !original.Add(context)) return false;
        }
        // Generic ABI/instantiation semantics are outside these ordinary leaves.
        // Original registration tables, not a mutable concrete-method cache,
        // establish their absence, including invoker and adjustor entry points.
        if (!original.SetEquals(bindings)) return false;
        aliases = bindings.OrderBy(alias => alias.Definition?.MethodIndex.Value).ToArray();
        return true;

        bool Contains(ReadOnlySpan<ulong> pointers)
        {
            foreach (var pointer in pointers)
                if (pointer == method.UnderlyingPointer) return true;
            return false;
        }
    }

    private static bool Bind(MethodAnalysisContext method, Shape shape, PE pe, X64UnwindProof.Index unwind,
        List<object> input, out FieldAnalysisContext? first, out FieldAnalysisContext? second, out ulong? scale)
    {
        first = null; second = null; scale = null;
        var app = method.AppContext;
        if (!Signature(method, shape.Operation, input) || method.DeclaringType is not { } owner) return false;
        if (shape.Operation == Kind.FieldSum)
        {
            var visited = new HashSet<TypeAnalysisContext>();
            for (var current = owner; current != null; current = current.BaseType)
            {
                if (!visited.Add(current) || visited.Count > 32 || !CaptureOwner(current, input, fields: true)) return false;
            }
            first = Field(shape.First.MemoryDisplacement64);
            second = Field(shape.Arithmetic.MemoryDisplacement64);
            return first != null && second != null;
        }
        var address = shape.Arithmetic.IPRelativeMemoryAddress;
        var offset = unwind.MapReadOnlyData(address, sizeof(double));
        if (offset < 0 || !unwind.IsUnaffectedByBaseRelocation(address, sizeof(double)) ||
            offset > pe.GetRawBinaryContent().Length - sizeof(double)) return false;
        var bytes = pe.GetRawBinaryContent().Slice(offset, sizeof(double));
        scale = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (!HasFiniteNormalScaleDomain(scale.Value)) return false;
        input.Add(address); input.Add(Convert.ToBase64String(bytes.ToArray()));
        return true;

        FieldAnalysisContext? Field(ulong offset)
        {
            // Static offsets refer to a different storage area and can equal an
            // instance offset. They must not participate in this ownership query.
            if (owner.Fields.Where(candidate => !candidate.IsStatic && candidate.Offset == (long)offset).ToArray() is not [var field] ||
                !ReferenceEquals(field.DeclaringType, owner) || field.BackingData?.Field is not { } definition ||
                !X64OriginalReferenceClassProof.OriginalField(app, definition) ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemDoubleType) ||
                definition.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R8 } raw ||
                !CaptureDescriptor(app, raw, input)) return null;
            var access = new FieldReference(field, new LocalVariable("receiver", new Register(null, "rcx"), owner), field.Offset);
            return NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, 64) ? field : null;
        }
    }

    private static bool Signature(MethodAnalysisContext method, Kind operation, List<object> input)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
            !app.Binary.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            method is ConcreteGenericMethodAnalysisContext || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" || method.GenericParameters.Count != 0 ||
            method.Definition is not { GenericContainer: null, IsUnmanagedCallersOnly: false,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_R8, NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            method.DeclaringType is not { Definition: { GenericContainer: null } ownerDefinition } owner ||
            !ReferenceEquals(owner.AppContext, app) || owner.IsInterface || owner.IsEnumType || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, ownerDefinition) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            owner.Methods.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
            !(ownerDefinition.Methods ?? []).SequenceEqual(owner.Methods.Select(candidate => candidate.Definition)) ||
            !X64OriginalReferenceClassProof.OriginalType(app, ownerDefinition) ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            method.Name != method.DefaultName || method.OverrideName != null || method.OverrideAttributes != null ||
            method.OverrideImplAttributes != null || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemDoubleType) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) || CallingConventions.ReturnsViaHiddenBuffer(method) ||
            CallingConventions.ReturnRegister(method).Name != "xmm0" ||
            !CaptureOwner(owner, input, fields: false) || !CaptureDescriptor(app, definition.RawReturnType, input))
            return false;
        var arguments = CallingConventions.ResolveForManaged(method);
        if (arguments is not [Register { Name: "rcx" }, Register { Name: "rdx" }]) return false;
        input.AddRange([method, definition, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            method.UnderlyingPointer, definition.MethodIndex.Value, definition.token, definition.flags, definition.iflags,
            definition.slot, definition.returnTypeIdx.Value, definition.parameterStart.Value, definition.parameterCount]);
        if (operation == Kind.FieldSum)
            return !method.IsStatic && !owner.IsValueType && NullCheckedCall.IsReferenceClass(owner) &&
                   method.Parameters.Count == 0 && definition.parameterCount == 0 &&
                   definition.InternalParameterData is [];
        if (!method.IsStatic || method.Parameters is not [var parameter] || parameter.Definition is not { } original ||
            parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
            parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue || parameter.Name != parameter.DefaultName ||
            parameter.Attributes != parameter.DefaultAttributes || !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt64Type) ||
            !X64OriginalReferenceClassProof.OriginalParameter(app, definition, 0, original) ||
            original.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I8, NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            !CaptureDescriptor(app, raw, input)) return false;
        input.AddRange([parameter, original, parameter.Name, parameter.Attributes, parameter.ParameterType,
            original.nameIndex, original.token, original.typeIndex.Value]);
        return true;
    }

    private static bool CaptureOwner(TypeAnalysisContext owner, List<object> input, bool fields)
    {
        var app = owner.AppContext;
        if (owner.Definition is not { GenericContainer: null } definition ||
            !X64OriginalReferenceClassProof.OriginalType(app, definition) ||
            !ReferenceEquals(app.ResolveContextForType(definition), owner) ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(owner.DeclaringAssembly) ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace || owner.Attributes != owner.DefaultAttributes ||
            owner.OverrideBaseType != null || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !CaptureDescriptor(app, definition.RawType, input)) return false;
        input.AddRange([owner, definition, owner.DeclaringAssembly, owner.Name, owner.Namespace, owner.Attributes,
            owner.BaseType ?? (object)"no-base", owner.DeclaringType ?? (object)"no-enclosing-type",
            definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Flags, definition.Bitfield,
            definition.ByvalTypeIndex.Value, definition.ParentIndex.Value, definition.DeclaringTypeIndex.Value,
            definition.GenericContainerIndex.Value, definition.HasCctor]);
        // Initializers belong to the original declaration and calling context.
        // These closed leaves do not read a static field or invoke an initializer.
        if (!fields) return true;
        if (owner.Fields.Count != definition.FieldCount ||
            !(definition.Fields ?? []).SequenceEqual(owner.Fields.Select(field => field.BackingData?.Field)) ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(owner, out var layout)) return false;
        input.Add(Convert.ToBase64String(layout));
        foreach (var field in owner.Fields)
        {
            if (field.BackingData?.Field is not { RawFieldType: { } raw } original ||
                !ReferenceEquals(field.DeclaringType, owner) || !X64OriginalReferenceClassProof.OriginalField(app, original) ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes || field.Offset != field.DefaultOffset ||
                field.OverrideFieldType != null || !CaptureDescriptor(app, raw, input)) return false;
            input.AddRange([field, original, field.Name, field.Attributes, field.Offset,
                original.nameIndex, original.token, original.typeIndex.Value]);
        }
        return true;
    }

    private static bool CaptureDescriptor(ApplicationAnalysisContext app, Il2CppType raw, List<object> input)
    {
        if (!X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            !app.Binary.TryGetTypeVirtualAddress(raw, out var address) || address > ulong.MaxValue - 11) return false;
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var image = app.Binary.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - 12 ||
            app.Binary.MapVirtualAddressToRaw(address + 11, false) != offset + 11) return false;
        input.AddRange([raw, address, raw.Datapoint, raw.Bits, raw.Data.Dummy, raw.Attrs, raw.Type,
            raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType,
            Convert.ToBase64String(image.Slice(checked((int)offset), 12).ToArray())]);
        return true;
    }

    internal static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition output)
    {
        if (method.GetExtraData<Evidence>(EvidenceKey) is not { } proof || method.DeclaringType is not { } owner ||
            owner.GetExtraData<TypeDefinition>("AsmResolverType") is not { } outputOwner ||
            !ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output) ||
            !ReferenceEquals(output.DeclaringType, outputOwner) ||
            outputOwner.Name != owner.Name || (outputOwner.Namespace?.ToString() ?? "") != owner.Namespace ||
            (uint)outputOwner.Attributes != (uint)owner.Attributes || outputOwner.GenericParameters.Count != 0 ||
            output.Name != method.Name || (ushort)output.Attributes != (ushort)method.Attributes ||
            (ushort)output.ImplAttributes != (ushort)method.ImplAttributes || output.GenericParameters.Count != 0 ||
            output.Signature is not { ExplicitThis: false, GenericParameterCount: 0 } signature ||
            signature.Attributes != (CallingConventionAttributes.Default |
                (method.IsStatic ? 0 : CallingConventionAttributes.HasThis)) ||
            signature.SentinelParameterTypes.Count != 0 || signature.HasThis != !method.IsStatic ||
            signature.ParameterTypes.Count != method.Parameters.Count ||
            !SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) ||
            output.ParameterDefinitions.Count != method.Parameters.Count)
            return false;
        for (var ordinal = 0; ordinal < method.Parameters.Count; ordinal++)
        {
            var parameter = method.Parameters[ordinal];
            var declaration = output.ParameterDefinitions[ordinal];
            if (declaration.Sequence != ordinal + 1 || declaration.Name != parameter.Name ||
                (ushort)declaration.Attributes != (ushort)parameter.Attributes ||
                !SignatureComparer.Default.Equals(signature.ParameterTypes[ordinal], parameter.ParameterType.ToTypeSignature()))
                return false;
        }
        if (!Owner(owner, outputOwner, fields: proof.Native.Operation == Kind.FieldSum)) return false;
        if (proof.Native.Operation != Kind.FieldSum) return true;
        var visited = new HashSet<TypeAnalysisContext> { owner };
        for (var current = owner.BaseType; current != null; current = current.BaseType)
            if (!visited.Add(current) || visited.Count > 32 ||
                current.GetExtraData<TypeDefinition>("AsmResolverType") is not { } declaration ||
                !Owner(current, declaration, fields: true)) return false;
        return true;

        static bool Owner(TypeAnalysisContext context, TypeDefinition declaration, bool fields)
        {
            if (context.Definition is not { } original ||
                !ReferenceEquals(context.GetExtraData<TypeDefinition>("AsmResolverType"), declaration) ||
                declaration.Name != context.Name || (declaration.Namespace?.ToString() ?? "") != context.Namespace ||
                (uint)declaration.Attributes != (uint)context.Attributes || declaration.GenericParameters.Count != 0 ||
                !ReferenceEquals(declaration.DeclaringType, context.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
                !SignatureComparer.Default.Equals(declaration.BaseType, context.BaseType?.ToTypeSignature().ToTypeDefOrRef()))
                return false;
            // Reuse the declaration emitter's policy. Native instance size is
            // not substituted for an unavailable authored managed ClassSize.
            var expected = new TypeDefinition(context.Namespace, context.Name,
                (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)context.Attributes);
            AsmResolverDllOutputFormat.ConfigureTypeLayout(original, expected);
            if (declaration.ClassLayout?.PackingSize != expected.ClassLayout?.PackingSize ||
                declaration.ClassLayout?.ClassSize != expected.ClassLayout?.ClassSize ||
                declaration.ClassLayout != null && !ReferenceEquals(declaration.ClassLayout.Parent, declaration))
                return false;
            if (!fields) return true;
            if (declaration.Fields.Count != context.Fields.Count) return false;
            for (var ordinal = 0; ordinal < context.Fields.Count; ordinal++)
            {
                var field = context.Fields[ordinal];
                var outputField = declaration.Fields[ordinal];
                if (!ReferenceEquals(field.GetExtraData<FieldDefinition>("AsmResolverField"), outputField) ||
                    !ReferenceEquals(outputField.DeclaringType, declaration) ||
                    outputField.Name != field.Name || (ushort)outputField.Attributes != (ushort)field.Attributes ||
                    outputField.Signature is not { Attributes: CallingConventionAttributes.Field } signature ||
                    !SignatureComparer.Default.Equals(signature.FieldType, field.FieldType.ToTypeSignature()) ||
                    outputField.FieldOffset != (declaration.IsExplicitLayout && !field.IsStatic ? field.Offset : (int?)null))
                    return false;
            }
            return true;
        }
    }
}
