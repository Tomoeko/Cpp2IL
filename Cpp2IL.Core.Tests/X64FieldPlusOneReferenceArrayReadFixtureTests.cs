using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64FieldPlusOneReferenceArrayReadFixtureTests
{
    [Test]
    public void GetterNeedsBothFieldsAndCompleteOrderedFailureExits()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FIELD_PLUS_ONE_REFERENCE_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_PLUS_ONE_REFERENCE_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName(
                "FieldPlusOneReferenceArrayFixture")!;
            var readers = new[] { "ReaderA", "ReaderB" }
                .Select(name => assembly.Types.Single(type => type.Name == name))
                .Select(owner => new
                {
                    Owner = owner,
                    Method = owner.Methods.Single(method => method.Name ==
                        "get_Next"),
                    Array = owner.Fields.Single(field => field.Name == "Items"),
                    Index = owner.Fields.Single(field => field.Name == "Position")
                }).ToArray();

            Assert.That(readers[0].Method.UnderlyingPointer,
                Is.Not.EqualTo(readers[1].Method.UnderlyingPointer));
            foreach (var reader in readers)
            {
                Assert.That(app.MethodsByAddress[reader.Method.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(
                    X64FieldPlusOneReferenceArrayReadProof.Find(reader.Method),
                    Is.EqualTo(new X64FieldPlusOneReferenceArrayReadProof
                        .Evidence(reader.Array, reader.Index)));
                try
                {
                    reader.Array.OverrideOffset = reader.Array.DefaultOffset + 8;
                    Assert.That(X64FieldPlusOneReferenceArrayReadProof.Find(
                        reader.Method), Is.Null);
                }
                finally { reader.Array.OverrideOffset = null; }
                try
                {
                    reader.Index.OverrideOffset = reader.Index.DefaultOffset + 8;
                    Assert.That(X64FieldPlusOneReferenceArrayReadProof.Find(
                        reader.Method), Is.Null);
                }
                finally { reader.Index.OverrideOffset = null; }
                var rawElement = reader.Array.BackingData!.Field.RawFieldType!
                    .GetEncapsulatedType()!;
                var elementKind = rawElement.Type;
                try
                {
                    rawElement.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4;
                    Assert.That(X64FieldPlusOneReferenceArrayReadProof.Find(
                        reader.Method), Is.Null);
                }
                finally { rawElement.Type = elementKind; }
            }

            var selected = readers[0];
            var body = X64Stack28BodyProof.Read(selected.Method, 14, 80)!;
            var pe = (PE)app.Binary;
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var instruction in new[]
                     { body[3], body[5], body[7], body[11], body[13] })
            {
                var alteredImage = (byte[])image.Clone();
                var offset = checked((int)pe.MapVirtualAddressToRaw(
                    instruction.NextIP - 1, false));
                alteredImage[offset] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(alteredImage, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("FieldPlusOneReferenceArrayFixture")!
                    .Types.Single(type => type.Name == "ReaderA").Methods
                    .Single(method => method.Name == "get_Next");
                Assert.That(X64FieldPlusOneReferenceArrayReadProof.Find(altered),
                    Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
