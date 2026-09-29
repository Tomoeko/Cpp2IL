using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64SideEffectClassCctorFixtureTests
{
    private static (string Binary, string Metadata) Inputs()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_SIDE_EFFECT_CLASS_CCTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SIDE_EFFECT_CLASS_CCTOR_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        return (binary, metadata);
    }

    private static MethodAnalysisContext Constructor() =>
        Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("SideEffectClassCctorFixture")!.Types
            .Single(type => type.Name == "StaticCells")
            .Methods.Single(method => method.Name == ".cctor");

    [Test]
    public void CompleteBodyAndStaticClassBindingsAreRequired()
    {
        var (binary, metadata) = Inputs();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
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
            });

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            var shape = X64StructStaticConstructorProof.TryProveShape(body);
            Assert.That(body, Has.Length.EqualTo(19));
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(body[3].IPRelativeMemoryAddress, Is.EqualTo(shape!.WitnessSlot));
                Assert.That(body[5].IPRelativeMemoryAddress, Is.EqualTo(shape.OwnerSlot));
                Assert.That(shape.WitnessSlot, Is.Not.EqualTo(shape.OwnerSlot));
                Assert.That(shape.MarkerOffset, Is.EqualTo(0));
                Assert.That(shape.BiasOffset, Is.EqualTo(4));
            });

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
                         ("owner slot reused for final store", 14, instruction =>
                         { instruction.MemoryDisplacement64 = shape!.WitnessSlot; return instruction; }),
                         ("negative-zero bits", 16, instruction =>
                         { instruction.Immediate32 = 0; return instruction; })
                     })
            {
                var changed = body.ToArray();
                changed[index] = mutate(changed[index]);
                Assert.That(X64StructStaticConstructorProof.TryProveShape(changed),
                    Is.Null, name);
            }

            var owner = method.DeclaringType!;
            try
            {
                owner.OverrideAttributes = owner.DefaultAttributes & ~TypeAttributes.Abstract;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "the declaring class must remain static");
            }
            finally { owner.OverrideAttributes = null; }

            try
            {
                proof!.Bias.OverrideOffset = 8;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "a changed static-field layout cannot keep the same store");
            }
            finally { proof!.Bias.OverrideOffset = null; }

            try
            {
                proof!.Witness.OverrideFieldType = method.AppContext.SystemTypes.SystemSingleType;
                Assert.That(X64StructStaticConstructorProof.Find(method), Is.Null,
                    "a changed witness type changes the increment");
            }
            finally { proof!.Witness.OverrideFieldType = null; }

            Assert.That(X64StructStaticConstructorProof.Find(method), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void MutatedPlayerSideEffectIsRejected()
    {
        var (binary, metadata) = Inputs();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var method = Constructor();
            method.EnsureRawBytes();
            var increment = X86Utils.Iterate(method).ElementAt(10);
            Assert.That(increment.Code, Is.EqualTo(Code.Inc_rm32));
            var offset = checked((int)((PE)method.AppContext.Binary)
                .MapVirtualAddressToRaw(increment.IP, false));
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
