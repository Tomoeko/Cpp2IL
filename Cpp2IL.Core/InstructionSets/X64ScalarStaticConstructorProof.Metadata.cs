using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ScalarStaticConstructorProof
{
    // Shared by the ordinary primitive and single-scalar wrapper initializers.
    // Neither route admits a MethodRef or changes the TypeInfo helper contract.
    internal static bool CaptureBinding(MethodAnalysisContext method, FieldAnalysisContext stored,
        X64ScalarWrapperStaticConstructorProof.Shape shape, List<object> input,
        out X64GenericMethodTableProof.Evidence genericTables)
    {
        genericTables = null!;
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
            app.Binary is not PE { PointerSizeBytes: 8 } pe ||
            !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            method is ConcreteGenericMethodAnalysisContext || !method.IsStatic || method.IsVirtual ||
            !method.IsVoid || method.Name != ".cctor" || method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            method.Definition is not { GenericContainer: null, parameterCount: 0, IsUnmanagedCallersOnly: false,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null, HasCctor: true,
                IsEnumType: false, IsByRefLike: false, IsImportOrWindowsRuntime: false } } owner ||
            owner.IsGenericInstance || owner.IsInterface || owner.GenericParameters.Count != 0 ||
            (owner.Attributes & TypeAttributes.BeforeFieldInit) == 0 ||
            owner.OverrideBaseType != null || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace || owner.Attributes != owner.DefaultAttributes ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(owner.DeclaringAssembly) ||
            !X64OriginalReferenceClassProof.OriginalType(app, owner.Definition) ||
            !ReferenceEquals(app.ResolveContextForType(owner.Definition), owner) ||
            !OriginalOwnerDescriptor(owner) ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !(owner.Definition.Methods ?? []).SequenceEqual(owner.Methods.Select(candidate => candidate.Definition)) ||
            owner.Methods.Where(candidate => candidate.Name == ".cctor").ToArray() is not [var cctor] ||
            !ReferenceEquals(cctor, method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var aliases) ||
            aliases is not [var alias] || !ReferenceEquals(alias, method) ||
            app.Metadata.methodDefs.Count(row => row.MethodPointer == method.UnderlyingPointer) != 1 ||
            X64GenericMethodTableProof.TryIdentify(app, pe, unwind) is not { } tables ||
            Contains(tables.MethodPointers) || Contains(tables.Invokers) || Contains(tables.AdjustorThunks) ||
            owner.Fields.Count != owner.Definition.FieldCount ||
            !(owner.Definition.Fields ?? []).SequenceEqual(owner.Fields.Select(field => field.BackingData?.Field)) ||
            owner.Fields.Where(field => field.IsStatic).ToArray() is not [var selected] || !ReferenceEquals(selected, stored) ||
            stored.Offset != 0 || stored.Offset != stored.DefaultOffset ||
            owner.Definition.RawSizes.static_fields_size != shape.Width ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(owner, out var layout,
                unboxValueTypeOffsets: owner.IsValueType) ||
            !OrdinaryField(stored) || stored.RawIl2CppCustomAttributeData.Length != 0 ||
            stored.CustomAttributes is { Count: > 0 } ||
            (stored.Attributes & (FieldAttributes.Static | FieldAttributes.InitOnly)) !=
                (FieldAttributes.Static | FieldAttributes.InitOnly) ||
            !ReferenceEquals(stored.FieldType, owner) &&
                (!ReferenceEquals(stored.FieldType, app.SystemTypes.SystemInt32Type) ||
                 stored.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 }) ||
            shape.Flag >= shape.TypeInfoSlot && shape.Flag - shape.TypeInfoSlot < 8 ||
            !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.Flag) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.TypeInfoSlot, 8) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, shape.TypeInfoSlot, 8) ||
            !X64MetadataInitializationHelperProof.TryCaptureTypeInfoInput(app, pe, unwind, shape.Initializer, out var helper) ||
            app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot) is not
                { Type: MetadataUsageType.TypeInfo, IsValid: true } usage ||
            usage.Offset != shape.TypeInfoSlot || usage.RawValue != owner.Definition.ByvalTypeIndex.Value ||
            !ReferenceEquals(usage.AsType(), owner.Definition.RawType) ||
            !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), owner)) return false;

        var slot = checked((int)pe.MapVirtualAddressToRaw(shape.TypeInfoSlot, false));
        var slotBytes = pe.GetRawBinaryContent().Slice(slot, 8);
        var encoded = ((ulong)MetadataUsageType.TypeInfo << 29) |
            ((ulong)usage.RawValue << 1) | 1;
        if (BinaryPrimitives.ReadUInt64LittleEndian(slotBytes) != encoded ||
            !CaptureDescriptor(app, definition.RawReturnType, input) ||
            !CaptureDescriptor(app, owner.Definition.RawType, input)) return false;
        var body = X64NativeInstructionReader.ReadRootBody(method);
        if (body == null || X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null) return false;
        var end = body[^1].NextIP;
        var raw = checked((int)pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false));
        input.AddRange([app, app.LibCpp2IlContext, app.Metadata, pe, method, definition,
            method.Name, method.Attributes, method.ImplAttributes, method.ReturnType, method.UnderlyingPointer,
            definition.MethodIndex.Value, definition.token, definition.flags, definition.iflags,
            definition.slot, definition.returnTypeIdx.Value, definition.parameterStart.Value, definition.parameterCount,
            owner, owner.Definition, owner.DeclaringAssembly, owner.Name, owner.Namespace, owner.Attributes,
            owner.BaseType ?? (object)"no-base", owner.DeclaringType ?? (object)"no-enclosing-type",
            owner.Definition.NameIndex, owner.Definition.NamespaceIndex, owner.Definition.Token,
            owner.Definition.Flags, owner.Definition.Bitfield, owner.Definition.ByvalTypeIndex.Value,
            owner.Definition.ParentIndex.Value, owner.Definition.DeclaringTypeIndex.Value,
            owner.Definition.GenericContainerIndex.Value, owner.Definition.HasCctor,
            Convert.ToBase64String(layout), usage.Type, usage.AsType(), usage.RawValue, usage.Offset,
            shape, helper, Convert.ToBase64String(slotBytes.ToArray()),
            Convert.ToBase64String(pe.GetRawBinaryContent().Slice(raw, checked((int)(end - method.UnderlyingPointer))).ToArray()),
            Convert.ToBase64String(method.RawBytes.AsSpan().ToArray())]);
        foreach (var field in owner.Fields)
        {
            if (!OrdinaryField(field) || !ReferenceEquals(field.DeclaringType, owner) ||
                field.BackingData?.Field is not { RawFieldType: { } rawType } original || !X64OriginalReferenceClassProof.OriginalField(app, original) ||
                !CaptureDescriptor(app, rawType, input)) return false;
            input.AddRange([field, original, field.Name, field.Attributes, field.Offset, field.FieldType,
                original.nameIndex, original.token, original.typeIndex.Value]);
        }
        genericTables = tables;
        return true;

        bool Contains(ReadOnlySpan<ulong> pointers)
        {
            foreach (var pointer in pointers) if (pointer == method.UnderlyingPointer) return true;
            return false;
        }
    }

    private static bool OriginalOwnerDescriptor(TypeAnalysisContext owner)
    {
        var app = owner.AppContext;
        if (!owner.IsValueType)
            return ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, owner.Definition!.RawType), owner);
        return owner.Definition is { IsValueType: true,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } raw } definition &&
               X64OriginalReferenceClassProof.CoherentDescriptor(raw) &&
               X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) &&
               definition.TypeIndex.Value >= 0 && raw.Datapoint == (ulong)definition.TypeIndex.Value &&
               raw.Data.Dummy == raw.Datapoint && ReferenceEquals(app.ResolveIl2CppType(raw), owner);
    }

    internal static bool OrdinaryField(FieldAnalysisContext field) =>
        field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes && field.Offset == field.DefaultOffset &&
        field.OverrideFieldType == null && ReferenceEquals(field.FieldType, field.DefaultFieldType) &&
        !field.UseOverrideConstantValue && field.StaticArrayInitialValue.Length == 0 &&
        (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA | FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) == 0 &&
        field.BackingData?.Field.RawFieldType is { NumMods: 0, Byref: 0, Pinned: 0 };

    internal static bool CaptureDescriptor(ApplicationAnalysisContext app, Il2CppType raw, List<object> input)
    {
        if (!X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            !app.Binary.TryGetTypeVirtualAddress(raw, out var address) || address > ulong.MaxValue - 11) return false;
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var image = app.Binary.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - 12 || app.Binary.MapVirtualAddressToRaw(address + 11, false) != offset + 11) return false;
        input.AddRange([raw, address, raw.Datapoint, raw.Bits, raw.Data.Dummy, raw.Attrs, raw.Type,
            raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType, Convert.ToBase64String(image.Slice(checked((int)offset), 12).ToArray())]);
        return true;
    }

    internal static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition output) =>
        method.GetExtraData<Evidence>(EvidenceKey) is { } proof && proof.IsUnchanged() && MatchesDeclaration(method, output);

    internal static bool MatchesDeclaration(MethodAnalysisContext method, MethodDefinition output)
    {
        if (method.DeclaringType is not { Definition: { } original } owner ||
            owner.GetExtraData<TypeDefinition>("AsmResolverType") is not { } declaration ||
            owner.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") is not { ManifestModule: { } module } assembly ||
            !ReferenceEquals(declaration.DeclaringModule, module) || !ReferenceEquals(module.Assembly, assembly) ||
            module.Name != owner.DeclaringAssembly.CleanAssemblyName + ".dll" ||
            assembly.Name != owner.DeclaringAssembly.Name || assembly.Version != owner.DeclaringAssembly.Version ||
            (assembly.Culture?.ToString() ?? "") != (owner.DeclaringAssembly.Culture ?? "") ||
            (uint)assembly.Attributes != owner.DeclaringAssembly.Flags ||
            (uint)assembly.HashAlgorithm != owner.DeclaringAssembly.HashAlgorithm ||
            !(assembly.PublicKey ?? []).SequenceEqual(owner.DeclaringAssembly.PublicKey ?? []) ||
            !ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output) ||
            !ReferenceEquals(output.DeclaringType, declaration) ||
            output.Name != method.Name || (ushort)output.Attributes != (ushort)method.Attributes ||
            (ushort)output.ImplAttributes != (ushort)method.ImplAttributes || output.GenericParameters.Count != 0 ||
            output.Signature is not { Attributes: CallingConventionAttributes.Default, HasThis: false, ExplicitThis: false,
                GenericParameterCount: 0, ParameterTypes.Count: 0, SentinelParameterTypes.Count: 0 } signature ||
            !SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) ||
            output.ParameterDefinitions.Count != 0 || declaration.Name != owner.Name ||
            (declaration.Namespace?.ToString() ?? "") != owner.Namespace || (uint)declaration.Attributes != (uint)owner.Attributes ||
            declaration.GenericParameters.Count != 0 ||
            !ReferenceEquals(declaration.DeclaringType, owner.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
            !SignatureComparer.Default.Equals(declaration.BaseType, owner.BaseType?.ToTypeSignature().ToTypeDefOrRef()) ||
            declaration.Fields.Count != owner.Fields.Count) return false;
        var expected = new TypeDefinition(owner.Namespace, owner.Name,
            (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)owner.Attributes);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(original, expected);
        if (declaration.ClassLayout?.PackingSize != expected.ClassLayout?.PackingSize ||
            declaration.ClassLayout?.ClassSize != expected.ClassLayout?.ClassSize ||
            declaration.ClassLayout != null && !ReferenceEquals(declaration.ClassLayout.Parent, declaration)) return false;
        for (var ordinal = 0; ordinal < owner.Fields.Count; ordinal++)
        {
            var field = owner.Fields[ordinal];
            var written = declaration.Fields[ordinal];
            if (!ReferenceEquals(field.GetExtraData<FieldDefinition>("AsmResolverField"), written) ||
                !ReferenceEquals(written.DeclaringType, declaration) || written.Name != field.Name ||
                (ushort)written.Attributes != (ushort)field.Attributes ||
                written.Signature is not { Attributes: CallingConventionAttributes.Field } fieldSignature ||
                !SignatureComparer.Default.Equals(fieldSignature.FieldType, field.FieldType.ToTypeSignature()) ||
                written.FieldOffset != (declaration.IsExplicitLayout && !field.IsStatic ? field.Offset : (int?)null)) return false;
        }
        return true;
    }
}
