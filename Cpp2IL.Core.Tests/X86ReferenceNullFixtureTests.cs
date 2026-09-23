using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86ReferenceNullFixtureTests
{
    [Test]
    public void SharedNativeAddressNeedsTheExactBooleanMethodProof()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_NULL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_NULL_FIXTURE_INPUT to the exact synthetic player-input directory.");
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
            var fixture = app.GetAssemblyByName("ReferenceNullFixture");
            Assert.That(fixture, Is.Not.Null);
            var method = fixture!.Types.SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "ArrayHasValue");
            Assert.That(app.MethodsByAddress[method.UnderlyingPointer].Count, Is.GreaterThan(1),
                "The fixture must actually exercise an aliased native body.");
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86ReferenceNullReturnProof.IsApplicable(method, native), Is.True);
            var lifted = app.InstructionSet.GetIsilFromMethod(method);
            Assert.That(lifted.Count(instruction => instruction.OpCode == OpCode.CheckEqual &&
                instruction.IntegerBitWidth == 64 &&
                instruction.Operands is [Register { Name: "ZF" }, Register { Name: "rcx" },
                    Immediate { Value: 0 }]), Is.EqualTo(1));
            Assert.That(lifted.Any(instruction => instruction.OpCode == OpCode.Subtract), Is.False);

            method.OverrideReturnType = app.SystemTypes.SystemInt32Type;
            Assert.That(X86ReferenceNullReturnProof.IsApplicable(method, native), Is.False,
                "An overridden return type cannot borrow the Boolean body proof.");
            method.OverrideReturnType = null;
            Assert.That(X86ReferenceNullReturnProof.IsApplicable(method, native), Is.True);
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
