using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64BooleanParameterTailBranchProofTests
{
    [Test]
    public void ExactPlayerBindsBothTailTargets()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_PARAMETER_BRANCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_PARAMETER_BRANCH_FIXTURE_INPUT to the neutral exact player input.");
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
            var owner = app.GetAssemblyByName("BooleanParameterBranchFixture")!.Types
                .Single(type => type.Name == "BranchMethods");
            var method = owner.Methods.Single(candidate => candidate.Name == "Select");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(native.Length, Is.GreaterThanOrEqualTo(5),
                "The extracted raw span must contain the bounded reachable native body.");
            var shape = X64BooleanParameterTailBranchProof.TryProveShape(native);
            Assert.That(shape, Is.Not.Null, "The authored player must expose the exact branch shape.");
            var proof = X64BooleanParameterTailBranchProof.Find(method, native);
            Assert.That(proof, Is.Not.Null, "The full player-only binding must be authenticated.");
            Assert.Multiple(() =>
            {
                Assert.That(proof!.BranchTarget.Name, Is.EqualTo("LowPath"));
                Assert.That(proof.FallthroughTarget.Name, Is.EqualTo("HighPath"));
                Assert.That(proof.BranchOnZero, Is.True);
            });
            var lifted = X64BooleanParameterTailBranchProof.TryLift(method, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.That(lifted!.Select(instruction => instruction.OpCode), Is.EqualTo(new[]
            {
                ISIL.OpCode.CheckEqual, ISIL.OpCode.ConditionalJump,
                ISIL.OpCode.Call, ISIL.OpCode.Return,
                ISIL.OpCode.Call, ISIL.OpCode.Return,
            }));
            Assert.Multiple(() =>
            {
                Assert.That(lifted[0].IntegerBitWidth, Is.Zero,
                    "The managed Boolean parameter must be loaded by its declared type.");
                Assert.That(lifted[2].Operands[2], Is.EqualTo(new ISIL.Immediate(0)));
                Assert.That(lifted[4].Operands[2], Is.EqualTo(new ISIL.Immediate(0)));
            });

            var clearIndex = native[0].Mnemonic == Mnemonic.Nop ? 1 : 0;
            var testIndex = clearIndex + 1;
            var branchIndex = testIndex + 1;
            var tailIndex = branchIndex + 1;
            var wrongClear = native.ToArray();
            wrongClear[clearIndex].Op0Register = Register.EAX;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, wrongClear), Is.Null,
                "The hidden MethodInfo argument must be cleared in ECX.");
            var wrongTest = native.ToArray();
            wrongTest[testIndex].Op0Register = Register.CL;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, wrongTest), Is.Null,
                "The branch must test the second argument in DL.");
            var interveningState = native.ToArray();
            interveningState[branchIndex].Code = Code.Xor_r32_rm32;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, interveningState), Is.Null,
                "No state-changing instruction may intervene before the branch.");
            var redirectedBranch = native.ToArray();
            redirectedBranch[branchIndex].NearBranch64 = native[tailIndex].NearBranchTarget;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, redirectedBranch), Is.Null,
                "Both exits need distinct target identities.");
            var interiorBranch = native.ToArray();
            interiorBranch[branchIndex].NearBranch64 = native[branchIndex].IP;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, interiorBranch), Is.Null,
                "The conditional exit cannot loop into the proved body.");
            var unexpectedCall = native.ToArray();
            unexpectedCall[tailIndex].Code = Code.Call_rel32_64;
            Assert.That(X64BooleanParameterTailBranchProof.Find(method, unexpectedCall), Is.Null,
                "A returning transfer would invalidate the closed body.");

            var targetBindings = app.MethodsByAddress[shape!.BranchTarget];
            try
            {
                targetBindings.Add(proof!.BranchTarget);
                Assert.That(X64BooleanParameterTailBranchProof.Find(method, native), Is.Null,
                    "A native target shared by multiple managed methods is ambiguous.");
            }
            finally { targetBindings.RemoveAt(targetBindings.Count - 1); }
            try
            {
                app.MethodsByAddress.Add(shape.End, [method]);
                Assert.That(X64BooleanParameterTailBranchProof.Find(method, native), Is.Null,
                    "A registered entry in the padding would invalidate the body boundary.");
            }
            finally { app.MethodsByAddress.Remove(shape.End); }

            var booleanParameter = method.Parameters[1];
            try
            {
                booleanParameter.OverrideParameterType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64BooleanParameterTailBranchProof.Find(method, native), Is.Null,
                    "A changed managed Boolean type cannot inherit the native proof.");
            }
            finally { booleanParameter.OverrideParameterType = null; }
            method.Parameters.Reverse();
            try
            {
                Assert.That(X64BooleanParameterTailBranchProof.Find(method, native), Is.Null,
                    "DL cannot prove a Boolean argument in another position.");
            }
            finally { method.Parameters.Reverse(); }

            var definition = owner.Definition!;
            var originalBitfield = definition.Bitfield;
            try
            {
                definition.Bitfield |= 1u << 3;
                Assert.That(X64BooleanParameterTailBranchProof.Find(method, native), Is.Null,
                    "A class initializer changes the semantics of managed helper calls.");
            }
            finally { definition.Bitfield = originalBitfield; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
