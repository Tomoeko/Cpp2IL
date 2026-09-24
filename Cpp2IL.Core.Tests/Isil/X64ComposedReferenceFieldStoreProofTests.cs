using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ComposedReferenceFieldStoreProofTests
{
    [Test]
    public void ShapeRetainsTheThreeDifferentFieldAddressesAndBothTerminalHelpers()
    {
        var body = SyntheticBody();
        var shape = X64ComposedReferenceFieldStoreProof.TryProveShape(body);
        Assert.Multiple(() =>
        {
            Assert.That(shape?.MarkerOffset, Is.EqualTo(0x44));
            Assert.That(shape?.SourceOffset, Is.EqualTo(0x38));
            Assert.That(shape?.DestinationOffset, Is.EqualTo(0x50));
            Assert.That(shape?.BarrierTarget, Is.EqualTo(0x2000));
            Assert.That(shape?.NullTarget, Is.EqualTo(0x3000));
        });
    }

    [TestCase("marker-width")]
    [TestCase("source-order")]
    [TestCase("source-owner")]
    [TestCase("holder-copy")]
    [TestCase("null-branch")]
    [TestCase("stored-value")]
    [TestCase("store-width")]
    [TestCase("stack-restoration")]
    [TestCase("barrier-transfer")]
    [TestCase("null-target")]
    [TestCase("extra-exit")]
    public void NeighboringShapesDoNotProveTheComposedStore(string defect)
    {
        var body = SyntheticBody();
        if (defect == "extra-exit")
            body = [..body, Instruction.Create(Code.Retnq)];
        else if (defect == "source-order")
            (body[1], body[3]) = (body[3], body[1]);
        else
        {
            var index = defect switch
            {
                "marker-width" => 1,
                "holder-copy" => 2,
                "source-owner" => 3,
                "null-branch" => 5,
                "stored-value" or "store-width" => 7,
                "stack-restoration" => 8,
                "barrier-transfer" => 9,
                _ => 10,
            };
            var instruction = body[index];
            switch (defect)
            {
                case "marker-width": instruction.Code = Code.Inc_rm64; break;
                case "holder-copy": instruction.Op0Register = Register.RBX; break;
                case "source-owner": instruction.MemoryBase = Register.RAX; break;
                case "null-branch": instruction.NearBranch64 = body[9].IP; break;
                case "stored-value": instruction.Op1Register = Register.RAX; break;
                case "store-width": instruction.Code = Code.Mov_rm32_r32; break;
                case "stack-restoration": instruction.Immediate8to64 = 0x20; break;
                case "barrier-transfer": instruction.Code = Code.Call_rel32_64; break;
                case "null-target": instruction.NearBranch64 = 0; break;
            }
            body[index] = instruction;
        }
        Assert.That(X64ComposedReferenceFieldStoreProof.TryProveShape(body), Is.Null, defect);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresUnchangedFieldsAndNativeBindings()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var method = app.GetAssemblyByName("ComposedArrayFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "ReplaceValues");
            var native = X86Utils.Iterate(method).ToArray();
            var proof = X64ComposedReferenceFieldStoreProof.Find(method, native);
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.MarkerField.FieldType, Is.SameAs(app.SystemTypes.SystemInt32Type));
                Assert.That(proof.SourceField.Name, Is.EqualTo("Replacement"));
                Assert.That(proof.DestinationField.Name, Is.EqualTo("Items"));
                Assert.That(proof.MarkerIncrementIp, Is.LessThan(proof.SourceReadIp));
                Assert.That(proof.SourceReadIp, Is.LessThan(proof.DestinationStoreIp));
                Assert.That(proof.DestinationStoreIp, Is.LessThan(proof.BarrierTailIp));
                Assert.That(proof.NativeEndExclusiveIp, Is.GreaterThan(proof.NullHelperCallIp));
            });

            var lifted = X64ComposedReferenceFieldStoreProof.TryLift(method, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(lifted!.Select(instruction => instruction.OpCode), Is.EqualTo(new[]
                {
                    Core.ISIL.OpCode.Add, Core.ISIL.OpCode.Move,
                    Core.ISIL.OpCode.Move, Core.ISIL.OpCode.Return,
                }));
                Assert.That(lifted[0].IntegerBitWidth, Is.EqualTo(32));
                Assert.That(lifted.Select(instruction => instruction.NativeAddress), Is.EqualTo(new ulong?[]
                {
                    proof!.MarkerIncrementIp, proof.SourceReadIp,
                    proof.DestinationStoreIp, proof.BarrierTailIp,
                }));
            });

            try
            {
                proof!.DestinationField.OverrideOffset = proof.DestinationField.DefaultOffset + 8;
                Assert.That(X64ComposedReferenceFieldStoreProof.Find(method, native), Is.Null);
            }
            finally { proof!.DestinationField.OverrideOffset = null; }
            try
            {
                proof!.DestinationField.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ComposedReferenceFieldStoreProof.Find(method, native), Is.Null);
            }
            finally { proof!.DestinationField.Attributes = proof.DestinationField.DefaultAttributes; }

            var wrongNull = native.ToArray();
            wrongNull[10].NearBranch64 = native[9].NearBranchTarget;
            Assert.That(X64ComposedReferenceFieldStoreProof.Find(method, wrongNull), Is.Null);
            var wrongBarrier = native.ToArray();
            wrongBarrier[9].NearBranch64 = native[10].NearBranchTarget;
            Assert.That(X64ComposedReferenceFieldStoreProof.Find(method, wrongBarrier), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] SyntheticBody()
    {
        // Invented offsets and helper addresses; these are not copied from a player.
        var bytes = Convert.FromHexString(
            "4883EC28FF4144488BC2488B51384885C07410488D48504889114883C428E9DD0F0000E8D81F0000");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        return Enumerable.Range(0, 11).Select(_ => decoder.Decode()).ToArray();
    }
}
