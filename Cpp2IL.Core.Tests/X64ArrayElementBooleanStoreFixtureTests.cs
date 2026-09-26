using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ArrayElementBooleanStoreFixtureTests
{
    [Test]
    public void ArrayElementStoresRequireOrderedGuardsAndMatchingLayouts()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_ELEMENT_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_ELEMENT_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("ArrayElementStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "CellCatalog");
            var array = owner.Fields.Single(field => field.Name == "Items");
            var value = assembly.Types.Single(type => type.Name == "Cell").Fields
                .Single(field => field.Name == "Enabled");
            foreach (var (name, literal) in new[] { ("Enable", true), ("Disable", false) })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                var evidence = X64ArrayElementBooleanStoreProof.Find(method);
                Assert.Multiple(() =>
                {
                    Assert.That(evidence?.ArrayField, Is.SameAs(array));
                    Assert.That(evidence?.ValueField, Is.SameAs(value));
                    Assert.That(evidence?.Value, Is.EqualTo(literal));
                });
            }
            var enable = owner.Methods.Single(method => method.Name == "Enable");
            try
            {
                array.OverrideOffset = array.DefaultOffset + 8;
                Assert.That(X64ArrayElementBooleanStoreProof.Find(enable), Is.Null);
            }
            finally { array.OverrideOffset = null; }
            try
            {
                value.OverrideFieldType = app.SystemTypes.SystemByteType;
                Assert.That(X64ArrayElementBooleanStoreProof.Find(enable), Is.Null);
            }
            finally { value.OverrideFieldType = null; }
            try
            {
                value.OverrideAttributes = value.DefaultAttributes | FieldAttributes.InitOnly;
                Assert.That(X64ArrayElementBooleanStoreProof.Find(enable), Is.Null);
                value.OverrideAttributes = (value.DefaultAttributes & ~FieldAttributes.FieldAccessMask) |
                    FieldAttributes.Private;
                Assert.That(X64ArrayElementBooleanStoreProof.Find(enable), Is.Null);
            }
            finally { value.OverrideAttributes = null; }
            try
            {
                enable.Parameters[0].OverrideParameterType = app.SystemTypes.SystemUInt32Type;
                Assert.That(X64ArrayElementBooleanStoreProof.Find(enable), Is.Null);
            }
            finally { enable.Parameters[0].OverrideParameterType = null; }

            var body = X64Stack28BodyProof.Read(enable, 16, 96)!;
            Assert.That(body, Is.Not.Null);
            var addresses = new List<ulong>
            {
                body[3].NextIP - 1, // Array null must reach the null helper.
                body[5].IP, // Only the proved unsigned condition is admitted.
                body[7].NextIP - 1, // Element address must use the array item header.
                body[9].NextIP - 1, // Element null must fail before the store.
                body[10].NextIP - 2, // Store must resolve to the declared Boolean field.
                body[10].NextIP - 1, // Only canonical Boolean literals are admitted.
                body[15].NextIP - 1, // Bounds helper identity is required.
            };
            var region = X64UnwindProof.ForApplication(app)!
                .ClassifySpan(enable.UnderlyingPointer, enable.UnderlyingPointer + 1);
            if (body[^1].NextIP < region.End)
                addresses.Add(body[^1].NextIP);
            var pe = (PE)app.Binary;
            var offsets = addresses.Select(address => checked((int)
                pe.MapVirtualAddressToRaw(address, false))).ToArray();
            var originalBinary = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var offset in offsets)
            {
                var changed = (byte[])originalBinary.Clone();
                changed[offset] ^= 2;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("ArrayElementStoreFixture")!.Types
                    .Single(type => type.Name == "CellCatalog").Methods
                    .Single(method => method.Name == "Enable");
                Assert.That(X64ArrayElementBooleanStoreProof.Find(altered), Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
