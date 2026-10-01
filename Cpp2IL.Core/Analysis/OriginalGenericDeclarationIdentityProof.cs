using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Authenticates original v29 generic declaration rows and their owner contexts.
/// Constraint descriptor semantics, native sharing, and recovered behavior require
/// separate evidence. A saved proof must be revalidated before consumption.
/// </summary>
internal static class OriginalGenericDeclarationIdentityProof
{
    private const int ContainerBytes = 16;
    private const int ParameterBytes = 16;

    internal sealed class ParameterBinding(
        Il2CppMetadata.GenericParameterOrigin origin,
        GenericParameterTypeAnalysisContext context,
        int[] constraintIndices,
        TypeAnalysisContext[] constraintContexts)
    {
        private readonly int[] _constraintIndices = (int[])constraintIndices.Clone();
        private readonly TypeAnalysisContext[] _constraintContexts = (TypeAnalysisContext[])constraintContexts.Clone();

        internal Il2CppMetadata.GenericParameterOrigin Origin { get; } = origin;
        internal GenericParameterTypeAnalysisContext Context { get; } = context;
        internal ReadOnlySpan<int> ConstraintIndices => _constraintIndices;
        internal ReadOnlySpan<TypeAnalysisContext> ConstraintContexts => _constraintContexts;
    }

    internal sealed class Evidence(
        HasGenericParameters owner,
        Il2CppMetadata metadata,
        Il2CppMetadata.GenericDeclarationTableOrigin table,
        Il2CppMetadata.GenericContainerOrigin container,
        Il2CppGenericContainer definition,
        ParameterBinding[] parameters)
    {
        private readonly ParameterBinding[] _parameters = (ParameterBinding[])parameters.Clone();

        internal HasGenericParameters Owner { get; } = owner;
        internal Il2CppMetadata Metadata { get; } = metadata;
        internal Il2CppMetadata.GenericDeclarationTableOrigin Table { get; } = table;
        internal Il2CppMetadata.GenericContainerOrigin Container { get; } = container;
        internal Il2CppGenericContainer Definition { get; } = definition;
        internal ReadOnlySpan<ParameterBinding> Parameters => _parameters;

        internal bool Matches()
        {
            if (TryIdentify(Owner) is not { } current ||
                !ReferenceEquals(Metadata, current.Metadata) || Table != current.Table ||
                Container != current.Container || !ReferenceEquals(Definition, current.Definition) ||
                Parameters.Length != current.Parameters.Length)
                return false;
            for (var index = 0; index < Parameters.Length; index++)
            {
                var saved = Parameters[index];
                var fresh = current.Parameters[index];
                if (saved.Origin != fresh.Origin || !ReferenceEquals(saved.Context, fresh.Context) ||
                    !saved.ConstraintIndices.SequenceEqual(fresh.ConstraintIndices) ||
                    saved.ConstraintContexts.Length != fresh.ConstraintContexts.Length)
                    return false;
                for (var constraint = 0; constraint < saved.ConstraintContexts.Length; constraint++)
                    if (!ReferenceEquals(saved.ConstraintContexts[constraint], fresh.ConstraintContexts[constraint]))
                        return false;
            }
            return true;
        }
    }

    internal static Evidence? TryIdentify(HasGenericParameters owner)
    {
        try
        {
            var app = owner.AppContext;
            var metadata = app.Metadata;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || metadata.MetadataVersion != 29 ||
                !AuthenticateTables(metadata, out var table) ||
                !TryBindOwner(owner, out var containerIndex, out var ownerIndex, out var isMethod) ||
                !metadata.TryGetGenericContainerOrigin(containerIndex, out var container) ||
                container.OwnerIndex != ownerIndex || container.IsGenericMethod != isMethod ||
                container.GenericParameterCount <= 0 || container.GenericParameterStart < 0 ||
                container.GenericParameterStart > table.ParameterCount - container.GenericParameterCount)
                return null;

            var definition = metadata.GetGenericContainerFromIndex(
                Il2CppVariableWidthIndex<Il2CppGenericContainer>.MakeTemporaryForFixedWidthUsage(containerIndex));
            var contexts = owner.GenericParameters;
            if (contexts.Count != container.GenericParameterCount)
                return null;
            var parameters = new ParameterBinding[contexts.Count];
            for (var position = 0; position < parameters.Length; position++)
            {
                var ordinal = container.GenericParameterStart + position;
                if (!metadata.TryGetGenericParameterOrigin(ordinal, out var parameter) ||
                    parameter.OwnerIndex != containerIndex || parameter.PositionInOwner != position)
                    return null;
                var original = metadata.GetGenericParameterFromIndex(
                    Il2CppVariableWidthIndex<Il2CppGenericParameter>.MakeTemporaryForFixedWidthUsage(ordinal));
                if (contexts[position] is not { } context ||
                    !ReferenceEquals(context.Definition, original) || !ReferenceEquals(context.Owner, owner) ||
                    !ReferenceEquals(context.AppContext, app) || context.Index != position ||
                    context.Type != (isMethod ? Il2CppTypeEnum.IL2CPP_TYPE_MVAR : Il2CppTypeEnum.IL2CPP_TYPE_VAR) ||
                    context.Name != context.DefaultName || context.DefaultName != original.Name ||
                    context.Namespace != "" || context.Attributes != context.DefaultAttributes ||
                    (ushort)context.DefaultAttributes != parameter.Flags ||
                    !CurrentNameMatches(metadata, parameter.NameIndex, context.DefaultName))
                    return null;

                var indices = new int[parameter.ConstraintsCount];
                var constraints = new TypeAnalysisContext[indices.Length];
                if (context.ConstraintTypes.Count != indices.Length)
                    return null;
                for (var index = 0; index < indices.Length; index++)
                {
                    if (!metadata.TryGetGenericConstraintOrigin(parameter.ConstraintsStart + index, out var typeIndex))
                        return null;
                    var resolved = app.ResolveIl2CppType(app.Binary.GetType(
                        Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(typeIndex)));
                    if (resolved == null || !ReferenceEquals(context.ConstraintTypes[index], resolved))
                        return null;
                    indices[index] = typeIndex;
                    constraints[index] = resolved;
                }
                parameters[position] = new(parameter, context, indices, constraints);
            }
            return new(owner, metadata, table, container, definition, parameters);
        }
        catch (Exception failure) when (failure is ArgumentException or IndexOutOfRangeException or
                                       InvalidOperationException or IOException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryBindOwner(HasGenericParameters owner, out int containerIndex,
        out int ownerIndex, out bool isMethod)
    {
        containerIndex = ownerIndex = -1;
        isMethod = false;
        var app = owner.AppContext;
        if (owner.GetType() == typeof(MethodAnalysisContext) && owner is MethodAnalysisContext
            { Definition: { } method, DeclaringType: { Definition: { } declaringDefinition } declaring } context)
        {
            if (method.genericContainerIndex.IsNull ||
                !X64OriginalReferenceClassProof.OriginalMethod(app, method) ||
                !X64OriginalReferenceClassProof.OriginalType(app, declaringDefinition) ||
                !ReferenceEquals(method.DeclaringType, declaringDefinition) ||
                !ReferenceEquals(app.ResolveContextForMethod(method), context) ||
                !ReferenceEquals(app.ResolveContextForType(declaringDefinition), declaring) ||
                declaring.Methods.Count(candidate => ReferenceEquals(candidate, context)) != 1)
                return false;
            containerIndex = method.genericContainerIndex.Value;
            ownerIndex = method.MethodIndex.Value;
            isMethod = true;
            return true;
        }
        if (owner.GetType() == typeof(TypeAnalysisContext) && owner is TypeAnalysisContext
            { Definition: { } type } typeContext && !type.GenericContainerIndex.IsNull &&
            X64OriginalReferenceClassProof.OriginalType(app, type) &&
            ReferenceEquals(app.ResolveContextForType(type), typeContext))
        {
            containerIndex = type.GenericContainerIndex.Value;
            ownerIndex = type.TypeIndex.Value;
            return true;
        }
        return false;
    }

    private static bool AuthenticateTables(Il2CppMetadata metadata,
        out Il2CppMetadata.GenericDeclarationTableOrigin origin)
    {
        origin = default;
        if (!metadata.TryGetGenericDeclarationTableOrigin(out origin))
            return false;
        if (metadata.metadataHeader is not { } header)
            return false;
        var rawHeader = metadata.ReadByteArrayAtRawAddress(0, 128);
        if (rawHeader.Length != 128 || BinaryPrimitives.ReadUInt32LittleEndian(rawHeader) != Il2CppMetadata.MetadataMagic ||
            BinaryPrimitives.ReadInt32LittleEndian(rawHeader.AsSpan(4)) != 29 ||
            !Section(metadata, header.genericParameters, origin.ParametersOffset, origin.ParametersBytes,
                origin.ParameterCount, ParameterBytes, rawHeader.AsSpan(104, 8)) ||
            !Section(metadata, header.genericParameterConstraints, origin.ConstraintsOffset, origin.ConstraintsBytes,
                origin.ConstraintCount, sizeof(int), rawHeader.AsSpan(112, 8)) ||
            !Section(metadata, header.genericContainers, origin.ContainersOffset, origin.ContainersBytes,
                origin.ContainerCount, ContainerBytes, rawHeader.AsSpan(120, 8)))
            return false;

        var containers = metadata.ReadByteArrayAtRawAddress(origin.ContainersOffset, origin.ContainersBytes);
        var parameters = metadata.ReadByteArrayAtRawAddress(origin.ParametersOffset, origin.ParametersBytes);
        var constraints = metadata.ReadByteArrayAtRawAddress(origin.ConstraintsOffset, origin.ConstraintsBytes);
        for (var index = 0; index < origin.ContainerCount; index++)
        {
            if (!metadata.TryGetGenericContainerOrigin(index, out var saved)) return false;
            var current = metadata.GetGenericContainerFromIndex(
                Il2CppVariableWidthIndex<Il2CppGenericContainer>.MakeTemporaryForFixedWidthUsage(index));
            var raw = containers.AsSpan(index * ContainerBytes, ContainerBytes);
            if (current.ownerIndex != saved.OwnerIndex || current.genericParameterCount != saved.GenericParameterCount ||
                current.isGenericMethod != saved.IsGenericMethod || current.genericParameterStart.Value != saved.GenericParameterStart ||
                BinaryPrimitives.ReadInt32LittleEndian(raw) != saved.OwnerIndex ||
                BinaryPrimitives.ReadInt32LittleEndian(raw[4..]) != saved.GenericParameterCount ||
                BinaryPrimitives.ReadInt32LittleEndian(raw[8..]) != (saved.IsGenericMethod ? 1 : 0) ||
                BinaryPrimitives.ReadInt32LittleEndian(raw[12..]) != saved.GenericParameterStart)
                return false;
        }
        for (var index = 0; index < origin.ParameterCount; index++)
        {
            if (!metadata.TryGetGenericParameterOrigin(index, out var saved)) return false;
            var current = metadata.GetGenericParameterFromIndex(
                Il2CppVariableWidthIndex<Il2CppGenericParameter>.MakeTemporaryForFixedWidthUsage(index));
            var raw = parameters.AsSpan(index * ParameterBytes, ParameterBytes);
            if (current.ownerIndex.Value != saved.OwnerIndex || current.nameIndex != saved.NameIndex ||
                current.constraintsStart != saved.ConstraintsStart || current.constraintsCount != saved.ConstraintsCount ||
                current.genericParameterIndexInOwner != saved.PositionInOwner || current.flags != saved.Flags ||
                BinaryPrimitives.ReadInt32LittleEndian(raw) != saved.OwnerIndex ||
                BinaryPrimitives.ReadInt32LittleEndian(raw[4..]) != saved.NameIndex ||
                BinaryPrimitives.ReadInt16LittleEndian(raw[8..]) != saved.ConstraintsStart ||
                BinaryPrimitives.ReadInt16LittleEndian(raw[10..]) != saved.ConstraintsCount ||
                BinaryPrimitives.ReadUInt16LittleEndian(raw[12..]) != saved.PositionInOwner ||
                BinaryPrimitives.ReadUInt16LittleEndian(raw[14..]) != saved.Flags ||
                saved.ConstraintsCount < 0 || saved.ConstraintsCount > 0 &&
                (saved.ConstraintsStart < 0 || saved.ConstraintsStart > origin.ConstraintCount - saved.ConstraintsCount))
                return false;
        }
        for (var index = 0; index < origin.ConstraintCount; index++)
            if (!metadata.TryGetGenericConstraintOrigin(index, out var typeIndex) ||
                metadata.constraintIndices[index].Value != typeIndex ||
                BinaryPrimitives.ReadInt32LittleEndian(constraints.AsSpan(index * sizeof(int))) != typeIndex)
                return false;
        return true;
    }

    private static bool Section(Il2CppMetadata metadata, Il2CppGlobalMetadataSectionHeader current,
        int offset, int bytes, int count, int stride, ReadOnlySpan<byte> raw) =>
        current != null && offset >= 0 && bytes >= 0 && count >= 0 && offset <= metadata.Length - bytes &&
        (long)count * stride == bytes && current.Offset == offset && current.Size == bytes &&
        BinaryPrimitives.ReadInt32LittleEndian(raw) == offset && BinaryPrimitives.ReadInt32LittleEndian(raw[4..]) == bytes;

    private static bool CurrentNameMatches(Il2CppMetadata metadata, int index, string expected)
    {
        if (metadata.metadataHeader?.@string is not { } section)
            return false;
        var raw = metadata.ReadByteArrayAtRawAddress(24, 8);
        if (section.Offset < 0 || section.Size < 0 || section.Offset > metadata.Length - section.Size ||
            raw.Length != 8 || BinaryPrimitives.ReadInt32LittleEndian(raw) != section.Offset ||
            BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4)) != section.Size || index < 0 || index >= section.Size)
            return false;
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        if (expectedBytes.Length >= section.Size - index)
            return false;
        var bytes = metadata.ReadByteArrayAtRawAddress((long)section.Offset + index, expectedBytes.Length + 1);
        return bytes.Length == expectedBytes.Length + 1 && bytes[^1] == 0 &&
               bytes.AsSpan(0, expectedBytes.Length).SequenceEqual(expectedBytes);
    }
}
