using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NestedArrayCallFixtureTests
{
    [Test]
    public void DirectAndNestedAliasesRequireTheirOwnLayoutAndWholeBody()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_ARRAY_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NESTED_ARRAY_CALL_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("NestedArrayCallFixture")!;
            var owners = new[] { "NestedA", "NestedB", "NestedC",
                "DirectA", "DirectB" }.Select(name =>
            {
                var owner = assembly.Types.Single(type => type.Name == name);
                return new
                {
                    Name = name,
                    Method = owner.Methods.Single(method => method.Name == "TouchAt"),
                    Array = owner.Fields.Single(field => field.Name == "Nodes")
                };
            }).ToArray();
            var node = assembly.Types.Single(type => type.Name == "Node");
            var link = node.Fields.Single(field => field.Name == "Link");
            var leaf = assembly.Types.Single(type => type.Name == "Leaf");
            var leafTarget = leaf.Methods.Single(method => method.Name == "Touch");
            var nodeTarget = node.Methods.Single(method => method.Name == "Touch");

            Assert.That(owners[0].Method.UnderlyingPointer,
                Is.EqualTo(owners[1].Method.UnderlyingPointer));
            Assert.That(owners[3].Method.UnderlyingPointer,
                Is.EqualTo(owners[4].Method.UnderlyingPointer));
            foreach (var item in owners)
            {
                Assert.That(X64NestedArrayCallProof.Find(item.Method),
                    Is.EqualTo(new X64NestedArrayCallProof.Evidence(item.Array,
                        item.Name.StartsWith("Nested", StringComparison.Ordinal)
                            ? link : null,
                        item.Name.StartsWith("Nested", StringComparison.Ordinal)
                            ? leafTarget : nodeTarget)));
            }

            // A folded address is admitted only while every metadata binding
            // independently explains the same load offset and target.
            try
            {
                owners[1].Array.OverrideOffset = owners[1].Array.DefaultOffset + 8;
                Assert.That(X64NestedArrayCallProof.Find(owners[0].Method), Is.Null);
                Assert.That(X64NestedArrayCallProof.Find(owners[1].Method), Is.Null);
            }
            finally { owners[1].Array.OverrideOffset = null; }
            try
            {
                owners[4].Array.OverrideOffset = owners[4].Array.DefaultOffset + 8;
                Assert.That(X64NestedArrayCallProof.Find(owners[3].Method), Is.Null);
                Assert.That(X64NestedArrayCallProof.Find(owners[4].Method), Is.Null);
            }
            finally { owners[4].Array.OverrideOffset = null; }
            try
            {
                link.OverrideOffset = link.DefaultOffset + 8;
                Assert.That(X64NestedArrayCallProof.Find(owners[2].Method), Is.Null);
            }
            finally { link.OverrideOffset = null; }
            try
            {
                leafTarget.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64NestedArrayCallProof.Find(owners[2].Method), Is.Null);
            }
            finally { leafTarget.OverrideReturnType = null; }

            var pe = (PE)app.Binary;
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var (ownerName, indices) in new[]
            {
                ("NestedC", new[] { 3, 5, 9, 12, 15, 16, 18 }),
                ("DirectA", new[] { 3, 5, 9, 12, 13, 15 })
            })
            {
                var selected = owners.Single(item => item.Name == ownerName);
                var body = X64Stack28BodyProof.Read(selected.Method,
                    ownerName == "NestedC" ? 19 : 16, 80)!;
                foreach (var index in indices)
                {
                    var changed = (byte[])image.Clone();
                    var offset = checked((int)pe.MapVirtualAddressToRaw(
                        body[index].NextIP - 1, false));
                    changed[offset] ^= 1;
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                        UnityVersion.Parse("2021.3.35f1"));
                    var altered = Cpp2IlApi.CurrentAppContext!
                        .GetAssemblyByName("NestedArrayCallFixture")!.Types
                        .Single(type => type.Name == ownerName).Methods
                        .Single(method => method.Name == "TouchAt");
                    Assert.That(X64NestedArrayCallProof.Find(altered), Is.Null,
                        $"Native instruction {index} changed for {ownerName}");
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void VolatileReferenceLoadsDoNotMatchBoundedShape()
    {
        foreach (var label in new[] { "OWNER", "NESTED" })
        {
            var directory = Environment.GetEnvironmentVariable(
                "CPP2IL_NESTED_ARRAY_CALL_VOLATILE_" + label + "_INPUT");
            if (string.IsNullOrEmpty(directory))
                Assert.Ignore("Set both volatile-control inputs to run exact-target negative checks.");
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
                var assembly = app.GetAssemblyByName("NestedArrayCallFixture")!;
                var names = label == "OWNER"
                    ? new[] { "NestedA", "NestedB", "NestedC", "DirectA", "DirectB" }
                    : new[] { "NestedA", "NestedB", "NestedC" };
                foreach (var name in names)
                {
                    var method = assembly.Types.Single(type => type.Name == name)
                        .Methods.Single(candidate => candidate.Name == "TouchAt");
                    Assert.That(X64NestedArrayCallProof.Find(method), Is.Null,
                        $"Volatile {label} load admitted for {name}");
                }
            }
            finally { Cpp2IlApi.ResetInternalState(); }
        }
    }
}
