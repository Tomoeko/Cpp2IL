using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64SequentialNullFieldProofTests
{
    [Test]
    public void ExactPlayerRejectsNearbyNativeBodies()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SEQUENTIAL_NULL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SEQUENTIAL_NULL_FIXTURE_INPUT to the neutral exact player input.");
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
            var method = app.GetAssemblyByName("SequentialNullGuardFixture")!.Types
                .Single(type => type.Name == "GuardedComparisons").Methods
                .Single(candidate => candidate.Name == "Compare");
            method.Analyze();
            var probe = method.NullArmFieldProbes.Single();
            Assert.That(probe.Helper, Is.Not.Null);
            Assert.That(X64SequentialNullFieldProof.Matches(method, probe.Access,
                probe.EarlierRead.Access, probe.Helper!), Is.True,
                "The unmodified exact player must bind the later field loads and runtime helper.");

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var entry = method.UnderlyingPointer;
            var end = entry + (ulong)method.RawBytes.Length;
            var firstOffset = probe.Access.Offset;
            var secondOffset = probe.EarlierRead.Access.Offset;
            var helperTarget = probe.Helper!.NativeTarget;
            bool Matches(Instruction[] body) => X64SequentialNullFieldProof.MatchesShape(body,
                entry, end, firstOffset, secondOffset, helperTarget);
            Assert.That(Matches(native), Is.True);

            foreach (var mutation in new[]
                     {
                         "first-polarity", "second-target", "first-load-width", "second-load-register",
                         "first-load-offset", "prefix", "code-size", "stack-frame", "return-epilog",
                         "helper-target", "extra-entry",
                     })
            {
                var changed = native.ToArray();
                var index = mutation switch
                {
                    "first-polarity" => 2,
                    "second-target" => 4,
                    "first-load-width" or "first-load-offset" => 6,
                    "second-load-register" or "prefix" or "code-size" => 5,
                    "stack-frame" => 0,
                    "return-epilog" => 15,
                    "helper-target" => 17,
                    _ => 1,
                };
                var instruction = changed[index];
                switch (mutation)
                {
                    case "first-polarity": instruction.Code = Code.Jne_rel8_64; break;
                    case "second-target": instruction.NearBranch64 = changed[14].IP; break;
                    case "first-load-width": instruction.Code = Code.Mov_r64_rm64; break;
                    case "second-load-register": instruction.Op0Register = Register.EAX; break;
                    case "first-load-offset": instruction.MemoryDisplacement64++; break;
                    case "prefix": instruction.HasLockPrefix = true; break;
                    case "code-size": instruction.CodeSize = CodeSize.Code32; break;
                    case "stack-frame": instruction.Immediate8to64 = 0x20; break;
                    case "return-epilog": instruction.Immediate8to64 = 0x20; break;
                    case "helper-target": instruction.NearBranch64 = changed[14].IP; break;
                    case "extra-entry": instruction.IP++; break;
                }
                changed[index] = instruction;
                Assert.That(Matches(changed), Is.False, mutation);
            }

            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(entry, end);
            Assert.That(unwind.MatchesUnwind(entry, region.End, 4, 0, new byte[] { 4, 0x42 }), Is.True);
            Assert.That(unwind.MatchesUnwind(entry, region.End, 4, 0, new byte[] { 4, 0x32 }), Is.False,
                "A different stack allocation operation cannot authenticate the body.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
