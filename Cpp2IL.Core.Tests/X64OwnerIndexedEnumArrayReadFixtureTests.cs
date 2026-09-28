using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64OwnerIndexedEnumArrayReadFixtureTests
{
    [Test]
    public void GetterRequiresBothFieldBindingsAndBothFailureBranches()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_OWNER_INDEXED_ENUM_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_OWNER_INDEXED_ENUM_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("OwnerIndexedEnumArrayFixture")!;
            var methods = new[] { "ReaderA", "ReaderB" }
                .Select(name => assembly.Types.Single(type => type.Name == name))
                .Select(owner => new
                {
                    Owner = owner,
                    Method = owner.Methods.Single(method => method.Name == "get_Current"),
                    Array = owner.Fields.Single(field => field.Name == "Values"),
                    Index = owner.Fields.Single(field => field.Name == "Slot")
                }).ToArray();
            Assert.That(methods[0].Method.UnderlyingPointer,
                Is.Not.EqualTo(methods[1].Method.UnderlyingPointer));
            foreach (var item in methods)
            {
                Assert.That(app.MethodsByAddress[item.Method.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(X64OwnerIndexedEnumArrayReadProof.Find(item.Method),
                    Is.EqualTo(new X64OwnerIndexedEnumArrayReadProof.Evidence(
                        item.Array, item.Index, item.Method.ReturnType)));
                try
                {
                    item.Array.OverrideOffset = item.Array.DefaultOffset + 8;
                    Assert.That(X64OwnerIndexedEnumArrayReadProof.Find(item.Method), Is.Null);
                }
                finally { item.Array.OverrideOffset = null; }
                try
                {
                    item.Index.OverrideOffset = item.Index.DefaultOffset + 8;
                    Assert.That(X64OwnerIndexedEnumArrayReadProof.Find(item.Method), Is.Null);
                }
                finally { item.Index.OverrideOffset = null; }
            }

            var selected = methods[0];
            var body = X64Stack28BodyProof.Read(selected.Method, 13, 64)!;
            var pe = (PE)app.Binary;
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var instruction in new[] { body[3], body[6], body[10], body[12] })
            {
                var changed = (byte[])image.Clone();
                var offset = checked((int)pe.MapVirtualAddressToRaw(
                    instruction.NextIP - 1, false));
                changed[offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("OwnerIndexedEnumArrayFixture")!.Types
                    .Single(type => type.Name == "ReaderA").Methods
                    .Single(method => method.Name == "get_Current");
                Assert.That(X64OwnerIndexedEnumArrayReadProof.Find(altered), Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
