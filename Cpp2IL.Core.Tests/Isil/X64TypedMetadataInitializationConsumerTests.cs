using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64TypedMetadataInitializationConsumerTests
{
    [TestCase("getter", "CPP2IL_STATIC_GETTER_FIXTURE_INPUT", "StaticFieldGetterFixture",
        "StaticState", "ReadFlag")]
    [TestCase("setter", "CPP2IL_STATIC_SCALAR_SETTER_FIXTURE_INPUT", "StaticScalarSetterFixture",
        "StaticState", "Assign")]
    [TestCase("add", "CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT", "MetadataGuardMoveFixture",
        "SharedState", "AddParameter")]
    [TestCase("object add", "CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT", "MetadataGuardMoveFixture",
        "SharedState", "AddBox")]
    [TestCase("cctor", "CPP2IL_STRUCT_STATIC_FORWARD_CALL_FIXTURE_INPUT", "StructStaticForwardCallFixture",
        "FloatPair", ".cctor")]
    [TestCase("base ctor", "CPP2IL_VIRTUAL_STRING_CALL_FIXTURE_INPUT", "VirtualStringCallFixture",
        "DerivedNode", ".ctor")]
    [TestCase("sink", "CPP2IL_GUARDED_SINK_FIXTURE_INPUT", "GuardedSinkFixture",
        "Forwarder", "SendSouth")]
    [TestCase("type", "CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT", "RuntimeCastConcatFixture",
        "MetadataControls", "TargetType")]
    public void ExactConsumerRequiresItsTypeInfoSwitchArm(string consumer, string inputVariable,
        string assemblyName, string typeName, string methodName)
    {
        var directory = Environment.GetEnvironmentVariable(inputVariable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set " + inputVariable + " to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            MethodAnalysisContext Selected() => Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName(assemblyName)!.Types.Single(type => type.Name == typeName)
                .Methods.Single(method => method.Name == methodName);
            Assert.That(FindEvidence(Selected(), consumer), Is.Not.Null);
            var app = Cpp2IlApi.CurrentAppContext!;
            var tableOffset = SwitchTableOffset(app);
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            var method = Selected();
            var bytes = method.RawBytes;
            if (consumer is "getter" or "add" or "object add")
            {
                var body = X86Utils.Iterate(method).ToArray();
                var end = consumer switch
                {
                    "getter" => body[10].NextIP,
                    "add" => body[14].NextIP,
                    _ => body[17].NextIP,
                };
                var consumedLength = checked((int)(end - method.UnderlyingPointer));
                try
                {
                    method.RawBytes = new BinarySlice(bytes.AsSpan().Slice(0, consumedLength).ToArray());
                    Assert.That(FindEvidence(method, consumer), Is.Not.Null,
                        "A complete cached body does not need to include independently proven INT3 padding.");
                    method.RawBytes = new BinarySlice(bytes.AsSpan().Slice(0, consumedLength - 1).ToArray());
                    Assert.That(FindEvidence(method, consumer), Is.Null,
                        "Executable bytes cannot be omitted from the cache as padding.");
                }
                finally { method.RawBytes = bytes; }
            }
            try
            {
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                Assert.That(FindEvidence(method, consumer), Is.Null,
                    "A conflicting cached body cannot be refreshed into accepted evidence.");
                Assert.That(method.RawBytes.AsSpan().ToArray(), Is.EqualTo(changed));
            }
            finally { method.RawBytes = bytes; }
            var definition = method.Definition!;
            var implementation = definition.iflags;
            try
            {
                definition.iflags |= (ushort)MethodImplAttributes.Synchronized;
                Assert.That(FindEvidence(method, consumer), Is.Null,
                    "The proof does not establish synchronized method semantics.");
            }
            finally { definition.iflags = implementation; }
            var protectedRanges = MetadataRanges(method, consumer);
            var originalNative = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            foreach (var (slot, length) in protectedRanges)
            foreach (var displacement in new[] { -7, 0, (int)length - 1, (int)length }.Distinct())
            {
                var target = checked((ulong)((long)slot + displacement));
                var expected = !protectedRanges.Any(range =>
                    target < range.Address + range.Length && range.Address < target + 8);
                var changed = WithIsolatedDir64Relocation(pe, target);
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var alteredApp = Cpp2IlApi.CurrentAppContext!;
                var alteredMethod = Selected();
                alteredMethod.EnsureRawBytes();
                Assert.That(X86Utils.Iterate(alteredMethod), Is.EqualTo(originalNative),
                    "The loader relocation leaves native file bytes unchanged.");
                var alteredUnwind = X64UnwindProof.ForApplication(alteredApp)!;
                Assert.That(alteredUnwind.IsUnaffectedByBaseRelocation(slot, length),
                    Is.EqualTo(displacement == length), "Each consumed metadata range is protected.");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(alteredApp,
                    (PE)alteredApp.Binary, alteredUnwind, alteredApp.GetOrCreateKeyFunctionAddresses()
                        .il2cpp_codegen_initialize_runtime_metadata), Is.True,
                    "The independent helper body remains authenticated.");
                Assert.That(FindEvidence(alteredMethod, consumer) != null, Is.EqualTo(expected),
                    "Only overlap with a consumed slot or once flag invalidates the unchanged body.");
            }
            var typeInfoEntry = image.AsSpan(tableOffset, 4).ToArray();
            var otherEntry = image.AsSpan(tableOffset + 4, 4).ToArray();
            Assert.That(otherEntry, Is.Not.EqualTo(typeInfoEntry));

            // Retarget only the TypeInfo entry. The thunk and helper address
            // remain unchanged, so a name/address match cannot authorize it.
            otherEntry.CopyTo(image, tableOffset);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            Assert.That(FindEvidence(Selected(), consumer), Is.Null,
                "The consumer must reject a helper with a changed TypeInfo arm.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void SinkStillRequiresTheStringLiteralArm()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_GUARDED_SINK_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GUARDED_SINK_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            MethodAnalysisContext Selected() => Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("GuardedSinkFixture")!.Types.Single(type => type.Name == "Forwarder")
                .Methods.Single(method => method.Name == "SendSouth");
            Assert.That(X64GuardedSinkCallProof.Find(Selected()), Is.Not.Null);
            var tableOffset = SwitchTableOffset(Cpp2IlApi.CurrentAppContext!);
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            var typeInfoEntry = image.AsSpan(tableOffset, 4).ToArray();
            Assert.That(image.AsSpan(tableOffset + 16, 4).ToArray(), Is.Not.EqualTo(typeInfoEntry));
            typeInfoEntry.CopyTo(image, tableOffset + 16);

            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, (PE)app.Binary,
                X64UnwindProof.ForApplication(app)!, app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata), Is.True,
                "The unchanged TypeInfo arm alone does not establish literal initialization.");
            Assert.That(X64GuardedSinkCallProof.Find(Selected()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void AlternatePlayerConsumerUsesTheIndependentlyProvedTypeInfoLayout()
    {
        var binary = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_FILE");
        var addressText = Environment.GetEnvironmentVariable("CPP2IL_TYPED_METADATA_CONSUMER_ADDRESS");
        var consumer = Environment.GetEnvironmentVariable("CPP2IL_TYPED_METADATA_CONSUMER_KIND");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata) ||
            string.IsNullOrEmpty(addressText) || string.IsNullOrEmpty(consumer))
            Assert.Ignore("Set the alternate metadata input, consumer address and consumer kind variables.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        var address = Convert.ToUInt64(addressText, 16);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var helper = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, (PE)app.Binary,
                X64UnwindProof.ForApplication(app)!, helper), Is.False,
                "This input must independently exercise the alternate helper.");
            var method = app.MethodsByAddress[address].Single();
            Assert.That(FindEvidence(method, consumer!), Is.Not.Null);
            var tableOffset = SwitchTableOffset(app);
            var image = File.ReadAllBytes(binary!);
            var metadataBytes = File.ReadAllBytes(metadata!);
            image.AsSpan(tableOffset + 4, 4).CopyTo(image.AsSpan(tableOffset, 4));

            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!.MethodsByAddress[address].Single();
            Assert.That(FindEvidence(changed, consumer!), Is.Null,
                "The alternate layout must still authenticate the consumed switch arm.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static object? FindEvidence(MethodAnalysisContext method, string consumer) => consumer switch
    {
        "getter" => X64MetadataStaticGetterProof.Find(method),
        "setter" => X64MetadataStaticInt32SetterProof.Find(method),
        "add" => X64MetadataStaticInt32AddProof.Find(method),
        "object add" => X64MetadataStaticObjectInt32AddProof.Find(method),
        "cctor" => X64StructStaticConstructorProof.Find(method),
        "base ctor" => X64GuardedBaseConstructorProof.Find(method),
        "sink" => X64GuardedSinkCallProof.Find(method),
        "type" => FindTypeEvidence(method),
        _ => throw new ArgumentException("Unknown typed metadata consumer.", nameof(consumer)),
    };

    private static object? FindTypeEvidence(MethodAnalysisContext method)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        return X64TypeFromHandleProof.Find(method, X86Utils.Iterate(method).ToArray());
    }

    private static int SwitchTableOffset(ApplicationAnalysisContext app)
    {
        var pe = (PE)app.Binary;
        var unwind = X64UnwindProof.ForApplication(app)!;
        var helper = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
        Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, pe, unwind, helper),
            Is.True);
        var primary = X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind, helper);
        var thunk = Decode(pe, helper, 5, 0);
        var wrapper = Decode(pe, thunk.NearBranchTarget, 7, 1);
        var tableLoad = primary
            ? Decode(pe, wrapper.NearBranchTarget + 0x5d, 0x23, 1)
            : Decode(pe, wrapper.NearBranchTarget + 0x37, 0x3a, 12);
        return checked((int)pe.MapVirtualAddressToRaw(unwind.ImageBase + tableLoad.MemoryDisplacement64,
            false));
    }

    private static (ulong Address, uint Length)[] MetadataRanges(MethodAnalysisContext method,
        string consumer)
    {
        var body = X86Utils.Iterate(method).ToArray();
        switch (consumer)
        {
            case "type":
            {
                var shape = X64TypeFromHandleProof.TryProveShape(body)!;
                return [(shape.TypeSlot, 8), (shape.TypeInfoSlot, 8), (shape.OnceFlag, 1)];
            }
            case "getter":
                return [(body[3].IPRelativeMemoryAddress, 8), (body[1].IPRelativeMemoryAddress, 1)];
            case "setter":
            {
                var shape = X64MetadataStaticInt32SetterProof.TryProveShape(body.Take(14).ToArray())!;
                return [(shape.TypeInfoSlot, 8), (shape.Flag, 1)];
            }
            case "add":
            {
                var shape = X64MetadataStaticInt32AddProof.TryProveShape(body.Take(15).ToArray())!;
                return [(shape.TypeInfoSlot, 8), (shape.Flag, 1)];
            }
            case "object add":
            {
                var shape = X64MetadataStaticObjectInt32AddProof.TryProveShape(body.Take(18).ToArray())!;
                return [(shape.TypeInfoSlot, 8), (shape.Flag, 1)];
            }
            case "cctor":
            {
                var shape = X64StructStaticConstructorProof.TryProveShape(body)!;
                return [(shape.OwnerSlot, 8), (shape.WitnessSlot, 8), (shape.Flag, 1)];
            }
            case "base ctor":
            {
                var shape = X64GuardedBaseConstructorProof.TryProveShape(body)!.Value;
                var baseConstructor = X64GuardedBaseConstructorProof.Find(method)!.BaseConstructor;
                var folded = X64GuardedBaseConstructorProof.TryProveShape(
                    X86Utils.Iterate(baseConstructor).ToArray())!.Value;
                return [(shape.TypeInfoSlot, 8), (shape.OnceFlag, 1),
                    (folded.TypeInfoSlot, 8), (folded.OnceFlag, 1)];
            }
            case "sink":
            {
                var shape = X64GuardedSinkCallProof.TryProveShape(body.Take(20).ToArray())!.Value;
                return [(shape.TypeInfoSlot, 8), (shape.LiteralSlot, 8), (shape.OnceFlag, 1)];
            }
            default: throw new ArgumentException("Unknown typed metadata consumer.", nameof(consumer));
        }
    }

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

    private static Instruction Decode(PE pe, ulong address, int length, int index)
    {
        var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(pe.GetRawBinaryContent().Slice(offset, length).ToArray()), address);
        for (var current = 0; current < index; current++)
            decoder.Decode();
        return decoder.Decode();
    }
}
