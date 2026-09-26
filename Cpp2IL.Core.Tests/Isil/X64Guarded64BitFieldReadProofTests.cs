using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64Guarded64BitFieldReadProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsNativeSizedFieldsAndRejectsChangedEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_INT_FIELD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_INT_FIELD_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var box = app.GetAssemblyByName("NativeIntFieldFixture")!.Types
                .Single(type => type.Name == "PointerBox");
            var signed = box.Methods.Single(method => method.Name == "ReadSigned");
            var unsigned = box.Methods.Single(method => method.Name == "ReadUnsigned");
            var signedField = box.Fields.Single(field => field.Name == "Signed");
            var unsignedField = box.Fields.Single(field => field.Name == "Unsigned");
            Assert.Multiple(() =>
            {
                Assert.That(X64Guarded64BitFieldReadProof.Find(signed,
                    X86Utils.Iterate(signed).ToArray())?.Field, Is.SameAs(signedField));
                Assert.That(X64Guarded64BitFieldReadProof.Find(unsigned,
                    X86Utils.Iterate(unsigned).ToArray())?.Field, Is.SameAs(unsignedField));
            });

            signedField.OverrideFieldType = app.SystemTypes.SystemInt64Type;
            try
            {
                Assert.That(X64Guarded64BitFieldReadProof.Find(signed,
                    X86Utils.Iterate(signed).ToArray()), Is.Null);
            }
            finally { signedField.OverrideFieldType = null; }

            var changed = File.ReadAllBytes(binary);
            var offset = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                signed.UnderlyingPointer, false));
            changed[offset + 7] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changed, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var altered = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("NativeIntFieldFixture")!.Types
                .Single(type => type.Name == "PointerBox").Methods
                .Single(method => method.Name == "ReadSigned");
            Assert.That(X64Guarded64BitFieldReadProof.Find(altered,
                X86Utils.Iterate(altered).ToArray()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactDirectAndNestedReferenceReadsRetainTheirFieldOffsets()
    {
        var direct = X64Guarded64BitFieldReadProof.TryProveShape(Body(false));
        var nested = X64Guarded64BitFieldReadProof.TryProveShape(Body(true));
        Assert.Multiple(() =>
        {
            Assert.That(direct, Is.Not.Null);
            Assert.That(direct!.ReceiverOffset, Is.Null);
            Assert.That(direct.FieldOffset, Is.EqualTo(0x10));
            Assert.That(nested, Is.Not.Null);
            Assert.That(nested!.ReceiverOffset, Is.EqualTo(0x10));
            Assert.That(nested.FieldOffset, Is.EqualTo(0x10));
        });
    }

    [TestCase(false, "narrow-load")]
    [TestCase(false, "wrong-receiver")]
    [TestCase(false, "wrong-branch")]
    [TestCase(false, "wrong-stack")]
    [TestCase(false, "missing-helper")]
    [TestCase(false, "prefix")]
    [TestCase(true, "narrow-load")]
    [TestCase(true, "wrong-receiver")]
    [TestCase(true, "wrong-parent")]
    [TestCase(true, "wrong-branch")]
    [TestCase(true, "wrong-stack")]
    [TestCase(true, "missing-helper")]
    [TestCase(true, "prefix")]
    public void NearbyNativeShapesRemainUnproved(bool nested, string defect)
    {
        var body = Body(nested);
        var position = defect switch
        {
            "wrong-parent" => 1,
            "wrong-branch" => nested ? 3 : 2,
            "narrow-load" or "wrong-receiver" or "prefix" => nested ? 4 : 3,
            "wrong-stack" => nested ? 5 : 4,
            _ => body.Count - 1,
        };
        var instruction = body[position];
        switch (defect)
        {
            case "narrow-load": instruction.Code = Code.Mov_r32_rm32; break;
            case "wrong-receiver": instruction.MemoryBase = Register.RDX; break;
            case "wrong-parent": instruction.MemoryBase = Register.RDX; break;
            case "wrong-branch": instruction.NearBranch64 = body[^2].IP; break;
            case "wrong-stack": instruction.Immediate8to64 = 0x20; break;
            case "missing-helper": instruction.Code = Code.Nopd; break;
            case "prefix": instruction.HasLockPrefix = true; break;
        }
        body[position] = instruction;
        Assert.That(X64Guarded64BitFieldReadProof.TryProveShape(body), Is.Null);
    }

    private static List<Instruction> Body(bool nested)
    {
        const ulong address = 0x1000;
        var bytes = Convert.FromHexString(nested
            ? "4883EC28488B41104885C07409488B40104883C428C3E811000000"
            : "4883EC284885C97409488B41104883C428C3E811000000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var body = new List<Instruction>();
        while (decoder.IP < address + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body;
    }
}
