using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player evidence for signed and unsigned 32-bit static getters.</summary>
[NonParallelizable]
public class X64MetadataStaticWordGetterFixtureTests
{
    [Test]
    public void OnlyOwnUnchangedWordFieldsAreAccepted()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STATIC_WORD_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STATIC_WORD_GETTER_FIXTURE_INPUT to the synthetic static word getter player-input directory.");
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
            var assembly = app.GetAssemblyByName("StaticWordGetterFixture")!;
            var owner = assembly.Types.Single(type => type.FullName == "StaticWordGetterFixture.StaticWordState");
            foreach (var (methodName, fieldName) in new[]
                     { ("ReadSigned", "Signed"), ("ReadUnsigned", "Unsigned") })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == methodName);
                var evidence = X64MetadataStaticGetterProof.Find(method);
                Assert.That(evidence, Is.Not.Null, methodName);
                Assert.That(evidence!.Field.Name, Is.EqualTo(fieldName));
                Assert.That(evidence.Field.DeclaringType, Is.SameAs(owner));

                var field = evidence.Field;
                try
                {
                    field.OverrideOffset = field.DefaultOffset + 4;
                    Assert.That(X64MetadataStaticGetterProof.Find(method), Is.Null,
                        "a changed field layout is not evidence");
                }
                finally { field.OverrideOffset = null; }

                try
                {
                    field.OverrideFieldType = methodName == "ReadSigned"
                        ? app.SystemTypes.SystemUInt32Type : app.SystemTypes.SystemInt32Type;
                    Assert.That(X64MetadataStaticGetterProof.Find(method), Is.Null,
                        "a changed signedness is not evidence");
                }
                finally { field.OverrideFieldType = null; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
