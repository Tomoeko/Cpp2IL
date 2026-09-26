using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64FixedReferenceArrayReadFixtureTests
{
    [Test]
    public void FixedIndicesRequireMatchingBoundsLoadsAndCompleteRegions()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FIXED_REFERENCE_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIXED_REFERENCE_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("FixedReferenceArrayFixture")!.Types
                .Single(type => type.Name == "CellCatalog");
            var array = owner.Fields.Single(field => field.Name == "Items");
            foreach (var (name, index) in new[]
            {
                ("get_Third", 2), ("get_Fourth", 3), ("get_Fifth", 4)
            })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                var evidence = X64FixedReferenceArrayReadProof.Find(method);
                Assert.Multiple(() =>
                {
                    Assert.That(evidence?.ArrayField, Is.SameAs(array));
                    Assert.That(evidence?.Index, Is.EqualTo(index));
                });
            }
            var third = owner.Methods.Single(method => method.Name == "get_Third");
            try
            {
                array.OverrideOffset = array.DefaultOffset + 8;
                Assert.That(X64FixedReferenceArrayReadProof.Find(third), Is.Null);
            }
            finally { array.OverrideOffset = null; }
            try
            {
                array.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64FixedReferenceArrayReadProof.Find(third), Is.Null);
            }
            finally { array.OverrideFieldType = null; }

            third.EnsureRawBytes();
            var body = X86Utils.Iterate(third).Take(12).ToArray();
            var bodyEnd = body[^1].NextIP;
            Assert.That(third.RawBytes.Length,
                Is.GreaterThan(bodyEnd - third.UnderlyingPointer),
                "this control includes a later unrelated native function");
            var pe = (PE)app.Binary;
            var addresses = new List<ulong>
            {
                body[4].NextIP - 1, // Bounds index must agree with the load.
                body[5].NextIP - 1, // Bounds failure must reach the proved helper.
                body[6].NextIP - 1, // Element address must select the same index.
            };
            var region = X64UnwindProof.ForApplication(app)!
                .ClassifySpan(third.UnderlyingPointer, third.UnderlyingPointer + 1);
            if (bodyEnd < region.End)
                addresses.Add(bodyEnd);
            var offsets = addresses.Select(address => checked((int)
                pe.MapVirtualAddressToRaw(address, false))).ToArray();
            var originalBinary = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var offset in offsets)
            {
                var changed = (byte[])originalBinary.Clone();
                changed[offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("FixedReferenceArrayFixture")!.Types
                    .Single(type => type.Name == "CellCatalog").Methods
                    .Single(method => method.Name == "get_Third");
                Assert.That(X64FixedReferenceArrayReadProof.Find(altered), Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
