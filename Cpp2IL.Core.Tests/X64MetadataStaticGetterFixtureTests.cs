using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using LibCpp2IL;

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
                     { ("ReadPointer", "Pointer"), ("ReadReference", "Reference") })
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
                    field.OverrideOffset = field.DefaultOffset + 8;
                    Assert.That(X64MetadataStaticGetterProof.Find(method), Is.Null,
                        "a changed field layout is not evidence");
                }
                finally { field.OverrideOffset = null; }
            }

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
}
