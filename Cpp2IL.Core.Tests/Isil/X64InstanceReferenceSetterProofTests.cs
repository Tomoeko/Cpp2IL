using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64InstanceReferenceSetterProofTests
{
    [Test]
    public void LeafShapeRequiresTheReceiverValueAndTerminalTail()
    {
        var shape = X64InstanceReferenceSetterProof.TryProveShape(SyntheticLeaf());
        Assert.Multiple(() =>
        {
            Assert.That(shape?.FieldOffset, Is.EqualTo(0x18));
            Assert.That(shape?.BarrierTarget, Is.EqualTo(0x100D));
        });
    }

    [TestCase("receiver")]
    [TestCase("offset")]
    [TestCase("store-width")]
    [TestCase("store-base")]
    [TestCase("stored-value")]
    [TestCase("tail-call")]
    [TestCase("extra-instruction")]
    public void SimilarLeavesDoNotAcquireAReferenceStoreProof(string defect)
    {
        var body = SyntheticLeaf();
        if (defect == "extra-instruction")
            body = [..body, Instruction.Create(Code.Retnq)];
        else
        {
            var index = defect switch
            {
                "receiver" or "offset" => 0,
                "tail-call" => 2,
                _ => 1,
            };
            var instruction = body[index];
            switch (defect)
            {
                case "receiver": instruction.Op0Register = Register.RDX; break;
                case "offset": instruction.Immediate8to64 = 0; break;
                case "store-width": instruction.Code = Code.Mov_rm32_r32; break;
                case "store-base": instruction.MemoryBase = Register.RAX; break;
                case "stored-value": instruction.Op1Register = Register.RAX; break;
                case "tail-call": instruction.Code = Code.Call_rel32_64; break;
            }
            body[index] = instruction;
        }
        Assert.That(X64InstanceReferenceSetterProof.TryProveShape(body), Is.Null,
            defect);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsEachFoldedSetterAndRejectsChangedEvidence()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_INSTANCE_REFERENCE_SETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INSTANCE_REFERENCE_SETTER_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("InstanceReferenceSetterFixture")!;
            var setters = assembly.Types.SelectMany(type => type.Methods)
                .Where(method => method.Name == "set_Value").ToArray();
            Assert.That(setters, Has.Length.EqualTo(2));
            Assert.That(setters.Select(method => method.UnderlyingPointer).Distinct()
                    .ToArray(),
                Has.Length.EqualTo(1), "the original player folded both setter bodies");

            foreach (var method in setters)
            {
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(method.RawBytes.Length, Is.GreaterThan(12),
                    "the metadata size estimate extends into another function");
                Assert.That(native.Take(3).Select(instruction => instruction.Code),
                    Is.EqualTo(new[] { Code.Add_rm64_imm8, Code.Mov_rm64_r64,
                        Code.Jmp_rel32_64 }));
                var evidence = X64InstanceReferenceSetterProof.Find(method, native);
                Assert.That(evidence?.Field.Name, Is.EqualTo("Stored"), method.FullName);
                Assert.That(evidence?.Field.Offset, Is.EqualTo(0x18), method.FullName);

                var field = evidence!.Field;
                try
                {
                    field.OverrideOffset = field.DefaultOffset + 8;
                    Assert.That(X64InstanceReferenceSetterProof.Find(method, native),
                        Is.Null, "changed field offset");
                }
                finally { field.OverrideOffset = null; }
                try
                {
                    field.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                    Assert.That(X64InstanceReferenceSetterProof.Find(method, native),
                        Is.Null, "changed field type");
                }
                finally { field.OverrideFieldType = null; }
                try
                {
                    field.OverrideAttributes = field.DefaultAttributes |
                        FieldAttributes.InitOnly;
                    Assert.That(X64InstanceReferenceSetterProof.Find(method, native),
                        Is.Null, "readonly field");
                }
                finally { field.OverrideAttributes = null; }

                var property = method.DeclaringType!.Properties.Single(candidate =>
                    ReferenceEquals(candidate.Setter, method));
                try
                {
                    property.OverridePropertyType = app.SystemTypes.SystemInt32Type;
                    Assert.That(X64InstanceReferenceSetterProof.Find(method, native),
                        Is.Null, "changed property type");
                }
                finally { property.OverridePropertyType = null; }
            }

            var aliases = app.MethodsByAddress[setters[0].UnderlyingPointer];
            var constructor = assembly.Types.Single(type => type.Name ==
                "ReferenceCell").Methods.Single(method => method.Name == ".ctor");
            aliases.Add(constructor);
            try
            {
                Assert.That(X64InstanceReferenceSetterProof.Find(setters[0]), Is.Null,
                    "a same-assembly alias must bind to the same setter shape");
            }
            finally { aliases.Remove(constructor); }
            Assert.That(X64InstanceReferenceSetterProof.Find(setters[0]), Is.Not.Null);

            var changedBinary = File.ReadAllBytes(binary);
            var pe = (PE)app.Binary;
            var raw = checked((int)pe.MapVirtualAddressToRaw(setters[0].UnderlyingPointer,
                false));
            Assert.That(changedBinary[raw + 7], Is.EqualTo((byte)0xE9));
            changedBinary[raw + 8] ^= 1; // Retarget the native tail away from the proved card marker.
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("InstanceReferenceSetterFixture")!.Types
                .Single(type => type.Name == "ReferenceCell").Methods
                .Single(method => method.Name == "set_Value");
            Assert.That(X64InstanceReferenceSetterProof.Find(changed), Is.Null,
                "a different terminal helper is not a managed reference store");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] SyntheticLeaf()
    {
        var bytes = Convert.FromHexString("4883C118488911E901000000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        return [decoder.Decode(), decoder.Decode(), decoder.Decode()];
    }
}
