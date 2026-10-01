using System;

namespace LibCpp2IL.Metadata;

public partial class Il2CppMetadata
{
    public readonly record struct GenericDeclarationTableOrigin(
        int ContainerCount, int ParameterCount,
        int ContainersOffset, int ContainersBytes,
        int ParametersOffset, int ParametersBytes,
        int ConstraintsOffset, int ConstraintsBytes, int ConstraintCount);

    public readonly record struct GenericContainerOrigin(int Index, int OwnerIndex,
        int GenericParameterCount, bool IsGenericMethod, int GenericParameterStart);

    public readonly record struct GenericParameterOrigin(int Index, int OwnerIndex,
        int NameIndex, short ConstraintsStart, short ConstraintsCount,
        ushort PositionInOwner, ushort Flags);

    private readonly record struct RetainedGenericContainer(
        Il2CppGenericContainer Instance, GenericContainerOrigin Origin);

    private readonly record struct RetainedGenericParameter(
        Il2CppGenericParameter Instance, GenericParameterOrigin Origin);

    private sealed class GenericDeclarationOrigins(
        GenericDeclarationTableOrigin table,
        RetainedGenericContainer[] containers,
        RetainedGenericParameter[] parameters,
        int[] constraints)
    {
        internal GenericDeclarationTableOrigin Table { get; } = table;
        internal ReadOnlySpan<RetainedGenericContainer> Containers => containers;
        internal ReadOnlySpan<RetainedGenericParameter> Parameters => parameters;
        internal ReadOnlySpan<int> Constraints => constraints;
    }

    private readonly GenericDeclarationOrigins? _genericDeclarationOrigins;

    private GenericDeclarationOrigins CaptureGenericDeclarationOrigins()
    {
        var containers = new RetainedGenericContainer[genericContainers.Length];
        for (var ordinal = 0; ordinal < containers.Length; ordinal++)
        {
            var row = genericContainers[ordinal];
            containers[ordinal] = new(row, new(ordinal, row.ownerIndex,
                row.genericParameterCount, row.isGenericMethod, row.genericParameterStart.Value));
        }

        var parameters = new RetainedGenericParameter[genericParameters.Length];
        for (var ordinal = 0; ordinal < parameters.Length; ordinal++)
        {
            var row = genericParameters[ordinal];
            // Index is assigned lazily by container enumeration. It is not a
            // serialized field and does not belong to the original row values.
            parameters[ordinal] = new(row, new(ordinal, row.ownerIndex.Value,
                row.nameIndex, row.constraintsStart, row.constraintsCount,
                row.genericParameterIndexInOwner, row.flags));
        }

        var constraints = new int[constraintIndices.Length];
        for (var ordinal = 0; ordinal < constraints.Length; ordinal++)
            constraints[ordinal] = constraintIndices[ordinal].Value;

        var header = metadataHeader;
        var table = new GenericDeclarationTableOrigin(containers.Length, parameters.Length,
            header.genericContainers.Offset, header.genericContainers.Size,
            header.genericParameters.Offset, header.genericParameters.Size,
            header.genericParameterConstraints.Offset, header.genericParameterConstraints.Size,
            constraints.Length);
        return new(table, containers, parameters, constraints);
    }

    /// <summary>
    /// Returns constructor-time generic declaration counts and section bounds.
    /// Mutable header values and raw row bytes require separate authentication.
    /// </summary>
    public bool TryGetGenericDeclarationTableOrigin(out GenericDeclarationTableOrigin origin)
    {
        origin = default;
        if (!_hasFinishedInitialRead || _genericDeclarationOrigins is not { } original ||
            genericContainers is null || genericParameters is null || constraintIndices is null ||
            genericContainers.Length != original.Table.ContainerCount ||
            genericParameters.Length != original.Table.ParameterCount ||
            constraintIndices.Length != original.Table.ConstraintCount)
            return false;
        origin = original.Table;
        return true;
    }

    /// <summary>
    /// Returns initial serialized values only for the exact parsed container at
    /// this ordinal. Current mutable fields must be compared with these values.
    /// </summary>
    public bool TryGetGenericContainerOrigin(int ordinal, out GenericContainerOrigin origin)
    {
        origin = default;
        if (!TryGetGenericDeclarationTableOrigin(out _) || ordinal < 0 ||
            ordinal >= _genericDeclarationOrigins!.Containers.Length)
            return false;
        var original = _genericDeclarationOrigins.Containers[ordinal];
        if (!ReferenceEquals(original.Instance, genericContainers[ordinal])) return false;
        origin = original.Origin;
        return true;
    }

    /// <summary>
    /// Returns initial serialized values only for the exact parsed parameter at
    /// this ordinal. Lazy parameter Index assignment does not change its origin.
    /// </summary>
    public bool TryGetGenericParameterOrigin(int ordinal, out GenericParameterOrigin origin)
    {
        origin = default;
        if (!TryGetGenericDeclarationTableOrigin(out _) || ordinal < 0 ||
            ordinal >= _genericDeclarationOrigins!.Parameters.Length)
            return false;
        var original = _genericDeclarationOrigins.Parameters[ordinal];
        if (!ReferenceEquals(original.Instance, genericParameters[ordinal])) return false;
        origin = original.Origin;
        return true;
    }

    /// <summary>Returns an original constraint index independently of the mutable index array.</summary>
    public bool TryGetGenericConstraintOrigin(int ordinal, out int typeIndex)
    {
        typeIndex = default;
        if (!TryGetGenericDeclarationTableOrigin(out _) || ordinal < 0 ||
            ordinal >= _genericDeclarationOrigins!.Constraints.Length)
            return false;
        typeIndex = _genericDeclarationOrigins.Constraints[ordinal];
        return true;
    }
}
