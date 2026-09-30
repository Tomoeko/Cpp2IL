using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using ManagedOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact scalar-selector provenance and complete lane-closure regressions.</summary>
[NonParallelizable]
public class X64ScalarFloatSelectionFixtureTests
{
    [TestCase("Minimum32", Mnemonic.Minss)]
    [TestCase("Maximum32", Mnemonic.Maxss)]
    [TestCase("ReadPositive32", Mnemonic.Maxss)]
    [TestCase("StorePositive32", Mnemonic.Maxss)]
    public void ExactNativeSelectorRequiresOriginalCallerBytesAndInstruction(string name, Mnemonic expected)
    {
        WithFixture(app =>
        {
            var method = Method(app, name);
            var body = X86Utils.Iterate(method).ToArray();
            var sites = body.Where(X64ScalarFloatSelectionProof.IsSelection).ToArray();
            Assert.That(sites, Has.Length.EqualTo(1),
                "This control must contain a real register scalar selection rather than a comparison branch.");
            var site = sites[0];
            Assert.That(site.Mnemonic, Is.EqualTo(expected));
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, site), Is.True);
            Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(body, site.IP), Is.True);

            var cache = method.RawBytes;
            try
            {
                var changed = cache.ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                Assert.That(X64ScalarFloatSelectionProof.CanLift(method, site), Is.False,
                    "A cached prefix mismatch cannot be replaced by rereading the original bytes.");
            }
            finally { method.RawBytes = cache; }
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, site), Is.True);

            var changedSite = site;
            changedSite.Code = expected == Mnemonic.Minss ? Code.Maxss_xmm_xmmm32 : Code.Minss_xmm_xmmm32;
            Assert.That(X64ScalarFloatSelectionProof.IsSelection(changedSite), Is.True);
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, changedSite), Is.False,
                "Another valid selector opcode is not the original native instruction.");
            changedSite = site;
            changedSite.Op1Register = Register.XMM2;
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, changedSite), Is.False);
            changedSite = site;
            changedSite.IP++;
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, changedSite), Is.False);
            var foreignMethod = Method(app, name == "Minimum32" ? "Maximum32" : "Minimum32");
            var foreignSite = X86Utils.Iterate(foreignMethod).Single(X64ScalarFloatSelectionProof.IsSelection);
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, foreignSite), Is.False,
                "A real selector in a neighboring native method cannot authenticate this caller.");

            method.Analyze();
            var lifted = method.ControlFlowGraph!.Instructions.Where(instruction =>
                instruction.OpCode == ManagedOpCode.FloatSelect).ToArray();
            Assert.That(lifted, Has.Length.EqualTo(1));
            Assert.That(lifted[0].NativeAddress, Is.EqualTo(site.IP));
        });
    }

    [Test]
    public void CompleteExactScalarBodyRejectsUpperLaneWidthAndControlStateObservers()
    {
        WithFixture(app =>
        {
            var method = Method(app, "Maximum32");
            var body = X86Utils.Iterate(method).ToArray();
            var selection = body.Single(X64ScalarFloatSelectionProof.IsSelection);
            Assert.That(X64ScalarFloatSelectionProof.CanLift(method, selection), Is.True);
            Assert.That(body[^1].Code, Is.EqualTo(Code.Retnq));
            foreach (var hex in new[]
            {
                "0F1100",       // A packed memory store exposes preserved upper lanes.
                "0F58C1",       // Packed arithmetic consumes those lanes.
                "F20F1100",     // A Binary64 store exposes bits32..63 of a Binary32 selection.
                "F20F58C1",     // Mixed-width scalar arithmetic consumes those preserved bits.
                "0F28F0",       // XMM6 requires an independently authenticated nonvolatile spill ABI.
                "0FAE10",       // LDMXCSR changes observable floating control state.
                "0FAE08",       // FXRSTOR can restore floating state without naming a vector register.
                "E800000000",   // Returning calls have an unproved vector-state and upper-lane ABI.
            })
            {
                var extended = body.Take(body.Length - 1).ToList();
                var added = Decode(hex, body[^1].IP);
                extended.AddRange(added);
                var terminal = body[^1];
                terminal.IP = added[^1].NextIP;
                extended.Add(terminal);
                Assert.That(X64ScalarFloatSelectionProof.IsScalarBody(extended, selection.IP), Is.False, hex);
            }
        });
    }

    private static Instruction[] Decode(string hex, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), address);
        return decoder.ToArray();
    }

    private static MethodAnalysisContext Method(ApplicationAnalysisContext app, string name) =>
        app.GetAssemblyByName("ScalarFloatSelectionFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);

    private static void WithFixture(Action<ApplicationAnalysisContext> action)
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FLOAT_SELECTION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_FLOAT_SELECTION_FIXTURE_INPUT to the neutral selector player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            action(Cpp2IlApi.CurrentAppContext!);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
