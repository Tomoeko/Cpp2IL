using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64GuardedSinkCallProofTests
{
    [Test]
    public void ExactPlayerBindsBothLiteralsAndRejectsNativeMutations()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_GUARDED_SINK_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GUARDED_SINK_FIXTURE_INPUT to a neutral exact player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("GuardedSinkFixture")!.Types
                .Single(type => type.Name == "Forwarder");
            var sink = app.GetAssemblyByName("Neutral.GuardedSink")!.Types
                .Single(type => type.Name == "GuardedSink");
            var north = owner.Methods.Single(method => method.Name == "SendNorth");
            var south = owner.Methods.Single(method => method.Name == "SendSouth");
            var accept = sink.Methods.Single(method => method.Name == "Accept");
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;

            var northBody = BoundedBody(north, unwind);
            var southBody = BoundedBody(south, unwind);
            var northShape = X64GuardedSinkCallProof.TryProveShape(northBody);
            var southShape = X64GuardedSinkCallProof.TryProveShape(southBody);
            Assert.Multiple(() =>
            {
                Assert.That(north.RawBytes.Length, Is.EqualTo(93));
                Assert.That(south.RawBytes.Length, Is.GreaterThan(93),
                    "The second method exercises an overlong inferred raw span.");
                Assert.That(northBody.Length, Is.EqualTo(20));
                Assert.That(southBody.Length, Is.EqualTo(20));
                Assert.That(northShape, Is.Not.Null);
                Assert.That(southShape, Is.Not.Null);
                Assert.That(owner.Definition!.HasCctor, Is.False);
                Assert.That(sink.Definition!.HasCctor, Is.True);
            });
            Assert.Multiple(() =>
            {
                Assert.That(northShape!.Value.TypeInfoSlot,
                    Is.EqualTo(southShape!.Value.TypeInfoSlot));
                Assert.That(northShape.Value.LiteralSlot,
                    Is.Not.EqualTo(southShape.Value.LiteralSlot));
                Assert.That(northShape.Value.OnceFlag,
                    Is.Not.EqualTo(southShape.Value.OnceFlag));
                Assert.That(northShape.Value.Tail,
                    Is.EqualTo(southShape.Value.Tail));
            });

            var northEvidence = X64GuardedSinkCallProof.Find(north);
            var southEvidence = X64GuardedSinkCallProof.Find(south);
            Assert.Multiple(() =>
            {
                Assert.That(northEvidence?.Literal, Is.EqualTo("north"));
                Assert.That(southEvidence?.Literal, Is.EqualTo("south"));
                Assert.That(northEvidence?.SinkMethod, Is.SameAs(accept));
                Assert.That(southEvidence?.SinkMethod, Is.SameAs(accept));
            });

            foreach (var mutation in new[] { "once branch", "literal helper",
                         "class status", "class indexed status", "class branch", "hidden MethodInfo",
                         "exception argument", "tail kind" })
            {
                var changed = southBody.ToArray();
                switch (mutation)
                {
                    case "once branch":
                        changed[4].NearBranch64 = changed[9].IP; break;
                    case "literal helper":
                        changed[8].NearBranch64 = changed[13].NearBranchTarget; break;
                    case "class status":
                        changed[11].MemoryDisplacement64++; break;
                    case "class indexed status":
                        changed[11].MemoryIndex = Register.RAX; break;
                    case "class branch":
                        changed[12].NearBranch64 = changed[15].IP; break;
                    case "hidden MethodInfo":
                        changed[15].Op0Register = Register.ECX; break;
                    case "exception argument":
                        changed[16].Op1Register = Register.RCX; break;
                    case "tail kind":
                        changed[19].Code = Code.Call_rel32_64; break;
                }
                Assert.That(X64GuardedSinkCallProof.TryProveShape(changed),
                    Is.Null, mutation);
            }

            Assert.That(X64GuardedSinkCallProof.BindProvedShape(south,
                southShape!.Value with
                {
                    LiteralSlot = southShape.Value.TypeInfoSlot
                }), Is.Null, "A TypeInfo slot is not a literal.");
            Assert.That(X64GuardedSinkCallProof.BindProvedShape(south,
                southShape.Value with
                {
                    TypeInfoSlot = southShape.Value.LiteralSlot
                }), Is.Null, "A literal slot is not sink TypeInfo.");
            Assert.That(X64GuardedSinkCallProof.BindProvedShape(south,
                southShape.Value with
                {
                    ClassInitializer = southShape.Value.MetadataInitializer
                }), Is.Null, "The class-init call must bind the exact export.");

            var sinkDefinition = sink.Definition!;
            var sinkBits = sinkDefinition.Bitfield;
            try
            {
                sinkDefinition.Bitfield &= ~(1u << 3);
                Assert.That(X64GuardedSinkCallProof.Find(south), Is.Null,
                    "A sink without an explicit cctor is outside the proof.");
            }
            finally { sinkDefinition.Bitfield = sinkBits; }

            var tailBindings = app.MethodsByAddress[southShape.Value.Tail];
            tailBindings.Add(north);
            try
            {
                Assert.That(X64GuardedSinkCallProof.Find(south), Is.Null,
                    "The sink tail must have one method identity.");
            }
            finally { tailBindings.Remove(north); }
            Assert.That(X64GuardedSinkCallProof.Find(south), Is.Not.Null);

            var original = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var nativeMutation in new[]
                     {
                         (name: "once store", address: southBody[9].NextIP - 1),
                         (name: "literal helper", address: southBody[8].IP + 1),
                         (name: "class initializer", address: southBody[13].IP + 1),
                         (name: "tail target", address: southBody[19].IP + 1),
                     })
            {
                var raw = checked((int)pe.MapVirtualAddressToRaw(
                    nativeMutation.address, false));
                Assert.That(raw, Is.GreaterThanOrEqualTo(0));
                var changed = (byte[])original.Clone();
                changed[raw] ^= 1;
                AssertRejected(changed, metadataBytes, nativeMutation.name);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] BoundedBody(
        Cpp2IL.Core.Model.Contexts.MethodAnalysisContext method,
        X64UnwindProof.Index unwind)
    {
        method.EnsureRawBytes();
        var region = unwind.ClassifySpan(method.UnderlyingPointer,
            method.UnderlyingPointer + 1);
        Assert.That(region.Kind,
            Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That(region.Start, Is.EqualTo(method.UnderlyingPointer));
        Assert.That(region.RootStart, Is.EqualTo(method.UnderlyingPointer));
        Assert.That(region.End - region.Start, Is.EqualTo(93));
        return X86Utils.Iterate(method.RawBytes.AsSpan().Slice(0, 93),
            method.UnderlyingPointer, is32Bit: false).ToArray();
    }

    private static void AssertRejected(byte[] binary, byte[] metadata,
        string mutation)
    {
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
            UnityVersion.Parse("2021.3.35f1"));
        var method = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("GuardedSinkFixture")!.Types
            .Single(type => type.Name == "Forwarder").Methods
            .Single(candidate => candidate.Name == "SendSouth");
        Assert.That(X64GuardedSinkCallProof.Find(method), Is.Null, mutation);
    }
}
