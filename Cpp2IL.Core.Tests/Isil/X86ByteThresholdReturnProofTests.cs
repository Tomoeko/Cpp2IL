using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ByteThresholdReturnProofTests
{
    [Test]
    public void ExactUnsignedByteComparisonUsesTheDeclaredFieldOffset()
    {
        var shape = X86ByteThresholdReturnProof.TryProveShape(Decode("807910800F93C0C3"));
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.FieldOffset, Is.EqualTo(16));
            Assert.That(shape.End, Is.EqualTo(0x1008));
        });
    }

    [Test]
    public void LastNullPageByteRemainsInTheProvedShape()
    {
        var shape = X86ByteThresholdReturnProof.TryProveShape(Decode("80B9FF0F0000800F93C0C3"));
        Assert.That(shape?.FieldOffset, Is.EqualTo(0xFFF));
    }

    [TestCase("8079107F0F93C0C3")] // a different threshold
    [TestCase("807910800F92C0C3")] // unsigned below
    [TestCase("807910800F94C0C3")] // equality
    [TestCase("807A10800F93C0C3")] // a different receiver
    [TestCase("807C1110800F93C0C3")] // indexed memory
    [TestCase("64807910800F93C0C3")] // segment-relative memory
    [TestCase("80790F800F93C0C3")] // object header, not an instance field
    [TestCase("80B900100000800F93C0C3")] // null RCX may read beyond the null page
    [TestCase("80F9800F93C0C3")] // partial register comparison
    [TestCase("807910800F9301C3")] // SETAE writes memory
    [TestCase("807910800F93C090C3")] // extra operation before the return
    [TestCase("807910800F93C0C20800")] // return changes the caller's stack
    public void NeighboringNativeShapesRemainUnproved(string bytes)
    {
        Assert.That(X86ByteThresholdReturnProof.TryProveShape(Decode(bytes)), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresUnchangedByteFieldAndCompleteNativeBody()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BYTE_THRESHOLD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BYTE_THRESHOLD_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var method = app.GetAssemblyByName("ByteThresholdFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "HasHighBit");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var field = X86ByteThresholdReturnProof.Find(method, native);
            Assert.That(field?.Name, Is.EqualTo("Value"));
            Assert.That(X86ByteThresholdReturnProof.TryLift(method, native)?.Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.CheckGreaterOrEqualUnsigned,
                    ISIL.OpCode.Return }));
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.CheckGreaterOrEqualUnsigned,
                    ISIL.OpCode.Return }));

            var originalAttributes = field!.Attributes;
            try
            {
                field.OverrideOffset = field.DefaultOffset + 8;
                Assert.That(X86ByteThresholdReturnProof.Find(method, native), Is.Null);
            }
            finally { field.OverrideOffset = null; }
            try
            {
                field.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X86ByteThresholdReturnProof.Find(method, native), Is.Null);
            }
            finally { field.Attributes = originalAttributes; }
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
