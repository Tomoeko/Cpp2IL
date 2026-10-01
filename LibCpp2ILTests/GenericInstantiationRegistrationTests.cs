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
        AssertInstantiationUnavailable(binary);
        SetField(binary, "_genericInsts", original);
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
        SetField(binary, "_genericInsts", new[] { original[0], original[1], original[1] });
        AssertInstantiationUnavailable(binary);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMethodTablesCaptureHeadersAndEachOriginalOrdinal(bool is32Bit)
    {
        var (binary, _) = Create(is32Bit, includeMethodTables: true);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var table));
        Assert.Equal(0x100ul, table.CodeRegistrationAddress);
        Assert.Equal(0x200ul, table.MetadataRegistrationAddress);
        Assert.Equal(2, table.SpecificationCount);
        Assert.Equal(0x700ul, table.SpecificationsAddress);
        Assert.Equal(2, table.FunctionCount);
        Assert.Equal(0x730ul, table.FunctionsAddress);
        Assert.Equal(2ul, table.MethodPointerCount);
        Assert.Equal(0x780ul, table.MethodPointersAddress);
        Assert.Equal(2ul, table.InvokerCount);
        Assert.Equal(0x7A0ul, table.InvokersAddress);
        Assert.Equal(0x7C0ul, table.AdjustorThunksAddress);
        for (var ordinal = 0; ordinal < 2; ordinal++)
        {
            Assert.True(binary.TryGetGenericMethodSpecificationRegistration(ordinal, out var specification));
            Assert.Equal(new Il2CppBinary.GenericMethodSpecificationRegistration(ordinal, ordinal, -1, ordinal), specification);
            Assert.True(binary.TryGetGenericMethodFunctionRegistration(ordinal, out var function));
            Assert.Equal(new Il2CppBinary.GenericMethodFunctionRegistration(ordinal, ordinal, ordinal, ordinal, -1), function);
        }
        Assert.False(binary.TryGetGenericMethodSpecificationRegistration(-1, out _));
        Assert.False(binary.TryGetGenericMethodSpecificationRegistration(2, out _));
        Assert.False(binary.TryGetGenericMethodFunctionRegistration(int.MaxValue, out _));
    }

    [Theory]
    [InlineData(false, "specifications")]
    [InlineData(true, "specifications")]
    [InlineData(false, "functions")]
    [InlineData(true, "functions")]
    [InlineData(false, "pointers")]
    [InlineData(true, "pointers")]
    public void CoherentRawCountAndCacheTruncationCannotHideAnOriginalTail(bool is32Bit, string table)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var original));
        var specifications = context.Metadata.methodSpecs;
        var functions = context.Metadata.genericMethodTables;
        var pointers = Field<ulong[]>(binary, "_genericMethodPointers");
        var selectedReference = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        Assert.True(binary.TryGetGenericMethodRegistration(selectedReference, out var selectedOrigin));
        if (table == "specifications")
        {
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 8, 1);
            context.Metadata.methodSpecs = specifications[..1];
        }
        else if (table == "functions")
        {
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 4, 1);
            context.Metadata.genericMethodTables = functions[..1];
        }
        else
        {
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 1);
            SetField(binary, "_genericMethodPointers", pointers[..1]);
        }
        // The selected per-reference origin still exists. It cannot establish
        // completeness of a table whose original tail was removed.
        Assert.True(binary.TryGetGenericMethodRegistration(selectedReference, out var retainedOrigin));
        Assert.Equal(selectedOrigin, retainedOrigin);
        Assert.False(binary.TryGetGenericMethodTableRegistration(out _));
        Assert.False(binary.TryGetGenericMethodSpecificationRegistration(0, out _));
        Assert.False(binary.TryGetGenericMethodFunctionRegistration(0, out _));
        context.Metadata.methodSpecs = specifications;
        context.Metadata.genericMethodTables = functions;
        SetField(binary, "_genericMethodPointers", pointers);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 8, 2);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 4, 2);
        binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 2);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var restored));
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData(false, "specification")]
    [InlineData(true, "specification")]
    [InlineData(false, "function")]
    [InlineData(true, "function")]
    public void EquivalentNativeRowReplacementCannotBorrowTheOriginalOrdinal(bool is32Bit, string row)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        if (row == "specification")
        {
            var original = context.Metadata.methodSpecs[1];
            context.Metadata.methodSpecs[1] = new Il2CppMethodSpec
            {
                methodDefinitionIndex = original.methodDefinitionIndex,
                classIndexIndex = original.classIndexIndex, methodIndexIndex = original.methodIndexIndex
            };
            Assert.True(binary.TryGetGenericMethodTableRegistration(out _));
            Assert.False(binary.TryGetGenericMethodSpecificationRegistration(1, out _));
            Assert.True(binary.TryGetGenericMethodSpecificationRegistration(0, out _));
            context.Metadata.methodSpecs[1] = original;
            Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out _));
        }
        else
        {
            var original = context.Metadata.genericMethodTables[1];
            context.Metadata.genericMethodTables[1] = new Il2CppGenericMethodFunctionsDefinitions
            {
                GenericMethodIndex = original.GenericMethodIndex, methodIndex = original.methodIndex,
                invokerIndex = original.invokerIndex, adjustorThunk = original.adjustorThunk
            };
            Assert.True(binary.TryGetGenericMethodTableRegistration(out _));
            Assert.False(binary.TryGetGenericMethodFunctionRegistration(1, out _));
            Assert.True(binary.TryGetGenericMethodFunctionRegistration(0, out _));
            context.Metadata.genericMethodTables[1] = original;
            Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutableMethodHeadersAndRowValuesDoNotRewriteTheirOriginalSnapshots(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var table));
        Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out var specification));
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out var function));
        context.Metadata.methodSpecs[1].methodDefinitionIndex = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(77);
        context.Metadata.genericMethodTables[1].invokerIndex = 88;
        binary.Word(0x700 + 12, 77);
        binary.Word(0x730 + 16 + 8, 88);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 8, 1);
        var code = Field<Il2CppCodeRegistration>(binary, "_codeRegistration");
        code.genericMethodPointersCount = 1;
        code.invokerPointersCount = 1;
        code.invokerPointers = 99;
        code.genericAdjustorThunks = 111;
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var currentTable));
        Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out var currentSpecification));
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out var currentFunction));
        Assert.Equal(table, currentTable);
        Assert.Equal(specification, currentSpecification);
        Assert.Equal(function, currentFunction);
        // Provenance remains available; its consumer must reject changed
        // current raw/cache facts before selecting runtime aliases.
    }

    [Theory]
    [InlineData(false, "specifications")]
    [InlineData(true, "specifications")]
    [InlineData(false, "functions")]
    [InlineData(true, "functions")]
    [InlineData(false, "mapping")]
    [InlineData(true, "mapping")]
    public void FailedNativeTableReadOrMappingCannotPublishNewOrPriorRows(bool is32Bit, string failure)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        if (failure == "specifications") binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 9, 0xFFF);
        if (failure == "functions") binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 5, 0xFFF);
        if (failure == "mapping") binary.Word(0x700, 99);
        if (failure == "mapping")
            Assert.Throws<IndexOutOfRangeException>(() => binary.Init(0x100, 0x200, context.Metadata));
        else
            Assert.Throws<EndOfStreamException>(() => binary.Init(0x100, 0x200, context.Metadata));
        AssertUnavailable(binary);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 9, 0x700);
        binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 5, 0x730);
        binary.Word(0x700, 0);
        binary.Init(0x100, 0x200, context.Metadata);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var restored));
        Assert.Equal(2, restored.SpecificationCount);
        Assert.Equal(2, restored.FunctionCount);
        Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out _));
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonemptyMethodTablesAreInvalidatedBeforeAContextSearchFailure(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        binary.FailSearch = true;
        Assert.Throws<InvalidOperationException>(() => binary.Init(context));
        AssertUnavailable(binary);
        binary.FailSearch = false;
        binary.Init(context);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var restored));
        Assert.Equal(2, restored.FunctionCount);
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out _));
    }

    [Theory]
    [InlineData(false, "code")]
    [InlineData(true, "code")]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    [InlineData(false, "both")]
    [InlineData(true, "both")]
    public void DelegatedStructuresWithoutBothNativeRootAddressesDoNotClaimTableProvenance(bool is32Bit, string missing)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        void Locate(Il2CppBinary current, Il2CppMetadata metadata,
            ref Il2CppCodeRegistration code, ref Il2CppMetadataRegistration registration)
        {
            if (!ReferenceEquals(current, binary) || !ReferenceEquals(metadata, context.Metadata)) return;
            code ??= binary.ReadReadableAtVirtualAddress<Il2CppCodeRegistration>(0x100);
            registration ??= binary.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(0x200);
        }
        Il2CppBinary.OnRegistrationStructLocationFailure += Locate;
        try
        {
            binary.Init(missing == "metadata" ? 0x100ul : 0,
                missing == "code" ? 0x200ul : 0, context.Metadata);
            Assert.False(binary.TryGetGenericMethodTableRegistration(out _));
            Assert.False(binary.TryGetGenericMethodSpecificationRegistration(0, out _));
            Assert.False(binary.TryGetGenericMethodFunctionRegistration(0, out _));
        }
        finally
        {
            Il2CppBinary.OnRegistrationStructLocationFailure -= Locate;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingNativeReadsKeepAllMethodTableSnapshotsUnavailable(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        binary.ObservePendingRead = true;
        binary.Init(context);
        Assert.True(binary.PendingReadsObserved > 0);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out _));
        Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out _));
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReinitializationReplacesGenericMethodOriginsAndCollections(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        var first = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        var second = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[1]]);
        Assert.True(binary.TryGetGenericMethodRegistration(first, out _));
        binary.Init(context);
        Assert.False(binary.TryGetGenericMethodRegistration(first, out _));
        Assert.False(binary.TryGetGenericMethodPointerVirtualAddress(second, out _));
        var currentFirst = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        var currentSecond = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[1]]);
        Assert.NotSame(first, currentFirst);
        Assert.NotSame(second, currentSecond);
        Assert.True(binary.TryGetGenericMethodRegistration(currentFirst, out _));
        Assert.True(binary.TryGetGenericMethodRegistration(currentSecond, out _));
        Assert.Same(currentFirst, Assert.Single(binary.ConcreteGenericImplementationsByAddress[0x900]));
        Assert.Same(currentSecond, Assert.Single(binary.ConcreteGenericImplementationsByAddress[0x908]));
    }

    [Theory]
    [InlineData(false, "search")]
    [InlineData(true, "search")]
    [InlineData(false, "first-mapping")]
    [InlineData(true, "first-mapping")]
    [InlineData(false, "second-mapping")]
    [InlineData(true, "second-mapping")]
    public void FailedInitializationLeavesNoPriorOrPartialGenericMethodOrigins(bool is32Bit, string failure)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        var prior = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        if (failure == "search")
        {
            binary.FailSearch = true;
            Assert.Throws<InvalidOperationException>(() => binary.Init(context));
        }
        else
        {
            binary.Word(failure == "first-mapping" ? 0x700ul : 0x70Cul, 99);
            Assert.Throws<IndexOutOfRangeException>(() => binary.Init(context));
        }
        AssertUnavailable(binary);
        Assert.False(binary.TryGetGenericMethodRegistration(prior, out _));
        Assert.False(binary.TryGetGenericMethodPointerVirtualAddress(prior, out _));
        Assert.Empty(binary.ConcreteGenericMethods);
        Assert.Empty(binary.ConcreteGenericImplementationsByAddress);
        Assert.Empty(Field<System.Collections.IDictionary>(binary, "_genericMethodRegistrations"));
        Assert.Empty(Field<System.Collections.IDictionary>(binary, "_genericMethodDictionary"));
        binary.FailSearch = false;
        binary.Word(0x700, 0);
        binary.Word(0x70C, 1);
        binary.Init(context);
        var current = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        Assert.NotSame(prior, current);
        Assert.True(binary.TryGetGenericMethodRegistration(current, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyGenericPointerGenerationCannotReuseEarlierOriginsOrPointers(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        var prior = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        Assert.NotEmpty(Field<System.Collections.IDictionary>(binary, "_genericMethodDictionary"));
        binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 0);
        binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 3, 0);
        binary.Init(context);
        Assert.True(binary.TryGetGenericMethodTableRegistration(out var table));
        Assert.Equal(0ul, table.MethodPointerCount);
        Assert.False(binary.TryGetGenericMethodRegistration(prior, out _));
        Assert.Empty(binary.ConcreteGenericMethods);
        Assert.Empty(binary.ConcreteGenericImplementationsByAddress);
        Assert.Empty(Field<System.Collections.IDictionary>(binary, "_genericMethodDictionary"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentGenerationOriginsRemainUnavailableUntilMappingCompletionReturns(bool is32Bit)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        var prior = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
        var observed = false;
        var previousWriter = LibCpp2IL.Logging.LibLogger.Writer;
        LibCpp2IL.Logging.LibLogger.Writer = new ObservingWriter(previousWriter, () =>
        {
            if (!binary.ConcreteGenericMethods.TryGetValue(context.Metadata.methodDefs[0], out var references)) return;
            foreach (var current in references)
            {
                if (ReferenceEquals(current, prior)) continue;
                observed = true;
                Assert.False(binary.TryGetGenericMethodRegistration(current, out _));
                Assert.False(binary.TryGetGenericMethodPointerVirtualAddress(current, out _));
            }
        });
        try
        {
            binary.Init(context);
            Assert.True(observed);
            var current = Assert.Single(binary.ConcreteGenericMethods[context.Metadata.methodDefs[0]]);
            Assert.True(binary.TryGetGenericMethodRegistration(current, out _));
        }
        finally
        {
            LibCpp2IL.Logging.LibLogger.Writer = previousWriter;
        }
    }

    [Theory]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    [InlineData(false, "binary")]
    [InlineData(true, "binary")]
    [InlineData(false, "context")]
    [InlineData(true, "context")]
    public void EquivalentInputSubstitutionCannotBorrowPublishedGenericOrigins(bool is32Bit, string part)
    {
        var (binary, context) = Create(is32Bit, includeMethodTables: true);
        var metadata = context.Metadata;
        var reference = Assert.Single(binary.ConcreteGenericMethods[metadata.methodDefs[0]]);
        Assert.True(binary.HasOriginalGenericRegistrationContext(context));
        Assert.False(binary.HasOriginalGenericRegistrationContext(null));
        try
        {
            if (part == "metadata")
                context.Metadata = (Il2CppMetadata)typeof(object).GetMethod("MemberwiseClone",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(metadata, null)!;
            else if (part == "binary")
                context.Binary = new FlatBinary(binary.GetRawBinaryContent().ToArray(), is32Bit);
            else
                SetField(binary, "_context", new LibCpp2IlContext(new LibCpp2IlMain.LibCpp2IlSettings())
                    { Metadata = metadata, Binary = binary });
            Assert.False(binary.HasOriginalGenericRegistrationContext(context));
            AssertUnavailable(binary);
            Assert.False(binary.TryGetGenericMethodRegistration(reference, out _));
            Assert.False(binary.TryGetGenericMethodPointerVirtualAddress(reference, out _));
        }
        finally
        {
            context.Metadata = metadata;
            context.Binary = binary;
            SetField(binary, "_context", context);
        }
        Assert.True(binary.HasOriginalGenericRegistrationContext(context));
        Assert.True(binary.TryGetGenericInstantiationRegistration(1, out _));
        Assert.True(binary.TryGetGenericMethodSpecificationRegistration(1, out _));
        Assert.True(binary.TryGetGenericMethodFunctionRegistration(1, out _));
        Assert.True(binary.TryGetGenericMethodRegistration(reference, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewContextMustActuallyInitializeBeforeAcquiringGenericOrigins(bool is32Bit)
    {
        var (binary, original) = Create(is32Bit, includeMethodTables: true);
        var prior = Assert.Single(binary.ConcreteGenericMethods[original.Metadata.methodDefs[0]]);
        var next = new LibCpp2IlContext(new LibCpp2IlMain.LibCpp2IlSettings())
            { Metadata = original.Metadata, Binary = binary };
        Assert.False(binary.HasOriginalGenericRegistrationContext(next));
        Assert.True(binary.HasOriginalGenericRegistrationContext(original));
        binary.Init(next);
        Assert.False(binary.HasOriginalGenericRegistrationContext(original));
        Assert.True(binary.HasOriginalGenericRegistrationContext(next));
        Assert.False(binary.TryGetGenericMethodRegistration(prior, out _));
        var current = Assert.Single(binary.ConcreteGenericMethods[next.Metadata.methodDefs[0]]);
        Assert.True(binary.TryGetGenericMethodRegistration(current, out _));
        binary.FailSearch = true;
        Assert.Throws<InvalidOperationException>(() => binary.Init(original));
        Assert.False(binary.HasOriginalGenericRegistrationContext(original));
        Assert.False(binary.HasOriginalGenericRegistrationContext(next));
        AssertUnavailable(binary);
        binary.FailSearch = false;
        binary.Init(original);
        Assert.True(binary.HasOriginalGenericRegistrationContext(original));
        Assert.False(binary.HasOriginalGenericRegistrationContext(next));
        Assert.False(binary.TryGetGenericMethodRegistration(current, out _));
    }

    private sealed class ObservingWriter(LibCpp2IL.Logging.LogWriter previous, Action observe) : LibCpp2IL.Logging.LogWriter
    {
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        public override void Info(string message) => previous.Info(message);
        public override void Warn(string message) => previous.Warn(message);
        public override void Error(string message) => previous.Error(message);
        public override void Verbose(string message)
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) observe();
            previous.Verbose(message);
        }
    }

    private static void AssertInstantiationUnavailable(Il2CppBinary binary)
    {
        Assert.False(binary.TryGetGenericInstantiationTableRegistration(out _));
        Assert.False(binary.TryGetGenericInstantiationRegistration(0, out _));
    }

    private static void AssertUnavailable(Il2CppBinary binary)
    {
        AssertInstantiationUnavailable(binary);
        Assert.False(binary.TryGetGenericMethodTableRegistration(out _));
        Assert.False(binary.TryGetGenericMethodSpecificationRegistration(0, out _));
        Assert.False(binary.TryGetGenericMethodFunctionRegistration(0, out _));
    }

    private static T Field<T>(Il2CppBinary binary, string name) =>
        (T)typeof(Il2CppBinary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binary)!;
    private static void SetField(Il2CppBinary binary, string name, object value) =>
        typeof(Il2CppBinary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(binary, value);

    private static (FlatBinary Binary, LibCpp2IlContext Context) Create(bool is32Bit, bool includeMethodTables = false)
    {
        var metadata = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(metadata, Il2CppMetadata.MetadataMagic);
        BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(4), 29);
        // Empty pre-v38 sections still sample one row to determine its width.
        // Point them to a zeroed payload rather than the metadata magic/header.
        for (var at = 8; at < 512; at += 8)
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(at), 512);
        if (includeMethodTables)
        {
            // Two real v29 method rows are parsed for the native generic mapper.
            // They have empty names and no declaring type or signature graph.
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(8 + 5 * 8), 0x240);
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(8 + 5 * 8 + 4), 2 * 32);
            for (var ordinal = 0; ordinal < 2; ordinal++)
                for (var field = 1; field <= 4; field++)
                    BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(0x240 + ordinal * 32 + field * 4), -1);
        }
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
        if (includeMethodTables)
        {
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 4, 2);
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 5, 0x730);
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 8, 2);
            binary.Native(0x200 + (ulong)binary.PointerSizeBytes * 9, 0x700);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 2, 2);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 3, 0x780);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 4, 0x7C0);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 5, 2);
            binary.Native(0x100 + (ulong)binary.PointerSizeBytes * 6, 0x7A0);
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                var specification = 0x700ul + (ulong)ordinal * 12;
                binary.Word(specification, ordinal);
                binary.Word(specification + 4, -1);
                binary.Word(specification + 8, ordinal);
                var function = 0x730ul + (ulong)ordinal * 16;
                binary.Word(function, ordinal);
                binary.Word(function + 4, ordinal);
                binary.Word(function + 8, ordinal);
                binary.Word(function + 12, -1);
                binary.Native(0x780ul + (ulong)ordinal * (ulong)binary.PointerSizeBytes, 0x900ul + (ulong)ordinal * 8);
                binary.Native(0x7A0ul + (ulong)ordinal * (ulong)binary.PointerSizeBytes, 0x910ul + (ulong)ordinal * 8);
            }
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
        public void Word(ulong address, int value) =>
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(checked((int)address)), value);
        public void Native(ulong address, ulong value)
        {
            is32Bit = native32Bit;
            if (native32Bit) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(checked((int)address)), checked((uint)value));
            else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(checked((int)address)), value);
        }
    }
}
