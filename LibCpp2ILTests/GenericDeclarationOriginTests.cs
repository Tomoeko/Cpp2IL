#nullable enable

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetRipper.Primitives;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using Xunit;

namespace LibCpp2ILTests;

public class GenericDeclarationOriginTests
{
    [Fact]
    public void OriginsRetainSerializedValuesWhileCurrentRowsAndHeaderChange()
    {
        using var metadata = Create();
        Assert.True(metadata.TryGetGenericDeclarationTableOrigin(out var table));
        Assert.Equal(new Il2CppMetadata.GenericDeclarationTableOrigin(2, 2,
            0x300, 32, 0x340, 32, 0x380, 8, 2), table);
        Assert.True(metadata.TryGetGenericContainerOrigin(1, out var container));
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out var parameter));
        Assert.True(metadata.TryGetGenericConstraintOrigin(1, out var constraint));
        Assert.Equal(new Il2CppMetadata.GenericContainerOrigin(1, 23, 1, true, 1), container);
        Assert.Equal(new Il2CppMetadata.GenericParameterOrigin(1, 1, 0, 1, 1, 0, 16), parameter);
        Assert.Equal(29, constraint);

        var row = metadata.GetGenericContainerFromIndex(Index<Il2CppGenericContainer>(1));
        row.ownerIndex = 99; row.genericParameterCount = 7; row.isGenericMethod = false;
        row.genericParameterStart = Index<Il2CppGenericParameter>(99);
        var value = metadata.GetGenericParameterFromIndex(Index<Il2CppGenericParameter>(1));
        value.ownerIndex = Index<Il2CppGenericContainer>(99); value.nameIndex = 99;
        value.constraintsStart = 99; value.constraintsCount = 7;
        value.genericParameterIndexInOwner = 6; value.flags = 0;
        metadata.constraintIndices[1] = Index<Il2CppType>(99);
        metadata.metadataHeader.genericContainers.Offset = 0x400;
        metadata.metadataHeader.genericParameters.Size = 64;
        metadata.metadataHeader.genericParameterConstraints.Offset = 0x410;

        Assert.True(metadata.TryGetGenericDeclarationTableOrigin(out var retainedTable));
        Assert.True(metadata.TryGetGenericContainerOrigin(1, out var retainedContainer));
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out var retainedParameter));
        Assert.True(metadata.TryGetGenericConstraintOrigin(1, out var retainedConstraint));
        Assert.Equal(table, retainedTable);
        Assert.Equal(container, retainedContainer);
        Assert.Equal(parameter, retainedParameter);
        Assert.Equal(constraint, retainedConstraint);
    }

    [Fact]
    public void EqualValuedRowReplacementsCannotAcquireOriginalIdentity()
    {
        using var metadata = Create();
        var containers = Field<Il2CppGenericContainer[]>(metadata, "genericContainers");
        var parameters = Field<Il2CppGenericParameter[]>(metadata, "genericParameters");
        var container = containers[1]; var parameter = parameters[1];
        containers[1] = new Il2CppGenericContainer
        {
            ownerIndex = container.ownerIndex, genericParameterCount = container.genericParameterCount,
            isGenericMethod = container.isGenericMethod, genericParameterStart = container.genericParameterStart
        };
        parameters[1] = new Il2CppGenericParameter
        {
            ownerIndex = parameter.ownerIndex, nameIndex = parameter.nameIndex,
            constraintsStart = parameter.constraintsStart, constraintsCount = parameter.constraintsCount,
            genericParameterIndexInOwner = parameter.genericParameterIndexInOwner, flags = parameter.flags
        };
        Assert.True(metadata.TryGetGenericDeclarationTableOrigin(out _));
        Assert.False(metadata.TryGetGenericContainerOrigin(1, out _));
        Assert.False(metadata.TryGetGenericParameterOrigin(1, out _));
        Assert.True(metadata.TryGetGenericContainerOrigin(0, out _));
        Assert.True(metadata.TryGetGenericParameterOrigin(0, out _));
        containers[1] = container; parameters[1] = parameter;
        Assert.True(metadata.TryGetGenericContainerOrigin(1, out _));
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out _));
    }

    [Fact]
    public void SameOrderedArrayContainersPreserveRowIdentityAndLazyIndexAssignment()
    {
        using var metadata = Create();
        var containers = Field<Il2CppGenericContainer[]>(metadata, "genericContainers");
        var parameters = Field<Il2CppGenericParameter[]>(metadata, "genericParameters");
        SetField(metadata, "genericContainers", containers.ToArray());
        SetField(metadata, "genericParameters", parameters.ToArray());
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out var before));
        Assert.Same(parameters[1], containers[1].GenericParameters.Single());
        Assert.Equal(1, parameters[1].Index.Value);
        Assert.True(metadata.TryGetGenericContainerOrigin(1, out _));
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out var after));
        Assert.Equal(before, after);
        Assert.Equal(0, after.PositionInOwner);
    }

    [Theory]
    [InlineData("genericContainers")]
    [InlineData("genericParameters")]
    [InlineData("constraintIndices")]
    public void ChangedArrayCountsInvalidateAllDeclarationOrigins(string field)
    {
        using var metadata = Create();
        var original = (Array)typeof(Il2CppMetadata).GetField(field,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(metadata)!;
        var shortened = Array.CreateInstance(original.GetType().GetElementType()!, 1);
        Array.Copy(original, shortened, 1);
        foreach (var replacement in new object?[] { shortened, null })
        {
            SetField(metadata, field, replacement);
            Assert.False(metadata.TryGetGenericDeclarationTableOrigin(out _));
            Assert.False(metadata.TryGetGenericContainerOrigin(0, out _));
            Assert.False(metadata.TryGetGenericParameterOrigin(0, out _));
            Assert.False(metadata.TryGetGenericConstraintOrigin(0, out _));
        }
        SetField(metadata, field, original);
        Assert.True(metadata.TryGetGenericDeclarationTableOrigin(out _));
        Assert.True(metadata.TryGetGenericContainerOrigin(1, out _));
        Assert.True(metadata.TryGetGenericParameterOrigin(1, out _));
        Assert.True(metadata.TryGetGenericConstraintOrigin(1, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void OrdinalsOutsideOriginalTablesAreRejected(int ordinal)
    {
        using var metadata = Create();
        Assert.False(metadata.TryGetGenericContainerOrigin(ordinal, out _));
        Assert.False(metadata.TryGetGenericParameterOrigin(ordinal, out _));
        Assert.False(metadata.TryGetGenericConstraintOrigin(ordinal, out _));
    }

    [Fact]
    public void UnpublishedMetadataHasNoDeclarationOrigins()
    {
        var metadata = (Il2CppMetadata)RuntimeHelpers.GetUninitializedObject(typeof(Il2CppMetadata));
        Assert.False(metadata.TryGetGenericDeclarationTableOrigin(out _));
        Assert.False(metadata.TryGetGenericContainerOrigin(0, out _));
        Assert.False(metadata.TryGetGenericParameterOrigin(0, out _));
        Assert.False(metadata.TryGetGenericConstraintOrigin(0, out _));
    }

    private static Il2CppVariableWidthIndex<T> Index<T>(int value) where T : ReadableClass =>
        Il2CppVariableWidthIndex<T>.MakeTemporaryForFixedWidthUsage(value);
    private static T Field<T>(Il2CppMetadata metadata, string name) =>
        (T)typeof(Il2CppMetadata).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metadata)!;
    private static void SetField(Il2CppMetadata metadata, string name, object? value) =>
        typeof(Il2CppMetadata).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(metadata, value);

    private static Il2CppMetadata Create()
    {
        var bytes = new byte[2048];
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Il2CppMetadata.MetadataMagic);
        I32(4, 29);
        // Empty legacy sections sample one zero row to determine their width.
        for (var offset = 8; offset < 512; offset += 8) I32(offset, 512);
        foreach (var (header, offset, size) in new[] { (104, 0x340, 32), (112, 0x380, 8), (120, 0x300, 32) })
        { I32(header, offset); I32(header + 4, size); }
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            var container = 0x300 + ordinal * 16;
            I32(container, ordinal == 0 ? 17 : 23); I32(container + 4, 1);
            I32(container + 8, ordinal); I32(container + 12, ordinal);
            var parameter = 0x340 + ordinal * 16;
            I32(parameter, ordinal);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(parameter + 8), (short)ordinal);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(parameter + 10), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(parameter + 14), (ushort)(ordinal == 0 ? 4 : 16));
        }
        I32(0x380, 11); I32(0x384, 29);
        var metadata = Il2CppMetadata.ReadFrom(bytes, UnityVersion.Parse("2021.3.35f1"));
        var context = new LibCpp2IlContext(new LibCpp2IlMain.LibCpp2IlSettings()) { Metadata = metadata };
        metadata.SetOwningContext(context);
        return metadata;
    }
}
