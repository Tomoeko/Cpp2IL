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

[NonParallelizable]
public class X64ScalarFloatRefMutationProofTests
{
    [Test]
    public void ExactReleasePlayerRequiresCompleteLeafCallerAndUnchangedFloatLayout()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_XMM_REF_MUTATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_XMM_REF_MUTATION_FIXTURE_INPUT to the neutral exact Release player-input directory.");
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
            var assembly = app.GetAssemblyByName("XmmRefMutationFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "FloatAcrossCall");
            var leaf = owner.Methods.Single(method => method.Name == "Identity");
            var caller = owner.Methods.Single(method => method.Name == "Sum");
            leaf.EnsureRawBytes();
            caller.EnsureRawBytes();
            var leafBody = X86Utils.Iterate(leaf).ToArray();
            var callerBody = X86Utils.Iterate(caller).ToArray();
            var leafProof = X64ScalarFloatRefMutationProof.FindLeaf(leaf);
            var callerProof = X64ScalarFloatRefMutationProof.FindCaller(caller);

            Assert.Multiple(() =>
            {
                Assert.That(leafBody, Has.Length.EqualTo(5));
                Assert.That(callerBody, Has.Length.EqualTo(19));
                Assert.That(X64ScalarFloatRefMutationProof.LeafShape(leafBody), Is.True);
                Assert.That(X64ScalarFloatRefMutationProof.CallerShape(callerBody), Is.True);
                Assert.That(leafProof, Is.Not.Null);
                Assert.That(callerProof, Is.Not.Null);
                Assert.That(callerProof?.Target, Is.SameAs(leaf));
                Assert.That(callerProof?.Fields.Select(field => field.Offset), Is.EqualTo(new[] { 0, 4, 8 }));
                Assert.That(X86CallerExceptionRegionProof.Check(leaf, leafBody,
                    new HashSet<ulong>()), Is.Null);
                Assert.That(X86CallerExceptionRegionProof.Check(caller, callerBody,
                    new HashSet<ulong>()), Is.Null);
                Assert.That(X86NonvolatileXmmStackProof.Find(caller, callerBody), Is.Empty,
                    "The general ABI gate must still reject an unproved byref call.");
            });

            var unwind = X64UnwindProof.ForApplication(app)!;
            Assert.That(X86NonvolatileXmmStackProof.Prove(callerBody, caller.UnderlyingPointer,
                unwind, instruction => instruction.IP == callerBody[10].IP &&
                    instruction.NearBranchTarget == leaf.UnderlyingPointer),
                Is.EquivalentTo(new[]
                {
                    callerBody[1].IP, callerBody[5].IP, callerBody[8].IP,
                    callerBody[12].IP, callerBody[14].IP, callerBody[16].IP,
                }), "Only the separately proved callee permits these exact unwind-backed ABI pairs.");

            var wrongLane = leafBody.ToArray();
            wrongLane[1].Op1Register = Register.XMM2;
            Assert.That(X64ScalarFloatRefMutationProof.LeafShape(wrongLane), Is.False,
                "The leaf must return the exact incoming float lane.");
            var wrongStore = leafBody.ToArray();
            wrongStore[3].MemoryDisplacement64 = 4;
            Assert.That(X64ScalarFloatRefMutationProof.LeafShape(wrongStore), Is.False,
                "The two native zero stores must cover exactly all twelve bytes.");

            var wrongCallLane = callerBody.ToArray();
            wrongCallLane[4].Op0Register = Register.XMM2;
            Assert.That(X64ScalarFloatRefMutationProof.CallerShape(wrongCallLane), Is.False,
                "A different SIMD argument lane cannot be treated as the float argument.");
            var wrongAddOrder = callerBody.ToArray();
            wrongAddOrder[13].Op1Register = Register.XMM8;
            Assert.That(X64ScalarFloatRefMutationProof.CallerShape(wrongAddOrder), Is.False,
                "The native scalar-add order is part of the proof.");
            var wrongSlot = callerBody.ToArray();
            wrongSlot[5].MemoryDisplacement64 = 0x40;
            Assert.That(X64ScalarFloatRefMutationProof.CallerShape(wrongSlot), Is.False,
                "Each ABI save must use its own unwind-authenticated slot.");

            var field = callerProof!.Fields[1];
            var originalAttributes = field.Attributes;
            try
            {
                field.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ScalarFloatRefMutationProof.FindLeaf(leaf), Is.Null);
                Assert.That(X64ScalarFloatRefMutationProof.FindCaller(caller), Is.Null);
            }
            finally { field.Attributes = originalAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
