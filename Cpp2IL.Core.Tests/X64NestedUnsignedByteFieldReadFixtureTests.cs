using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Exact-player control for a nested unsigned byte read returning Int32.</summary>
[NonParallelizable]
public class X64NestedUnsignedByteFieldReadFixtureTests
{
    [Test]
    public void ByteWideningBindsOriginalFieldsAndTheCompleteNullExit()
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_NESTED_BYTE_FIELD_READ_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_NESTED_BYTE_FIELD_READ_INPUT to the neutral exact player input.");
        var binary = Path.Combine(input!, "GameAssembly.dll");
        var metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("NestedByteFieldReadFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "ByteOwner");
            var child = assembly.Types.Single(type => type.Name == "ByteCell");
            var method = owner.Methods.Single(candidate => candidate.Name == "ReadUnsigned");
            var source = owner.Fields.Single(field => field.Name == "Child");
            var value = child.Fields.Single(field => field.Name == "Value");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var evidence = X64NestedScalarFieldReadProof.Find(method);
            Assert.Multiple(() =>
            {
                Assert.That(method.RawBytes.Length, Is.EqualTo(27));
                Assert.That(native, Has.Length.EqualTo(8));
                Assert.That(native[4].Code, Is.EqualTo(Code.Movzx_r32_rm8));
                Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[7].IP));
                Assert.That(method.Definition!.RawReturnType!.Type,
                    Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_I4));
                Assert.That(value.BackingData!.Field.RawFieldType!.Type,
                    Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_U1));
                Assert.That(evidence?.ReceiverField, Is.SameAs(source));
                Assert.That(evidence?.ValueField, Is.SameAs(value));
            });

            void Reject(string reason, Action change, Action restore)
            {
                change();
                try
                {
                    Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null, reason);
                }
                finally { restore(); }
            }

            var sourceOffset = source.Offset;
            Reject("the parent field must still match the native first load",
                () => source.Offset += 8, () => source.Offset = sourceOffset);
            Reject("the parent field must retain its reference type",
                () => source.OverrideFieldType = app.SystemTypes.SystemObjectType,
                () => source.OverrideFieldType = null);
            Reject("the byte field must retain its canonical unsigned type",
                () => value.OverrideFieldType = app.SystemTypes.SystemSByteType,
                () => value.OverrideFieldType = null);
            Reject("the generated owner must be able to read its child field",
                () => value.OverrideAttributes = (value.DefaultAttributes &
                    ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private,
                () => value.OverrideAttributes = null);

            var rawField = value.BackingData!.Field.RawFieldType!;
            var originalFieldKind = rawField.Type;
            Reject("signed source bytes cannot stand in for zero extension",
                () => rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_I1,
                () => rawField.Type = originalFieldKind);
            var rawReturn = method.Definition!.RawReturnType!;
            var originalReturnKind = rawReturn.Type;
            Reject("the Int32 return descriptor must remain original",
                () => rawReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_U4,
                () => rawReturn.Type = originalReturnKind);
            Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Not.Null);

            var pe = (PE)app.Binary;
            var branchTail = checked((int)pe.MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            var movzxOpcode = checked((int)pe.MapVirtualAddressToRaw(
                native[4].IP + 1, false));
            var changedBinary = File.ReadAllBytes(binary);
            changedBinary[branchTail] ^= 1;
            AssertChangedImageRejects(changedBinary, File.ReadAllBytes(metadata),
                "a retargeted child-null branch must be rejected");

            changedBinary = File.ReadAllBytes(binary);
            Assert.That(changedBinary[movzxOpcode], Is.EqualTo(0xB6));
            changedBinary[movzxOpcode] = 0xBE;
            AssertChangedImageRejects(changedBinary, File.ReadAllBytes(metadata),
                "a signed byte load must not impersonate unsigned widening");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void AssertChangedImageRejects(byte[] binary, byte[] metadata,
        string reason)
    {
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
            UnityVersion.Parse("2021.3.35f1"));
        var method = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("NestedByteFieldReadFixture")!.Types
            .Single(type => type.Name == "ByteOwner").Methods
            .Single(candidate => candidate.Name == "ReadUnsigned");
        Assert.That(X64NestedScalarFieldReadProof.Find(method), Is.Null, reason);
    }
}
