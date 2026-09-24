using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player proof and mutation checks for a bounded struct initializer.</summary>
[NonParallelizable]
public class X64StructStaticConstructorFixtureTests
{
    private static (string Binary, string Metadata) Inputs()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STRUCT_STATIC_FORWARD_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STRUCT_STATIC_FORWARD_CALL_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        return (binary, metadata);
    }

    private static MethodAnalysisContext Constructor() =>
        Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("StructStaticForwardCallFixture")!.Types
            .Single(type => type.Name == "FloatPair").Methods.Single(method => method.Name == ".cctor");

    [Test]
    public void CompleteBodyAndMetadataAreRequired()
    {
        var (binary, metadata) = Inputs();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var method = Constructor();
            var proof = X64StructStaticConstructorProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.Witness.Name, Is.EqualTo("Events"));
                Assert.That(proof.Marker.Name, Is.EqualTo("Marker"));
                Assert.That(proof.Bias.Name, Is.EqualTo("Bias"));
                Assert.That(proof.MarkerValue, Is.EqualTo(37));
                Assert.That(proof.BiasBits, Is.EqualTo(0x8000_0000));
                Assert.That(BitConverter.SingleToInt32Bits(
                    BitConverter.ToSingle(BitConverter.GetBytes(proof.BiasBits), 0)),
                    Is.EqualTo(unchecked((int)0x8000_0000)));
            });

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            Assert.That(body, Has.Length.EqualTo(19));
            Assert.That(X64StructStaticConstructorProof.TryProveShape(body), Is.Not.Null);
            foreach (var (name, index, mutate) in new (string, int, Func<Instruction, Instruction>)[]
                     {
                         ("branch merge", 2, instruction =>
                         { instruction.NearBranch64 = body[9].IP; return instruction; }),
                         ("second metadata helper", 6, instruction =>
                         { instruction.NearBranch64 = body[8].IP; return instruction; }),
                         ("once flag store", 7, instruction =>
                         { instruction.Immediate8 = 2; return instruction; }),
                         ("witness side effect", 10, instruction =>
                         { instruction.Code = Code.Dec_rm32; return instruction; }),
                         ("class static-fields offset", 12, instruction =>
                         { instruction.MemoryDisplacement64 = 0xB0; return instruction; }),
                         ("owner metadata slot", 14, instruction =>
                         { instruction.MemoryDisplacement64 = body[5].MemoryDisplacement64;
                             return instruction; }),
                         ("negative zero bits", 16, instruction =>
                         { instruction.Immediate32 = 0; return instruction; })
                     })
            {
                var changed = body.ToArray();
                changed[index] = mutate(changed[index]);
                Assert.That(X64StructStaticConstructorProof.TryProveShape(changed), Is.Null, name);
            }

            var secondInstanceField = method.DeclaringType!.Fields.Single(field => field.Name == "Second");
            try
            {
                secondInstanceField.OverrideOffset = 8;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "changed value-type ABI layout");
            }
            finally { secondInstanceField.OverrideOffset = null; }

            try
            {
                proof!.Witness.OverrideFieldType = method.AppContext.SystemTypes.SystemSingleType;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "changed witness field type");
            }
            finally { proof!.Witness.OverrideFieldType = null; }

            var originalAttributes = proof!.Witness.RawIl2CppCustomAttributeData;
            try
            {
                proof.Witness.RawIl2CppCustomAttributeData = new BinarySlice([0x01]);
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "a field attribute can change static storage semantics");
            }
            finally { proof.Witness.RawIl2CppCustomAttributeData = originalAttributes; }

            var oldBitfield = proof!.Witness.DeclaringType!.Definition!.Bitfield;
            try
            {
                proof.Witness.DeclaringType.Definition.Bitfield |= 1u << 3;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "a witness static constructor adds a managed side effect");
            }
            finally { proof.Witness.DeclaringType.Definition.Bitfield = oldBitfield; }

            var bindings = method.AppContext.MethodsByAddress[method.UnderlyingPointer];
            bindings.Add(method);
            try
            {
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "a shared native address is ambiguous");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            Assert.That(X64StructStaticConstructorProof.Find(method), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void MutatedPlayerInstructionIsRejected()
    {
        var (binary, metadata) = Inputs();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var method = Constructor();
            method.EnsureRawBytes();
            var increment = X86Utils.Iterate(method).ElementAt(10);
            Assert.That(increment.Code, Is.EqualTo(Code.Inc_rm32));
            var offset = checked((int)((PE)method.AppContext.Binary).MapVirtualAddressToRaw(
                increment.IP, false));
            var alteredBinary = File.ReadAllBytes(binary);
            Assert.Multiple(() =>
            {
                Assert.That(alteredBinary[offset], Is.EqualTo(0xFF));
                Assert.That(alteredBinary[offset + 1], Is.EqualTo(0x01));
            });
            alteredBinary[offset + 1] = 0x09; // dec dword ptr [rcx]

            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(alteredBinary, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            Assert.That(X64StructStaticConstructorProof.Find(Constructor()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
