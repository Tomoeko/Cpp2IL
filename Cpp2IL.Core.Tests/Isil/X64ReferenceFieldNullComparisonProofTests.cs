using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedOpCode = Cpp2IL.Core.ISIL.OpCode;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64ReferenceFieldNullComparisonProofTests
{
    [TestCase("48837910000F94C0C3", ManagedOpCode.CheckEqual)]
    [TestCase("48837910000F95C0C3", ManagedOpCode.CheckNotEqual)]
    [TestCase("48837910000F97C0C3", ManagedOpCode.CheckGreaterUnsigned)]
    [TestCase("48837910000F96C0C3", ManagedOpCode.CheckLessOrEqualUnsigned)]
    public void CompleteLeafAuthenticatesTheFlagConsumer(string hex, ManagedOpCode op)
    {
        var body = Decode(hex);
        Assert.That(IsClosed(body, op), Is.True);
    }

    [TestCase("48837910000F95C0C3")] // the opposite SETcc
    [TestCase("488379100083C0010F94C0C3")] // flag-changing ADD between CMP and SETcc
    [TestCase("48837910007400C3")] // unproved conditional branch
    [TestCase("4883791000C3")] // no flag consumer
    [TestCase("837910000F94C0C3")] // 32-bit field read
    [TestCase("48837A10000F94C0C3")] // different receiver register
    [TestCase("6748837910000F94C0C3")] // address-size override truncates RCX to ECX
    [TestCase("48837918000F94C0C3")] // different field offset
    [TestCase("48837910010F94C0C3")] // comparison against one
    [TestCase("48837910000F94C0E900000000")] // tail transfer, no return
    public void NearbyNativeBodiesRemainUnproved(string hex)
    {
        Assert.That(IsClosed(Decode(hex), ManagedOpCode.CheckEqual), Is.False);
    }

    [Test]
    public void OnlyExactSmallAlignedReceiverOffsetsAreAdmitted()
    {
        Assert.Multiple(() =>
        {
            foreach (var offset in new[] { 16, 24, 32 })
                Assert.That(X64ReferenceFieldNullComparisonProof.IsProvedNullReceiverOffset(offset, 8), Is.True);
            foreach (var offset in new[] { 8, 17, 40, 4096 })
                Assert.That(X64ReferenceFieldNullComparisonProof.IsProvedNullReceiverOffset(offset, 8), Is.False);
        });
    }

    [Test]
    public void ExactPlayerBindsFieldProvenanceAndRejectsChangedMetadata()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_FIELD_NULL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_FIELD_NULL_FIXTURE_INPUT to the neutral player-input directory.");

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
            var types = app.GetAssemblyByName("ReferenceFieldNullFixture")!.Types;
            var owner = types.Single(type => type.Name == "ReferenceOwner");
            foreach (var name in new[] { "IsCurrentNull", "HasCurrent", "IsTextNull",
                         "IsTrapReferenceNull" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.EnsureRawBytes();
                method.Analyze();
                Assert.That(FindEvidence(method), Is.Not.Null, name);
            }

            var inherited = types.Single(type => type.Name == "DerivedOwner").Methods
                .Single(method => method.Name == "IsInheritedCurrentNull");
            inherited.EnsureRawBytes();
            inherited.Analyze();
            Assert.That(FindEvidence(inherited), Is.Not.Null);

            var selected = owner.Methods.Single(method => method.Name == "IsCurrentNull");
            var comparison = Comparison(selected);
            var neighbor = owner.Fields.Single(field => field.Name == "Neighbor");
            var current = owner.Fields.Single(field => field.Name == "Current");
            try
            {
                neighbor.OverrideOffset = current.Offset;
                Assert.That(X64ReferenceFieldNullComparisonProof.TryIdentify(selected, comparison), Is.Null,
                    "an overlapping sibling field invalidates the reference-field layout");
            }
            finally { neighbor.OverrideOffset = null; }

            try
            {
                selected.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64ReferenceFieldNullComparisonProof.TryIdentify(selected, comparison), Is.Null,
                    "SETcc only establishes an ordinary Boolean return ABI");
            }
            finally { selected.OverrideReturnType = null; }

            var interior = selected.UnderlyingPointer + 5;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            try
            {
                app.MethodsByAddress.Add(interior, [selected]);
                Assert.That(X64ReferenceFieldNullComparisonProof.TryIdentify(selected, comparison), Is.Null,
                    "another managed entry inside the decoded leaf invalidates its boundary");
            }
            finally { app.MethodsByAddress.Remove(interior); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static X64ReferenceFieldNullComparisonProof.Evidence? FindEvidence(
        Cpp2IL.Core.Model.Contexts.MethodAnalysisContext method) =>
        X64ReferenceFieldNullComparisonProof.TryIdentify(method, Comparison(method));

    private static ManagedInstruction Comparison(Cpp2IL.Core.Model.Contexts.MethodAnalysisContext method) =>
        method.ControlFlowGraph!.Instructions.Single(instruction =>
            instruction.IntegerBitWidth == 64 &&
            instruction.OpCode is ManagedOpCode.CheckEqual or ManagedOpCode.CheckNotEqual or
                ManagedOpCode.CheckGreaterUnsigned or ManagedOpCode.CheckLessOrEqualUnsigned);

    private static bool IsClosed(NativeInstruction[] body, ManagedOpCode op) =>
        X64ReferenceFieldNullComparisonProof.IsClosedLeafBody(body, 0x1000,
            body[0].IP, body.Length > 1 ? body[1].IP : 0, body[^1].NextIP, 16, "rcx", op);

    private static NativeInstruction[] Decode(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0x1000);
        var body = new System.Collections.Generic.List<NativeInstruction>();
        while (decoder.IP < 0x1000 + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body.ToArray();
    }
}
