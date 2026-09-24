using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player evidence for TypeInfo-guarded static field getters.</summary>
[NonParallelizable]
public class X64MetadataStaticGetterFixtureTests
{
    [Test]
    public void OnlyOwnUnchangedFieldsWithoutClassConstructorAreAccepted()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STATIC_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STATIC_GETTER_FIXTURE_INPUT to the synthetic static getter player-input directory.");
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
            var assembly = app.GetAssemblyByName("StaticFieldGetterFixture")!;
            var owner = assembly.Types.Single(type => type.FullName == "StaticFieldGetterFixture.StaticState");
            foreach (var (methodName, fieldName) in new[]
                     { ("ReadPointer", "Pointer"), ("ReadReference", "Reference"),
                         ("ReadFlag", "Flag") })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == methodName);
                var evidence = X64MetadataStaticGetterProof.Find(method);
                Assert.That(evidence, Is.Not.Null, methodName);
                Assert.That(evidence!.Field.Name, Is.EqualTo(fieldName));
                Assert.That(evidence.Field.DeclaringType, Is.SameAs(owner));
                Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(evidence.TypeInfoSlot)?.Type,
                    Is.EqualTo(MetadataUsageType.TypeInfo));

                var field = evidence.Field;
                try
                {
                    field.OverrideOffset = field.DefaultOffset + (methodName == "ReadFlag" ? 1 : 8);
                    Assert.That(X64MetadataStaticGetterProof.Find(method), Is.Null,
                        "a changed field layout is not evidence");
                }
                finally { field.OverrideOffset = null; }
            }

            var booleanGetter = owner.Methods.Single(method => method.Name == "ReadFlag");
            var booleanField = owner.Fields.Single(field => field.Name == "Flag");
            var neighbor = owner.Fields.Single(field => field.Name == "NeighborFlag");
            Assert.That(neighbor.Offset, Is.EqualTo(booleanField.Offset + 1));
            booleanGetter.EnsureRawBytes();
            var native = X86Utils.Iterate(booleanGetter).Take(11).ToArray();
            Assert.That(native, Has.Length.EqualTo(11));
            Assert.That(native[8].Code, Is.EqualTo(Code.Movzx_r32_rm8));
            Assert.That(X64MetadataStaticGetterProof.FieldLoadPair(native[7], native[8],
                out var fieldOffset, out var loadSize), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(fieldOffset, Is.EqualTo((ulong)booleanField.Offset));
                Assert.That(loadSize, Is.EqualTo(1));
            });
            foreach (var defect in new[] { "signed extension", "word extension", "destination",
                         "base", "index" })
            {
                var changed = native[8];
                switch (defect)
                {
                    case "signed extension": changed.Code = Code.Movsx_r32_rm8; break;
                    case "word extension": changed.Code = Code.Movzx_r32_rm16; break;
                    case "destination": changed.Op0Register = Register.ECX; break;
                    case "base": changed.MemoryBase = Register.RDX; break;
                    case "index": changed.MemoryIndex = Register.RAX; break;
                }
                Assert.That(X64MetadataStaticGetterProof.FieldLoadPair(native[7], changed,
                    out _, out _), Is.False, defect);
            }

            try
            {
                booleanField.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64MetadataStaticGetterProof.Find(booleanGetter), Is.Null,
                    "the managed field type must remain Boolean");
            }
            finally { booleanField.OverrideFieldType = null; }
            try
            {
                booleanField.OverrideAttributes = booleanField.DefaultAttributes |
                    FieldAttributes.HasFieldMarshal;
                Assert.That(X64MetadataStaticGetterProof.Find(booleanGetter), Is.Null,
                    "a changed marshaling declaration is not evidence");
            }
            finally { booleanField.OverrideAttributes = null; }
            try
            {
                booleanGetter.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64MetadataStaticGetterProof.Find(booleanGetter), Is.Null,
                    "the managed return type must remain Boolean");
            }
            finally { booleanGetter.OverrideReturnType = null; }

            var binding = app.MethodsByAddress[booleanGetter.UnderlyingPointer];
            var unrelatedGetter = owner.Methods.Single(method => method.Name == "ReadReference");
            binding.Add(unrelatedGetter);
            try
            {
                Assert.That(X64MetadataStaticGetterProof.Find(booleanGetter), Is.Null,
                    "a shared native address does not prove this method's field");
            }
            finally { binding.Remove(unrelatedGetter); }

            var source = owner.Methods.Single(method => method.Name == "ReadPointer");
            var oldBitfield = owner.Definition!.Bitfield;
            try
            {
                owner.Definition.Bitfield |= 1u << 3;
                Assert.That(X64MetadataStaticGetterProof.Find(source), Is.Null,
                    "a class constructor can have observable initialization effects");
            }
            finally { owner.Definition.Bitfield = oldBitfield; }

            var holder = assembly.Types.Single(type =>
                type.FullName == "StaticFieldGetterFixture.ReferenceHolder");
            Assert.That(X64MetadataStaticGetterProof.Find(holder.Methods.Single()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void SignedByteExtensionIsNotAProvedBooleanGetter()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STATIC_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STATIC_GETTER_FIXTURE_INPUT to the synthetic static getter player-input directory.");
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
            var method = app.GetAssemblyByName("StaticFieldGetterFixture")!.Types
                .Single(type => type.Name == "StaticState").Methods
                .Single(candidate => candidate.Name == "ReadFlag");
            method.EnsureRawBytes();
            var load = X86Utils.Iterate(method).ElementAt(8);
            Assert.That(load.Code, Is.EqualTo(Code.Movzx_r32_rm8));
            var pe = (PE)app.Binary;
            var offset = checked((int)pe.MapVirtualAddressToRaw(load.IP, false));
            var alteredBinary = File.ReadAllBytes(binary);
            Assert.That(alteredBinary[offset], Is.EqualTo(0x0F));
            Assert.That(alteredBinary[offset + 1], Is.EqualTo(0xB6));
            alteredBinary[offset + 1] = 0xBE; // movsx eax, byte [rcx+offset]
            var metadataBytes = File.ReadAllBytes(metadata);

            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(alteredBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedApp = Cpp2IlApi.CurrentAppContext!;
            var changed = changedApp.GetAssemblyByName("StaticFieldGetterFixture")!.Types
                .Single(type => type.Name == "StaticState").Methods
                .Single(candidate => candidate.Name == "ReadFlag");
            Assert.That(X64MetadataStaticGetterProof.Find(changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
