using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86DirectBooleanFieldGetterProofTests
{
    [TestCase("0FB64110C3", 0x10)]
    [TestCase("66900FB64110C3", 0x10)]
    [TestCase("900FB64110C3", 0x10)]
    [TestCase("0FB681FF0F0000C3", 0xFFF)]
    public void CompleteByteGetterShapeRetainsItsFieldOffset(string bytes, int offset)
    {
        var native = Decode(bytes);
        var shape = X86DirectBooleanFieldGetterProof.TryProveShape(native);
        Assert.Multiple(() =>
        {
            Assert.That(shape?.FieldOffset, Is.EqualTo(offset));
            Assert.That(shape?.End, Is.EqualTo(0x1000 + Convert.FromHexString(bytes).Length));
        });
    }

    [TestCase("0FB65110C3")] // EDX instead of the returned EAX
    [TestCase("0FB64210C3")] // another receiver register
    [TestCase("0FB6440110C3")] // indexed memory
    [TestCase("0FBE4110C3")] // signed byte extension
    [TestCase("0FB74110C3")] // word extension
    [TestCase("0FB6410FC3")] // object header
    [TestCase("0FB68100100000C3")] // null receiver need not fault
    [TestCase("0FB64110")]
    [TestCase("0FB6411090C3")] // extra operation
    [TestCase("900FB6411090C3")] // extra leading and trailing operations
    [TestCase("0FB64110C20800")] // caller stack adjustment
    [TestCase("640FB64110C3")] // segment override
    [TestCase("F30FB64110C3")] // repeat prefix
    public void NeighboringNativeShapesRemainUnproved(string bytes)
    {
        Assert.That(X86DirectBooleanFieldGetterProof.TryProveShape(Decode(bytes)), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsEachGetterToItsOwnUnchangedBooleanField()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_GETTER_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("BooleanGetterFixture")!;
            foreach (var ownerName in new[] { "FirstState", "SecondState" })
            {
                var owner = assembly.Types.Single(type => type.Name == ownerName);
                var method = owner.Methods.Single(candidate => candidate.Name == "ReadValue");
                var field = owner.Fields.Single(candidate => candidate.Name == "Value");
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(X86DirectBooleanFieldGetterProof.Find(method, native), Is.SameAs(field),
                    $"{ownerName} must bind its complete native body to its own Boolean field");
                var lifted = app.InstructionSet.GetIsilFromMethod(method);
                Assert.That(lifted.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.Return }));
                Assert.That(lifted[0].IntegerBitWidth, Is.EqualTo(8));
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction => instruction is
                    { OpCode: ISIL.OpCode.Move,
                        Operands: [_, Cpp2IL.Core.ISIL.FieldReference resolved] } &&
                    ReferenceEquals(resolved.Field, field)), Is.True,
                    $"{ownerName} must retain the own-field binding in typed ISIL");

                try
                {
                    field.OverrideOffset = field.DefaultOffset + 8;
                    Assert.That(X86DirectBooleanFieldGetterProof.Find(method, native), Is.Null);
                }
                finally { field.OverrideOffset = null; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        var body = new List<Instruction>();
        while (decoder.IP < 0x1000 + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body.ToArray();
    }
}
