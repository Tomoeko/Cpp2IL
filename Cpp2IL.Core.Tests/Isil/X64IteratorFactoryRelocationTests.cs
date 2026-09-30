using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64IteratorFactoryRelocationTests
{
    [TestCase(-7, false)]
    [TestCase(0, false)]
    [TestCase(7, false)]
    [TestCase(8, true)]
    public void GeneratedFactoryAuthenticatesEveryByteOfItsTypeInfoTokenSlot(
        int displacement, bool expected)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT to the neutral generated iterator player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        var metadataBytes = File.ReadAllBytes(metadata);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var factory = SelectedFactory(app);
            var native = X86Utils.Iterate(factory).ToArray();
            Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Not.Null);
            var slot = native[9].IPRelativeMemoryAddress;
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(factory.UnderlyingPointer, factory.UnderlyingPointer + 1);
            var rootBody = X64NativeInstructionReader.ReadRootBody(factory);
            Assert.That(rootBody, Is.Not.Null);
            Assert.That(unwind.IsUnaffectedByBaseRelocation(slot, 8), Is.True);
            var slotBytes = pe.GetRawBinaryContent()
                .Slice(checked((int)pe.MapVirtualAddressToRaw(slot, false)), 8).ToArray();
            var changedImage = WithIsolatedDir64Relocation(pe, checked((ulong)((long)slot + displacement)));

            // Reinitialization gives the changed loader evidence its own binary,
            // metadata context, native-byte cache and independently parsed unwind index.
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedImage, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var alteredApp = Cpp2IlApi.CurrentAppContext!;
            var alteredFactory = SelectedFactory(alteredApp);
            var currentNative = X86Utils.Iterate(alteredFactory).ToArray();
            var alteredPe = (PE)alteredApp.Binary;
            var alteredUnwind = X64UnwindProof.ForApplication(alteredApp)!;
            var currentRegion = alteredUnwind.ClassifySpan(alteredFactory.UnderlyingPointer,
                alteredFactory.UnderlyingPointer + 1);
            Assert.Multiple(() =>
            {
                Assert.That(currentNative, Is.EqualTo(native), "The relocation entry leaves native file bytes unchanged.");
                Assert.That(currentRegion.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(currentRegion.Start, Is.EqualTo(region.Start));
                Assert.That(currentRegion.End, Is.EqualTo(region.End));
                Assert.That(currentRegion.RootStart, Is.EqualTo(region.RootStart));
                Assert.That(X64NativeInstructionReader.ReadRootBody(alteredFactory), Is.EqualTo(rootBody),
                    "The complete caller body still has an authenticated file-backed unwind boundary.");
                Assert.That(alteredPe.GetRawBinaryContent().Slice(
                    checked((int)alteredPe.MapVirtualAddressToRaw(slot, false)), 8).ToArray(), Is.EqualTo(slotBytes));
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(alteredApp, alteredPe,
                    alteredUnwind, currentNative[7].NearBranchTarget), Is.True,
                    "The independent TypeInfo initializer remains authenticated.");
                Assert.That(alteredUnwind.IsUnaffectedByBaseRelocation(slot, 8), Is.EqualTo(expected));
                Assert.That(X64IteratorFactoryProof.Find(alteredFactory, currentNative) != null, Is.EqualTo(expected),
                    "A DIR64 write that touches any token byte invalidates the factory; the adjacent write does not.");
            });
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static MethodAnalysisContext SelectedFactory(ApplicationAnalysisContext app)
    {
        var factory = app.GetAssemblyByName("IteratorFactoryFixture")!.Types
            .Single(type => type.Name == "IteratorOwner").Methods.Single(method => method.Name == "Iterate");
        factory.EnsureRawBytes();
        return factory;
    }

    // Replace the relocation directory with one valid DIR64 entry and one ABSOLUTE
    // alignment entry. The PE's sections, token, native body and unwind records stay intact.
    private static byte[] WithIsolatedDir64Relocation(PE pe, ulong target)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optional, 2)), Is.EqualTo(0x20B));
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var directory = optional + 112 + 5 * 8;
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory, 4));
        var relocationSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory + 4, 4));
        Assert.That(relocationRva, Is.Not.Zero);
        Assert.That(relocationSize, Is.GreaterThanOrEqualTo(12));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva, false));
        Assert.That(raw, Is.InRange(0, image.Length - 12));
        var rva = checked((uint)(target - imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directory + 4, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw, 4), rva & ~0xFFFU);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw + 4, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 8, 2), (ushort)(0xA000U | (rva & 0xFFFU)));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 10, 2), 0);
        return image;
    }
}
