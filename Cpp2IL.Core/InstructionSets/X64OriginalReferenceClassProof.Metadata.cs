using System;
using System.Buffers.Binary;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64OriginalReferenceClassProof
{
    // Fixed v29 identity layouts from the matching metadata readers. Native
    // reference descriptors have an eight-byte union followed by packed flags.
    private const int AssemblyIdentityBytes = 64;
    private const int ImageIdentityWords = 10;
    private const int PropertyIdentityWords = 5;
    private const int EventIdentityWords = 6;
    private const int CodegenModuleIdentityWords = 17;

    private static bool CanonicalReference(AssemblyAnalysisContext ownerAssembly, AssemblyAnalysisContext dependency)
    {
        if (!ReferenceEquals(ownerAssembly.AppContext, dependency.AppContext) ||
            !CanonicalAssembly(ownerAssembly) || !CanonicalAssembly(dependency))
            return false;
        var metadata = ownerAssembly.AppContext.Metadata;
        var start = ownerAssembly.Definition!.ReferencedAssemblyStart;
        var count = ownerAssembly.Definition.ReferencedAssemblyCount;
        var section = metadata.metadataHeader.referencedAssemblies;
        if (start < 0 || count <= 0 || start > metadata.referencedAssemblies.Length - count ||
            section.Size % sizeof(int) != 0 || start > section.Size / sizeof(int) - count)
            return false;

        var indices = metadata.referencedAssemblies.AsSpan(start, count).ToArray();
        var offset = metadata.metadataHeader.referencedAssemblies.Offset + (long)start * sizeof(int);
        var originalIndices = metadata.ReadClassArrayAtRawAddr<int>(offset, count);
        return originalIndices.SequenceEqual(indices) &&
            indices.All(index => index >= 0 && index < metadata.AssemblyDefinitions.Length) &&
            indices.Count(index => ReferenceEquals(metadata.AssemblyDefinitions[index], dependency.Definition)) == 1;
    }

    internal static bool CanonicalAssembly(AssemblyAnalysisContext assembly)
    {
        var app = assembly.AppContext;
        var metadata = app.Metadata;
        if (assembly.Definition is not { } definition || metadata.MetadataVersion != 29 ||
            metadata.AssemblyDefinitions.Length is < 1 or > 65536 || metadata.imageDefinitions.Length is < 1 or > 65536)
            return false;
        var assemblyIndex = Array.IndexOf(metadata.AssemblyDefinitions, definition);
        if (assemblyIndex < 0 || definition.ImageIndex < 0 || definition.ImageIndex >= metadata.imageDefinitions.Length)
            return false;
        var image = metadata.imageDefinitions[definition.ImageIndex];
        if (image.assemblyIndex != assemblyIndex || !ReferenceEquals(app.ResolveContextForAssembly(definition), assembly) ||
            !ReferenceEquals(app.ResolveContextForAssembly(image), assembly) ||
            app.Assemblies.Count(candidate => ReferenceEquals(candidate, assembly)) != 1 ||
            app.Assemblies.Count(candidate => candidate.Name == assembly.Name) != 1 ||
            !ReferenceEquals(app.GetAssemblyByName(assembly.Name), assembly) || !UnchangedAssemblyIdentity(assembly))
            return false;

        if (assembly.ManifestModule.Name != image.Name || assembly.CodeGenModule is not { } module ||
            !OriginalCodegenModule(app, module) ||
            module.Name != image.Name || app.Binary.GetCodegenModuleIndex(module) < 0 ||
            !ReferenceEquals(app.Binary.GetCodegenModuleByName(image.Name!), module) ||
            app.Binary.ReadStringToNull(app.Binary.MapVirtualAddressToRaw(module.moduleName, false)) != module.Name ||
            !image.Types.SequenceEqual(assembly.Types.Select(type => type.Definition)))
            return false;

        var header = metadata.ReadReadable<Il2CppGlobalMetadataHeader>(0);
        if (!OriginalIdentitySections(metadata, header) ||
            header.assemblies.Size != (long)metadata.AssemblyDefinitions.Length * AssemblyIdentityBytes ||
            header.images.Size != (long)metadata.imageDefinitions.Length * ImageIdentityWords * sizeof(uint))
            return false;
        var offset = header.assemblies.Offset + (long)assemblyIndex * AssemblyIdentityBytes;
        var original = metadata.ReadReadable<Il2CppAssemblyDefinition>(offset);
        var name = definition.AssemblyName;
        var originalName = original.AssemblyName;
        if (definition.ImageIndex != original.ImageIndex || definition.Token != original.Token ||
            definition.ReferencedAssemblyStart != original.ReferencedAssemblyStart || definition.ReferencedAssemblyCount != original.ReferencedAssemblyCount ||
            name.nameIndex != originalName.nameIndex || name.cultureIndex != originalName.cultureIndex ||
            name.publicKeyIndex != originalName.publicKeyIndex || name.hash_alg != originalName.hash_alg || name.hash_len != originalName.hash_len ||
            name.flags != originalName.flags || name.major != originalName.major || name.minor != originalName.minor ||
            name.build != originalName.build || name.revision != originalName.revision || name.publicKeyToken != originalName.publicKeyToken)
            return false;

        var imageOffset = header.images.Offset + (long)definition.ImageIndex * ImageIdentityWords * sizeof(uint);
        var row = metadata.ReadClassArrayAtRawAddr<uint>(imageOffset, ImageIdentityWords);
        return row.SequenceEqual(new uint[]
        {
            unchecked((uint)image.nameIndex), unchecked((uint)image.assemblyIndex), unchecked((uint)image.firstTypeIndex.Value), image.typeCount,
            unchecked((uint)image.exportedTypeStart.Value), image.exportedTypeCount, unchecked((uint)image.entryPointIndex.Value), image.token,
            unchecked((uint)image.customAttributeStart), image.customAttributeCount
        });
    }

    private static bool UnchangedAssemblyIdentity(AssemblyAnalysisContext assembly) =>
        assembly.Name == assembly.DefaultName && assembly.Version == assembly.DefaultVersion &&
        assembly.Flags == assembly.DefaultFlags && assembly.HashAlgorithm == assembly.DefaultHashAlgorithm &&
        (assembly.Culture ?? "") == (assembly.DefaultCulture ?? "") &&
        (assembly.PublicKey ?? []).SequenceEqual(assembly.DefaultPublicKey ?? []) &&
        (assembly.PublicKeyToken ?? []).SequenceEqual(assembly.DefaultPublicKeyToken ?? []);

    private static bool OriginalIdentitySections(LibCpp2IL.Metadata.Il2CppMetadata metadata, Il2CppGlobalMetadataHeader header)
    {
        var current = metadata.metadataHeader;
        var sections = new[]
        {
            (current.assemblies, header.assemblies), (current.images, header.images),
            (current.typeDefinitions, header.typeDefinitions), (current.methods, header.methods),
            (current.fields, header.fields), (current.parameters, header.parameters),
            (current.properties, header.properties), (current.events, header.events),
            (current.referencedAssemblies, header.referencedAssemblies)
        };
        return sections.All(pair => pair.Item1.Offset == pair.Item2.Offset && pair.Item1.Size == pair.Item2.Size &&
            pair.Item2.Offset >= 0 && pair.Item2.Size >= 0 && pair.Item2.Offset <= metadata.Length - pair.Item2.Size);
    }

    // The primitive rows use the fixed-width v29 readers' field order. They do
    // not restart the metadata parser's process-wide variable-index sessions.
    internal static bool OriginalType(ApplicationAnalysisContext app, Il2CppTypeDefinition definition)
    {
        var index = definition.TypeIndex.Value;
        if (index < 0 || index >= app.Metadata.TypeDefinitionCount ||
            !ReferenceEquals(app.Metadata.typeDefs[index], definition)) return false;
        var expected = new uint[]
        {
            unchecked((uint)definition.NameIndex), unchecked((uint)definition.NamespaceIndex), unchecked((uint)definition.ByvalTypeIndex.Value),
            unchecked((uint)definition.DeclaringTypeIndex.Value), unchecked((uint)definition.ParentIndex.Value), unchecked((uint)definition.ElementTypeIndex),
            unchecked((uint)definition.GenericContainerIndex.Value), definition.Flags, unchecked((uint)definition.FirstFieldIdx.Value),
            unchecked((uint)definition.FirstMethodIdx.Value), unchecked((uint)definition.FirstEventId.Value), unchecked((uint)definition.FirstPropertyId.Value),
            unchecked((uint)definition.NestedTypesStart.Value), unchecked((uint)definition.InterfacesStart.Value), unchecked((uint)definition.VtableStart),
            unchecked((uint)definition.InterfaceOffsetsStart.Value), (uint)definition.MethodCount | (uint)definition.PropertyCount << 16,
            (uint)definition.FieldCount | (uint)definition.EventCount << 16, (uint)definition.NestedTypeCount | (uint)definition.VtableCount << 16,
            (uint)definition.InterfacesCount | (uint)definition.InterfaceOffsetsCount << 16, definition.Bitfield, definition.Token
        };
        return OriginalRow(app, app.Metadata.metadataHeader.typeDefinitions, index, app.Metadata.TypeDefinitionCount, expected);
    }

    internal static bool OriginalMethod(ApplicationAnalysisContext app, Il2CppMethodDefinition definition)
    {
        var index = definition.MethodIndex.Value;
        if (index < 0 || index >= app.Metadata.MethodDefinitionCount ||
            !ReferenceEquals(app.Metadata.methodDefs[index], definition)) return false;
        var expected = new uint[]
        {
            unchecked((uint)definition.nameIndex), unchecked((uint)definition.declaringTypeIdx.Value), unchecked((uint)definition.returnTypeIdx.Value),
            unchecked((uint)definition.parameterStart.Value), unchecked((uint)definition.genericContainerIndex.Value), definition.token,
            (uint)definition.flags | (uint)definition.iflags << 16, (uint)definition.slot | (uint)definition.parameterCount << 16
        };
        return OriginalRow(app, app.Metadata.metadataHeader.methods, index, app.Metadata.MethodDefinitionCount, expected);
    }

    private static bool OriginalField(ApplicationAnalysisContext app, Il2CppFieldDefinition definition)
    {
        var section = app.Metadata.metadataHeader.fields;
        var index = definition.FieldIndex.Value;
        var expected = new uint[] { unchecked((uint)definition.nameIndex), unchecked((uint)definition.typeIndex.Value), definition.token };
        return index >= 0 && index < section.Size / (expected.Length * sizeof(uint)) &&
            ReferenceEquals(app.Metadata.GetFieldDefinitionFromIndex(definition.FieldIndex), definition) &&
            OriginalRow(app, section, index, section.Size / (expected.Length * sizeof(uint)), expected);
    }

    internal static bool OriginalParameter(ApplicationAnalysisContext app, Il2CppMethodDefinition method,
        int parameterIndex, Il2CppParameterDefinition definition)
    {
        var section = app.Metadata.metadataHeader.parameters;
        var index = (long)method.parameterStart.Value + parameterIndex;
        var expected = new uint[] { unchecked((uint)definition.nameIndex), definition.token, unchecked((uint)definition.typeIndex.Value) };
        var count = section.Size / (expected.Length * sizeof(uint));
        return index >= 0 && index < count && OriginalRow(app, section, (int)index, count, expected);
    }

    private static bool OriginalProperty(TypeAnalysisContext type, int ordinal, Il2CppPropertyDefinition definition)
    {
        var section = type.AppContext.Metadata.metadataHeader.properties;
        var index = (long)type.Definition!.FirstPropertyId.Value + ordinal;
        var count = section.Size / (PropertyIdentityWords * sizeof(uint));
        var expected = new uint[]
        {
            unchecked((uint)definition.nameIndex), unchecked((uint)definition.get.Value),
            unchecked((uint)definition.set.Value), definition.attrs, definition.token
        };
        return ordinal >= 0 && ordinal < type.Definition.PropertyCount && index >= 0 && index < count &&
            definition.PropertyIndex == index && ReferenceEquals(definition.DeclaringType, type.Definition) &&
            OriginalRow(type.AppContext, section, (int)index, count, expected);
    }

    private static bool OriginalEvent(TypeAnalysisContext type, int ordinal, Il2CppEventDefinition definition)
    {
        var section = type.AppContext.Metadata.metadataHeader.events;
        var index = (long)type.Definition!.FirstEventId.Value + ordinal;
        var count = section.Size / (EventIdentityWords * sizeof(uint));
        var expected = new uint[]
        {
            unchecked((uint)definition.nameIndex), unchecked((uint)definition.typeIndex.Value),
            unchecked((uint)definition.add.Value), unchecked((uint)definition.remove.Value),
            unchecked((uint)definition.raise.Value), definition.token
        };
        return ordinal >= 0 && ordinal < type.Definition.EventCount && index >= 0 && index < count &&
            ReferenceEquals(definition.DeclaringType, type.Definition) &&
            OriginalRow(type.AppContext, section, (int)index, count, expected);
    }

    private static bool OriginalRow(ApplicationAnalysisContext app, Il2CppGlobalMetadataSectionHeader section,
        int index, int count, uint[] expected)
    {
        if (app.MetadataVersion != 29 || index < 0 || index >= count || section.Offset < 0 || section.Size < 0 ||
            section.Size != (long)count * expected.Length * sizeof(uint) ||
            section.Offset > app.Metadata.Length - section.Size) return false;
        var offset = section.Offset + (long)index * expected.Length * sizeof(uint);
        return app.Metadata.ReadClassArrayAtRawAddr<uint>(offset, expected.Length).SequenceEqual(expected);
    }

    private static bool OriginalDescriptor(ApplicationAnalysisContext app, Il2CppType raw)
    {
        if (!app.Binary.TryGetTypeVirtualAddress(raw, out var address) || address > ulong.MaxValue - 11)
            return false;
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var bytes = app.Binary.GetRawBinaryContent();
        return offset >= 0 && offset <= bytes.Length - 12 &&
            app.Binary.MapVirtualAddressToRaw(address + 11, false) == offset + 11 &&
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice((int)offset, 8)) == raw.Datapoint &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice((int)offset + 8, 4)) == raw.Bits;
    }

    private static bool OriginalCodegenModule(ApplicationAnalysisContext app, Il2CppCodeGenModule module)
    {
        // The v29 x64 module reader has seventeen pointer-sized words. Keep the
        // original module record as registration evidence, independent of its
        // cached name or mutable method-pointer array.
        if (app.MetadataVersion != 29 || app.Binary.PointerSizeBytes != sizeof(ulong) ||
            !app.Binary.TryGetCodegenModuleVirtualAddress(module, out var address)) return false;
        var expected = new ulong[]
        {
            module.moduleName, unchecked((ulong)module.methodPointerCount), module.methodPointers,
            unchecked((ulong)module.adjustorThunkCount), module.adjustorThunks, module.invokerIndices,
            module.reversePInvokeWrapperCount, module.reversePInvokeWrapperIndices,
            unchecked((ulong)module.rgctxRangesCount), module.pRgctxRanges,
            unchecked((ulong)module.rgctxsCount), module.rgctxs, module.debuggerMetadata,
            module.moduleInitializer, module.staticConstructorTypeIndices, module.metadataRegistration, module.codeRegistration
        };
        var bytes = app.Binary.GetRawBinaryContent();
        var size = CodegenModuleIdentityWords * sizeof(ulong);
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        if (address > ulong.MaxValue - (ulong)(size - 1) || offset < 0 || offset > bytes.Length - size ||
            app.Binary.MapVirtualAddressToRaw(address + (ulong)(size - 1), false) != offset + size - 1)
            return false;
        for (var index = 0; index < expected.Length; index++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice((int)offset + index * sizeof(ulong), sizeof(ulong))) != expected[index])
                return false;
        return true;
    }

    internal static bool OriginalMethodPointer(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.Definition is not { } definition || !OriginalMethod(app, definition) ||
            method.DeclaringType?.DeclaringAssembly is not { CodeGenModule: { } module } assembly ||
            !CanonicalAssembly(assembly) || (definition.token & 0xFF000000) != 0x06000000)
            return false;
        var index = (long)(definition.token & 0x00FFFFFF) - 1;
        if (index < 0 || index >= module.methodPointerCount || module.methodPointers == 0 ||
            (ulong)index > (ulong.MaxValue - module.methodPointers) / sizeof(ulong)) return false;
        var address = module.methodPointers + (ulong)index * sizeof(ulong);
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var bytes = app.Binary.GetRawBinaryContent();
        var pointers = app.Binary.GetCodegenModuleMethodPointers(app.Binary.GetCodegenModuleIndex(module));
        var ordinaryPointer = address <= ulong.MaxValue - 7 && offset >= 0 && offset <= bytes.Length - sizeof(ulong) &&
            app.Binary.MapVirtualAddressToRaw(address + 7, false) == offset + 7 && index < pointers.Length &&
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice((int)offset, sizeof(ulong))) == method.UnderlyingPointer &&
            pointers[(int)index] == method.UnderlyingPointer;
        if (ordinaryPointer) return true;
        // The parser gives a generic definition its first original registered
        // variant. Its native body may therefore come from the generic pointer
        // table even when the module's MethodDef slot is empty.
        if (!app.Binary.ConcreteGenericMethods.TryGetValue(definition, out var references) || references.Count == 0)
            return false;
        var registrations = references.Select(reference =>
            app.Binary.TryGetGenericMethodRegistration(reference, out var origin) ? origin.TableIndex : -1).ToArray();
        if (registrations.Any(table => table < 0) || registrations.Distinct().Count() != registrations.Length)
            return false;
        var first = references[Array.IndexOf(registrations, registrations.Min())];
        return OriginalGenericMethodReference(app, first, method.UnderlyingPointer) &&
            app.ConcreteGenericMethodsByRef.TryGetValue(first, out var concrete) &&
            ReferenceEquals(concrete.BaseMethodContext, method);
    }
}
