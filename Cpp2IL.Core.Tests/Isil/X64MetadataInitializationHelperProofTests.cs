using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64MetadataInitializationHelperProofTests
{
    [Test]
    [NonParallelizable]
    public void AlternateExactPlayerAuthenticatesStringLiteralCacheAndAllocation()
    {
        var binary = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_FILE");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Set both alternate metadata input paths to a local exact-target player.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var initializer = app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe,
                unwind, initializer), Is.False, "the existing core layout is distinct");
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app,
                pe, unwind, initializer), Is.True, "alternate core identity");
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app,
                pe, unwind, initializer), Is.True, "alternate TypeInfo arm");
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app,
                pe, unwind, initializer), Is.True, "alternate string-literal arm");

            var thunk = Decode(pe, initializer, 5, 0);
            var wrapper = Decode(pe, thunk.NearBranchTarget, 7, 1);
            var core = wrapper.NearBranchTarget;
            var tableLoad = Decode(pe, core + 0x37, 0x3a, 12);
            var tableEntryAddress = unwind.ImageBase + tableLoad.MemoryDisplacement64 + 16;
            var tableEntry = checked((int)pe.MapVirtualAddressToRaw(
                tableEntryAddress, false));
            var image = File.ReadAllBytes(binary!);
            var armRva = BinaryPrimitives.ReadInt32LittleEndian(
                image.AsSpan(tableEntry, 4));
            Assert.That(armRva, Is.GreaterThan(0));
            var arm = unwind.ImageBase + (uint)armRva;
            var exchange = Decode(pe, arm, 0x7f, 19);
            Assert.That(exchange.Mnemonic, Is.EqualTo(Mnemonic.Cmpxchg));
            Assert.That(exchange.HasLockPrefix, Is.True);
            var exchangePrefix = checked((int)pe.MapVirtualAddressToRaw(exchange.IP, false));
            Assert.That(image[exchangePrefix], Is.EqualTo(0xf0));

            var mutations = new[]
            {
                (Name: "literal switch entry", Offset: tableEntry),
                (Name: "cache compare-exchange", Offset: exchangePrefix),
                (Name: "string allocation export", Offset: checked((int)
                    pe.MapVirtualAddressToRaw(pe.GetVirtualAddressOfExportedFunctionByName(
                        "il2cpp_string_new_len"), false))),
                (Name: "write barrier export", Offset: checked((int)
                    pe.MapVirtualAddressToRaw(pe.GetVirtualAddressOfExportedFunctionByName(
                        "il2cpp_gc_wbarrier_set_field"), false))),
            };
            var metadataBytes = File.ReadAllBytes(metadata!);
            foreach (var mutation in mutations)
            {
                var changed = (byte[])image.Clone();
                changed[mutation.Offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var modified = Cpp2IlApi.CurrentAppContext!;
                var changedPe = (PE)modified.Binary;
                var changedUnwind = X64UnwindProof.ForApplication(modified)!;
                var changedInitializer = modified.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata;
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(
                    modified, changedPe, changedUnwind, changedInitializer), Is.True,
                    $"alternate core after {mutation.Name} mutation");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(
                    modified, changedPe, changedUnwind, changedInitializer), Is.True,
                    $"TypeInfo arm after {mutation.Name} mutation");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(
                    modified, changedPe, changedUnwind, changedInitializer), Is.False,
                    mutation.Name);
            }

            var wrongTypeArm = (byte[])image.Clone();
            wrongTypeArm[tableEntry - 16] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(wrongTypeArm, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var wrongTypeApp = Cpp2IlApi.CurrentAppContext!;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(
                wrongTypeApp, (PE)wrongTypeApp.Binary,
                X64UnwindProof.ForApplication(wrongTypeApp)!, wrongTypeApp
                    .GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata), Is.False,
                "a retargeted TypeInfo switch entry cannot authenticate the cast route");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerAuthenticatesStringLiteralUsageArm()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT to the neutral synthetic player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("LiteralConcatFixture")!.Types
                .Single(type => type.Name == "Resolver").Methods
                .Single(candidate => candidate.Name == "Compose");
            method.EnsureRawBytes();
            var calls = X86Utils.Iterate(method).Where(instruction =>
                instruction.Code == Code.Call_rel32_64).ToArray();
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var initializer = app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata;
            Assert.That(calls.Count(call => call.NearBranchTarget == initializer), Is.EqualTo(1));
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe,
                unwind, initializer), Is.True);
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe,
                unwind, calls.First(call => call.NearBranchTarget != initializer).NearBranchTarget),
                Is.False);

            var thunk = Decode(pe, initializer, 5, 0);
            var wrapper = Decode(pe, thunk.NearBranchTarget, 7, 1);
            var dispatch = Decode(pe, wrapper.NearBranchTarget + 0x5d, 13, 1);
            var tableEntry = (int)pe.MapVirtualAddressToRaw(
                unwind.ImageBase + dispatch.MemoryDisplacement64 + 4 * 4, false);
            var stringExport = (int)pe.MapVirtualAddressToRaw(
                pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_string_new_len"), false);
            var barrierExport = (int)pe.MapVirtualAddressToRaw(
                pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_gc_wbarrier_set_field"), false);
            var original = File.ReadAllBytes(binary);
            var mutations = new[]
            {
                (Name: "StringLiteral switch entry", Offset: tableEntry),
                (Name: "string constructor export", Offset: stringExport),
                (Name: "write barrier export", Offset: barrierExport),
            };
            var scratch = Path.Combine(Path.GetDirectoryName(directory!)!,
                "metadata-arm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var mutatedBinary = Path.Combine(scratch, "GameAssembly.dll");
                foreach (var mutation in mutations)
                {
                    var copy = (byte[])original.Clone();
                    copy[mutation.Offset] ^= 1;
                    File.WriteAllBytes(mutatedBinary, copy);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(mutatedBinary, metadata,
                        UnityVersion.Parse("2021.3.35f1"));
                    var mutatedApp = Cpp2IlApi.CurrentAppContext!;
                    var mutatedPe = (PE)mutatedApp.Binary;
                    var mutatedUnwind = X64UnwindProof.ForApplication(mutatedApp)!;
                    var mutatedInitializer = mutatedApp.GetOrCreateKeyFunctionAddresses()
                        .il2cpp_codegen_initialize_runtime_metadata;
                    Assert.That(X64MetadataInitializationHelperProof.TryIdentify(mutatedApp,
                        mutatedPe, mutatedUnwind, mutatedInitializer), Is.True,
                        $"TypeInfo identity after {mutation.Name} mutation");
                    Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(
                        mutatedApp, mutatedPe, mutatedUnwind, mutatedInitializer), Is.False,
                        mutation.Name);
                }
            }
            finally { Directory.Delete(scratch, recursive: true); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction Decode(PE pe, ulong address, int length, int index)
    {
        var offset = (int)pe.MapVirtualAddressToRaw(address, false);
        var bytes = pe.GetRawBinaryContent().Slice(offset, length).ToArray();
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        for (var current = 0; current < index; current++)
            decoder.Decode();
        return decoder.Decode();
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerAuthenticatesTypeInfoInitializerBeyondKeyFunctionLookup()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_FACTORY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_FACTORY_FIXTURE_INPUT to the neutral synthetic player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("IteratorFactoryManualFixture")!.Types
                .Single(type => type.Name == "ManualOwner").Methods
                .Single(candidate => candidate.Name == "Create");
            method.EnsureRawBytes();
            var calls = X86Utils.Iterate(method).Where(instruction =>
                instruction.Code == Code.Call_rel32_64).ToArray();
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var initializer = app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata;
            Assert.That(calls.Count(call => call.NearBranchTarget == initializer), Is.EqualTo(1));
            Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                initializer), Is.True);
            Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                calls.First(call => call.NearBranchTarget != initializer).NearBranchTarget), Is.False);

            var thunk = Decode(pe, initializer, 5, 0);
            var wrapper = Decode(pe, thunk.NearBranchTarget, 7, 1);
            var resolverCall = Decode(pe, wrapper.NearBranchTarget + 0x5d, 0x23, 5);
            var slowCall = Decode(pe, resolverCall.NearBranchTarget, 0x7c, 24);
            var throwingJump = Decode(pe, slowCall.NearBranchTarget, 0x31, 7);
            var nonthrowingCall = Decode(pe, slowCall.NearBranchTarget, 0x31, 8);
            var throwingCall = Decode(pe, throwingJump.NearBranchTarget, 0x30, 3);
            Assert.That(throwingCall.NearBranchTarget,
                Is.EqualTo(nonthrowingCall.NearBranchTarget));
            var wrongTarget = nonthrowingCall.NearBranchTarget + 1;
            var copy = File.ReadAllBytes(binary);
            foreach (var call in new[] { nonthrowingCall, throwingCall })
            {
                var offset = (int)pe.MapVirtualAddressToRaw(call.IP, false);
                var displacement = checked((int)((long)wrongTarget - (long)call.NextIP));
                BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset + 1, 4), displacement);
            }
            var scratch = Path.Combine(Path.GetDirectoryName(directory!)!,
                "class-init-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var mutatedBinary = Path.Combine(scratch, "GameAssembly.dll");
                File.WriteAllBytes(mutatedBinary, copy);
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(mutatedBinary, metadata,
                    UnityVersion.Parse("2021.3.35f1"));
                var mutatedApp = Cpp2IlApi.CurrentAppContext!;
                var mutatedPe = (PE)mutatedApp.Binary;
                var mutatedUnwind = X64UnwindProof.ForApplication(mutatedApp)!;
                var mutatedInitializer = mutatedApp.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata;
                Assert.That(X64MetadataInitializationHelperProof.TryIdentify(mutatedApp,
                    mutatedPe, mutatedUnwind, mutatedInitializer), Is.False,
                    "retargeted Class::Init calls");
            }
            finally { Directory.Delete(scratch, recursive: true); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
