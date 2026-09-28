using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ArrayElementScalarFieldReadFixtureTests
{
    [Test]
    public void Int32AndEnumIndexedSingleReadsRequireClosedNativeAndFields()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ARRAY_ELEMENT_SCALAR_FIELD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_ELEMENT_SCALAR_FIELD_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("ArrayElementScalarFieldFixture")!;
            var reader = assembly.Types.Single(type => type.Name == "Reader");
            var node = assembly.Types.Single(type => type.Name == "Node");
            var array = reader.Fields.Single(field => field.Name == "Nodes");
            var names = new[] { ("ReadLevel", "Level"),
                ("ReadAmount", "Amount"), ("ReadFraction", "Fraction") };
            foreach (var (name, fieldName) in names)
            {
                var method = reader.Methods.Single(candidate => candidate.Name == name);
                var value = node.Fields.Single(field => field.Name == fieldName);
                Assert.That(X64ArrayElementScalarFieldReadProof.Find(method),
                    Is.EqualTo(new X64ArrayElementScalarFieldReadProof.Evidence(array, value)));
                try
                {
                    array.OverrideOffset = array.DefaultOffset + 8;
                    Assert.That(X64ArrayElementScalarFieldReadProof.Find(method), Is.Null);
                }
                finally { array.OverrideOffset = null; }
                try
                {
                    value.OverrideOffset = value.DefaultOffset + 4;
                    Assert.That(X64ArrayElementScalarFieldReadProof.Find(method), Is.Null);
                }
                finally { value.OverrideOffset = null; }
            }

            var pe = (PE)app.Binary;
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var name in new[] { "ReadLevel", "ReadFraction" })
            {
                var method = reader.Methods.Single(candidate => candidate.Name == name);
                var body = X64Stack28BodyProof.Read(method, 16, 80)!;
                foreach (var index in new[] { 3, 5, 7, 9, 10, 13, 15 })
                {
                    var changed = (byte[])image.Clone();
                    var offset = checked((int)pe.MapVirtualAddressToRaw(
                        body[index].NextIP - 1, false));
                    changed[offset] ^= 1;
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                        UnityVersion.Parse("2021.3.35f1"));
                    var altered = Cpp2IlApi.CurrentAppContext!
                        .GetAssemblyByName("ArrayElementScalarFieldFixture")!.Types
                        .Single(type => type.Name == "Reader").Methods
                        .Single(candidate => candidate.Name == name);
                    Assert.That(X64ArrayElementScalarFieldReadProof.Find(altered), Is.Null,
                        $"Native instruction {index} changed for {name}");
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void EachVolatileFieldChangesTheBoundedNativeProof()
    {
        foreach (var (label, methods) in new[]
        {
            ("ARRAY", new[] { "ReadLevel", "ReadAmount", "ReadFraction" }),
            ("INT", new[] { "ReadLevel" }),
            ("SINGLE", new[] { "ReadFraction" })
        })
        {
            var directory = Environment.GetEnvironmentVariable(
                "CPP2IL_ARRAY_ELEMENT_SCALAR_FIELD_VOLATILE_" + label + "_INPUT");
            if (string.IsNullOrEmpty(directory))
                Assert.Ignore("Set all three exact-target volatile-control inputs.");
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
                var reader = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("ArrayElementScalarFieldFixture")!.Types
                    .Single(type => type.Name == "Reader");
                foreach (var name in methods)
                {
                    var method = reader.Methods.Single(candidate => candidate.Name == name);
                    Assert.That(X64ArrayElementScalarFieldReadProof.Find(method), Is.Null,
                        $"Volatile {label} field admitted for {name}");
                }
            }
            finally { Cpp2IlApi.ResetInternalState(); }
        }
    }
}
