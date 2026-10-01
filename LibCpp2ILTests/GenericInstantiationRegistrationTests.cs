using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using AssetRipper.Primitives;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using Xunit;

namespace LibCpp2ILTests;

public class GenericInstantiationRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalParseCapturesEachOrdinalAndNativeWidth(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit);
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out var table));
        Assert.Equal(2, table.Count);
        Assert.Equal(0x300ul, table.Address);
        Assert.Equal(0x200ul, table.MetadataRegistrationAddress);
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            Assert.True(binary.TryGetGenericInstantiationRegistration(ordinal, out var row));
            Assert.Equal(ordinal, row.Index);
            Assert.Equal(0x400ul + (ulong)ordinal * 0x20, row.Address);
            Assert.Equal((ulong)ordinal + 1, row.ArgumentCount);
            Assert.Equal(0x600ul + (ulong)ordinal * 0x20, row.ArgumentsAddress);
        }
        Assert.False(binary.TryGetGenericInstantiationRegistration(-1, out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(2, out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(int.MaxValue, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutableCachesAndRawRowsDoNotRewriteOriginalValues(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit);
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out var table));
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out var row));
        var instances = Field<Il2CppGenericInst[]>(binary, "_genericInsts");
        instances[1].pointerCount = 77;
        instances[1].pointerStart = 88;
        var metadataRegistration = Field<Il2CppMetadataRegistration>(binary, "_metadataRegistration");
        metadataRegistration.genericInstsCount = 1;
        metadataRegistration.genericInsts = 99;
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 2, 1);
        binary.Native(row.Address, 77);
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out var currentTable));
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out var currentRow));
        Assert.Equal(table, currentTable);
        Assert.Equal(row, currentRow);
        // These APIs expose provenance. A recovery consumer must separately
        // compare the current raw/cache facts to these original values.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentReplacementInstanceCannotBorrowOriginalOrdinal(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit);
        var instances = Field<Il2CppGenericInst[]>(binary, "_genericInsts");
        var original = instances[1];
        instances[1] = new Il2CppGenericInst { pointerCount = original.pointerCount, pointerStart = original.pointerStart };
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out _));
        Assert.True(binary.TryGetGenericInstantiationRegistration(0, out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(1, out _));
        instances[1] = original;
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizedArrayCannotHideAnOriginalRow(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit);
        var original = Field<Il2CppGenericInst[]>(binary, "_genericInsts");
        SetField(binary, "_genericInsts", new[] { original[0] });
        AssertUnavailable(binary);
        SetField(binary, "_genericInsts", original);
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
        SetField(binary, "_genericInsts", new[] { original[0], original[1], original[1] });
        AssertUnavailable(binary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameOrderedInstancesMayChangeArrayContainerButNotOrdinalIdentity(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit);
        var original = Field<Il2CppGenericInst[]>(binary, "_genericInsts");
        SetField(binary, "_genericInsts", (Il2CppGenericInst[])original.Clone());
        Assert.True(binary.TryGetGenericInstantiationRegistration(0, out _));
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
        SetField(binary, "_genericInsts", new[] { original[1], original[0] });
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(0, out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(1, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingInitializationDoesNotPublishPartialRows(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit);
        binary.ObservePendingRead = true;
        binary.Init(context);
        Assert.True(binary.PendingReadsObserved > 0);
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedContextSearchInvalidatesPriorSuccessfulParse(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit);
        binary.FailSearch = true;
        Assert.Throws<InvalidOperationException>(() => binary.Init(context));
        AssertUnavailable(binary);
        binary.FailSearch = false;
        binary.Init(context);
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
    }

    [Theory]
    [InlineData(false, "registration")]
    [InlineData(true, "registration")]
    [InlineData(false, "instance")]
    [InlineData(true, "instance")]
    [InlineData(false, "later-pointer")]
    [InlineData(true, "later-pointer")]
    public void AnyFailedDirectInitLeavesNoPublishedProvenance(bool is32Bit, string failure)
    {
        var (binary, context) = Create(is32Bit);
        var metadataAddress = 0x200ul;
        if (failure == "registration") metadataAddress = 0xFFFul;
        if (failure == "instance") binary.Native(0x300, 0xFFF);
        if (failure == "later-pointer")
        {
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 1);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 3, 0xFFF);
        }
        Assert.Throws<EndOfStreamException>(() => binary.Init(0x100, metadataAddress, context.Metadata));
        AssertUnavailable(binary);
        binary.Native(0x300, 0x400);
        binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 0);
        binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 3, 0);
        binary.Init(0x100, 0x200, context.Metadata);
        Assert.True(binary.TryGetGenericInstantiationTableRegistration(out var restored));
        Assert.Equal(2, restored.Count);
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
    }

    private static void AssertUnavailable(Il2CppBinary binary)
    {
        Assert.False(binary.TryGetGenericInstantiationTableRegistration(out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(0, out _));
    }

    private static T Field<T>(Il2CppBinary binary, string name) =>
        (T)typeof(Il2CppBinary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binary)!;
    private static void SetField(Il2CppBinary binary, string name, object value) =>
        typeof(Il2CppBinary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(binary, value);

    private static (FlatBinary Binary, LibCpp2IlContext Context) Create(bool is32Bit)
    {
        var metadata = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata, Il2CppMetadata.MetadataMagic);
        BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(4), 29);
        // Empty pre-v38 sections still sample one row to determine its width.
        // Point them to a zeroed payload rather than the metadata magic/header.
        for (var at = 8; at < 512; at += 8)
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(at), 512);
        var context = new LibCpp2IlContext(new LibCpp2IlMain.LibCpp2IlSettings());
        context.Metadata = Il2CppMetadata.ReadFrom(metadata, UnityVersion.Parse("2021.3.35f1"));
        context.Metadata.SetOwningContext(context);
        var binary = new FlatBinary(new byte[4096], is32Bit) { is32Bit = is32Bit };
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 2, 2);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 3, 0x300);
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            var row = 0x400ul + (ulong)ordinal * 0x20;
            binary.Native(0x300 + (ulong)ordinal * (ulong)binary.PointerSizeBytes, row);
            binary.Native(row, (ulong)ordinal + 1);
            binary.Native(row + (ulong)binary.PointerSizeBytes, 0x600ul + (ulong)ordinal * 0x20);
        }
        binary.Init(context);
        return (binary, context);
    }

    private sealed class FlatBinary(byte[] bytes, bool native32Bit) : Il2CppBinary(new MemoryStream(bytes))
    {
        public bool FailSearch { get; set; }
        public bool ObservePendingRead { get; set; }
        public int PendingReadsObserved { get; private set; }
        public override ulong ReadNUint()
        {
            if (ObservePendingRead)
            {
                AssertUnavailable(this);
                PendingReadsObserved++;
            }
            return base.ReadNUint();
        }
        public override long RawLength => bytes.Length;
        public override long MapVirtualAddressToRaw(ulong address, bool throwOnError = true) => checked((long)address);
        public override ulong MapRawAddressToVirtual(uint offset, bool throwOnError = true) => offset;
        public override ulong GetRva(ulong pointer) => pointer;
        public override byte GetByteAtRawAddress(ulong address) => bytes[checked((int)address)];
        public override ReadOnlySpan<byte> GetRawBinaryContent() => bytes;
        public override ReadOnlySpan<byte> GetEntirePrimaryExecutableSection() => [];
        public override ulong GetVirtualAddressOfPrimaryExecutableSection() => 0;
        public override ulong GetVirtualAddressOfExportedFunctionByName(string name) => 0;
        public override (ulong, ulong) FindCodeAndMetadataReg(Il2CppMetadata metadata)
        {
            if (FailSearch) throw new InvalidOperationException("Synthetic registration search failure");
            is32Bit = native32Bit;
            return (0x100, 0x200);
        }
        public void Native(ulong address, ulong value)
        {
            is32Bit = native32Bit;
            if (native32Bit) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(checked((int)address)), checked((uint)value));
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(checked((int)address)), value);
        }
    }
}
