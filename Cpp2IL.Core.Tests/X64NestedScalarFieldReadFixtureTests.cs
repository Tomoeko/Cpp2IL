using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for nested scalar field reads.</summary>
[NonParallelizable]
public class X64NestedScalarFieldReadFixtureTests
{
    [TestCase("get_NestedFlag", "Flag")]
    [TestCase("ReadCount", "Count")]
    public void RecoveredReadLoadsTheReceiverBeforeTheScalarField(
        string methodName, string fieldName)
    {
        var assembly = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_BOOLEAN_GETTER_RECOVERED_ASSEMBLY");
        if (string.IsNullOrEmpty(assembly))
            Assert.Ignore("Set CPP2IL_NESTED_BOOLEAN_GETTER_RECOVERED_ASSEMBLY to the recovered synthetic DLL.");
        Assert.That(File.Exists(assembly), Is.True);

        using var stream = File.OpenRead(assembly!);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var owner = reader.GetTypeDefinition(reader.TypeDefinitions.Single(handle =>
            reader.GetString(reader.GetTypeDefinition(handle).Name) == "FlagHolder"));
        var child = reader.GetTypeDefinition(reader.TypeDefinitions.Single(handle =>
            reader.GetString(reader.GetTypeDefinition(handle).Name) == "FlagCell"));
        var method = reader.GetMethodDefinition(owner.GetMethods().Single(handle =>
            reader.GetString(reader.GetMethodDefinition(handle).Name) == methodName));
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        Assert.That(il, Has.Length.EqualTo(12));
        Assert.Multiple(() =>
        {
            Assert.That(il[0], Is.EqualTo((byte)System.Reflection.Emit.OpCodes.Ldarg_0.Value));
            Assert.That(il[1], Is.EqualTo((byte)System.Reflection.Emit.OpCodes.Ldfld.Value));
            Assert.That(il[6], Is.EqualTo((byte)System.Reflection.Emit.OpCodes.Ldfld.Value));
            Assert.That(il[11], Is.EqualTo((byte)System.Reflection.Emit.OpCodes.Ret.Value));
        });
        var receiver = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(
            il.AsSpan(2, 4)));
        var value = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(
            il.AsSpan(7, 4)));
        Assert.That(receiver.Kind, Is.EqualTo(HandleKind.FieldDefinition));
        Assert.That(value.Kind, Is.EqualTo(HandleKind.FieldDefinition));
        var receiverField = (FieldDefinitionHandle)receiver;
        var valueField = (FieldDefinitionHandle)value;
        Assert.Multiple(() =>
        {
            Assert.That(owner.GetFields(), Does.Contain(receiverField));
            Assert.That(child.GetFields(), Does.Contain(valueField));
            Assert.That(reader.GetString(reader.GetFieldDefinition(receiverField).Name),
                Is.EqualTo("Child"));
            Assert.That(reader.GetString(reader.GetFieldDefinition(valueField).Name),
                Is.EqualTo(fieldName));
        });
    }

    [Test]
    public void GetterRequiresItsProvedNullBranchAndUnchangedFieldBindings()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NESTED_BOOLEAN_GETTER_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("NestedBooleanGetterFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "FlagHolder");
            var method = owner.Methods.Single(candidate => candidate.Name == "get_NestedFlag");
            var evidence = X64NestedScalarFieldReadProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.ReceiverField.Name, Is.EqualTo("Child"));
            Assert.That(evidence.ValueField.Name, Is.EqualTo("Flag"));

            var integerMethod = owner.Methods.Single(candidate => candidate.Name == "ReadCount");
            var integerEvidence = X64NestedScalarFieldReadProof.Find(integerMethod);
            Assert.That(integerEvidence, Is.Not.Null);
            Assert.That(integerEvidence!.ReceiverField.Name, Is.EqualTo("Child"));
            Assert.That(integerEvidence.ValueField.Name, Is.EqualTo("Count"));
            try
            {
                integerEvidence.ValueField.OverrideFieldType = app.SystemTypes.SystemBooleanType;
                Assert.That(X64NestedScalarFieldReadProof.Find(integerMethod), Is.Null,
                    "an Int32 native load cannot be retagged as Boolean");
            }
            finally { integerEvidence.ValueField.OverrideFieldType = null; }

            var receiver = evidence.ReceiverField;
            try
            {
                receiver.OverrideOffset = receiver.DefaultOffset + 8;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null,
                    "a different receiver field offset is not proved");
            }
            finally { receiver.OverrideOffset = null; }

            var value = evidence.ValueField;
            try
            {
                value.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null,
                    "a non-Boolean child field is not proved");
            }
            finally { value.OverrideFieldType = null; }
            try
            {
                value.OverrideAttributes = (value.DefaultAttributes &
                    ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private;
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null,
                    "the generated owner cannot read a private child field");
            }
            finally { value.OverrideAttributes = null; }

            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            var constructor = owner.Methods.Single(candidate => candidate.Name == ".ctor");
            aliases.Add(constructor);
            try
            {
                Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null,
                    "a shared native entry cannot identify the getter's fields");
            }
            finally { aliases.Remove(constructor); }
            Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Not.Null);

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native, Has.Length.EqualTo(8));
            Assert.That(native[3].Mnemonic, Is.EqualTo(Mnemonic.Je));
            Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[7].IP));
            var pe = (PE)app.Binary;
            var branchTail = checked((int)pe.MapVirtualAddressToRaw(native[3].NextIP - 1,
                false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("NestedBooleanGetterFixture")!.Types
                .Single(type => type.Name == "FlagHolder").Methods
                .Single(candidate => candidate.Name == "get_NestedFlag");
            Assert.That(X64NestedScalarFieldReadProof.Find(changedMethod), Is.Null,
                "a retargeted native null branch is not proved");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
