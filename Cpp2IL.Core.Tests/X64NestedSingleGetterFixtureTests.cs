using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NestedSingleGetterFixtureTests
{
    [Test]
    public void NestedPublicSingleReadRequiresItsExactNullBranchAndFields()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_SINGLE_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NESTED_SINGLE_GETTER_FIXTURE_INPUT to the neutral exact player input.");
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
            var reader = app.GetAssemblyByName("NestedSingleGetterFixture")!.Types
                .Single(type => type.Name == "FloatReader");
            var method = reader.Methods.Single(candidate => candidate.Name == "ReadLevel");
            var receiver = reader.Fields.Single(field => field.Name == "Child");
            var child = receiver.FieldType;
            var value = child.Fields.Single(field => field.Name == "Level");
            var evidence = X64NestedScalarFieldReadProof.Find(method);
            Assert.Multiple(() =>
            {
                Assert.That(evidence?.ReceiverField, Is.SameAs(receiver));
                Assert.That(evidence?.ValueField, Is.SameAs(value));
                Assert.That(child.Visibility, Is.EqualTo(TypeAttributes.NestedPublic));
                Assert.That(child.DeclaringType?.Visibility, Is.EqualTo(TypeAttributes.Public));
            });

            try
            {
                value.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null);
            }
            finally { value.OverrideFieldType = null; }

            try
            {
                value.OverrideAttributes = (value.DefaultAttributes &
                    ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null);
            }
            finally { value.OverrideAttributes = null; }

            try
            {
                child.OverrideAttributes = (child.DefaultAttributes &
                    ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPrivate;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null);
            }
            finally { child.OverrideAttributes = null; }

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native, Has.Length.EqualTo(8));
            Assert.That(native[4].Code, Is.EqualTo(Code.Movss_xmm_xmmm32));
            Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[7].IP));
            var branchTail = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("NestedSingleGetterFixture")!.Types
                .Single(type => type.Name == "FloatReader").Methods
                .Single(candidate => candidate.Name == "ReadLevel");
            Assert.That(X64NestedScalarFieldReadProof.Find(changedMethod), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
