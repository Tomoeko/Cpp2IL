using System;
using System.Collections.Generic;
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

public class X64StaticLiteralConcatProofTests
{
    [Test]
    [NonParallelizable]
    public void OneMethodPlayerKeepsTheClosedStaticConcatShape()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STATIC_LITERAL_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STATIC_LITERAL_CONCAT_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var owner = app.GetAssemblyByName("StaticLiteralConcatFixture")!.Types
                .Single(type => type.Name == "LiteralJoiner");
            var append = owner.Methods.Single();
            Assert.Multiple(() =>
            {
                Assert.That(append.Name, Is.EqualTo("AppendLiteral"));
                Assert.That(owner.Definition!.HasCctor, Is.False);
                Assert.That(owner.Fields.Select(field => field.Name),
                    Is.EquivalentTo(new[] { "Neighbor" }));
            });
            append.EnsureRawBytes();
            var native = X86Utils.Iterate(append).ToArray();
            Assert.That(native.Length, Is.EqualTo(14));
            var proof = X64StaticLiteralConcatProof.Find(append, native);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Literal, Is.EqualTo("|tag"));
            var lifted = X64StaticLiteralConcatProof.TryLift(append, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(lifted![0].Operands[0], Is.SameAs(proof.Concat));
                Assert.That(lifted[0].Operands[1], Is.SameAs(lifted[0].Destination));
                Assert.That(lifted[0].Operands[2],
                    Is.EqualTo(new ISIL.Register(null, "rcx")));
                Assert.That(lifted[0].Operands[3],
                    Is.EqualTo(new ISIL.StringLiteral("|tag")));
            });
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsGuardedLiteralAndStaticConcatTail()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var append = app.GetAssemblyByName("RuntimeCastConcatFixture")!.Types
                .Single(type => type.Name == "MetadataControls").Methods
                .Single(method => method.Name == "AppendLiteral");
            append.EnsureRawBytes();
            var native = X86Utils.Iterate(append).ToArray();
            Assert.That(native.Length, Is.EqualTo(14));
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(native), Is.True);

            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(append.UnderlyingPointer,
                append.UnderlyingPointer + 1);
            var flag = native[2].IPRelativeMemoryAddress;
            var slot = native[5].IPRelativeMemoryAddress;
            Assert.Multiple(() =>
            {
                Assert.That(region.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(region.End, Is.EqualTo(native[^1].NextIP));
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True);
                Assert.That(X86CallerExceptionRegionProof.Check(append, native,
                    new HashSet<ulong>()), Is.Null);
                Assert.That(unwind.IsWritableZeroInitializedRva(
                    (uint)(flag - unwind.ImageBase)), Is.True);
                Assert.That(unwind.IsWritableFileBackedRva(
                    (uint)(slot - unwind.ImageBase)), Is.True);
                Assert.That(app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot)?.Type,
                    Is.EqualTo(MetadataUsageType.StringLiteral));
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(
                    app, pe, unwind, native[6].NearBranchTarget), Is.True);
            });
            var proof = X64StaticLiteralConcatProof.Find(append, native);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Literal, Is.EqualTo("|tag"));
            Assert.That(proof.Concat.Name, Is.EqualTo("Concat"));
            var lifted = X64StaticLiteralConcatProof.TryLift(append, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(lifted!.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.Call, ISIL.OpCode.Return }));
                Assert.That(lifted[0].Operands[0], Is.SameAs(proof.Concat),
                    "the static Concat target is operand zero");
                Assert.That(lifted[0].Operands[1], Is.SameAs(lifted[0].Destination),
                    "the return destination is operand one");
                Assert.That(lifted[0].Operands[2],
                    Is.EqualTo(new ISIL.Register(null, "rcx")),
                    "the source string parameter precedes the literal");
                Assert.That(lifted[0].Operands[3],
                    Is.EqualTo(new ISIL.StringLiteral(proof.Literal)));
                Assert.That(lifted[0].Operands[4],
                    Is.EqualTo(new ISIL.Immediate(0)),
                    "the trailing native MethodInfo argument is ignored by managed Concat");
            });

            var callerBindings = app.MethodsByAddress[append.UnderlyingPointer];
            var unrelated = append.DeclaringType!.Methods.Single(method =>
                method.Name == "TargetType");
            callerBindings.Add(unrelated);
            try
            {
                Assert.That(X64StaticLiteralConcatProof.Find(append, native), Is.Null,
                    "a shared caller address cannot prove this method's body");
            }
            finally { callerBindings.Remove(unrelated); }

            var ownerDefinition = append.DeclaringType.Definition!;
            var originalBitfield = ownerDefinition.Bitfield;
            try
            {
                ownerDefinition.Bitfield |= 1U << 3;
                Assert.That(X64StaticLiteralConcatProof.Find(append, native), Is.Null,
                    "a class initializer can add an unproved managed effect");
            }
            finally { ownerDefinition.Bitfield = originalBitfield; }
            try
            {
                ownerDefinition.Bitfield &= ~(1U << 10);
                Assert.That(X64StaticLiteralConcatProof.Find(append, native), Is.Null,
                    "custom packing is outside the ordinary owner contract");
            }
            finally { ownerDefinition.Bitfield = originalBitfield; }
            var originalFlags = ownerDefinition.Flags;
            try
            {
                ownerDefinition.Flags = (originalFlags & ~(uint)TypeAttributes.LayoutMask) |
                    (uint)TypeAttributes.SequentialLayout;
                Assert.That(X64StaticLiteralConcatProof.Find(append, native), Is.Null,
                    "nondefault layout is outside the ordinary owner contract");
            }
            finally { ownerDefinition.Flags = originalFlags; }

            var wrongBranch = native.ToArray();
            wrongBranch[4].NearBranch64 = native[7].IP;
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(wrongBranch), Is.False);
            var wrongFlagStore = native.ToArray();
            wrongFlagStore[7].MemoryDisplacement64 += 1;
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(wrongFlagStore), Is.False);
            var wrongSlotLoad = native.ToArray();
            wrongSlotLoad[8].MemoryDisplacement64 += 8;
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(wrongSlotLoad), Is.False);
            var wrongWidth = native.ToArray();
            wrongWidth[8].Code = Code.Mov_r32_rm32;
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(wrongWidth), Is.False);
            var wrongHelper = native.ToArray();
            wrongHelper[6].NearBranch64 = native[13].NearBranchTarget;
            Assert.That(X64StaticLiteralConcatProof.Find(append, wrongHelper), Is.Null);
            var wrongTail = native.ToArray();
            wrongTail[13].NearBranch64 = native[6].NearBranchTarget;
            Assert.That(X64StaticLiteralConcatProof.Find(append, wrongTail), Is.Null);
            var extraEffect = native.ToArray();
            extraEffect[9].Code = Code.Inc_rm32;
            Assert.That(X64StaticLiteralConcatProof.TryProveShape(extraEffect), Is.False);
            try
            {
                append.Attributes |= MethodAttributes.Virtual;
                Assert.That(X64StaticLiteralConcatProof.Find(append, native), Is.Null);
            }
            finally { append.Attributes = append.DefaultAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
