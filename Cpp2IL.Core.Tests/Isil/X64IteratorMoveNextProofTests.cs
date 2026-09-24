using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorMoveNextProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerPreservesStateWriteBeforeOwnerNullFailure()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var iterator = app.GetAssemblyByName("IteratorFactoryFixture")!.Types.Single(type =>
                type.Name.Contains("<Iterate>d__", StringComparison.Ordinal));
            var moveNext = iterator.Methods.Single(method => method.Name == "MoveNext");
            moveNext.EnsureRawBytes();
            var native = X86Utils.Iterate(moveNext).ToArray();
            Assert.That(native.Length, Is.GreaterThanOrEqualTo(27));
            Assert.That(X64IteratorMoveNextProof.TryProveShape(native.Take(27).ToArray()), Is.True);

            var proof = X64IteratorMoveNextProof.Find(moveNext, native);
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.State.Offset, Is.EqualTo(16));
                Assert.That(proof.Current.Offset, Is.EqualTo(24));
                Assert.That(proof.CapturedOwner.Offset, Is.EqualTo(32));
                Assert.That(proof.YieldedField.Offset, Is.EqualTo(16));
            });
            var lifted = X64IteratorMoveNextProof.TryLift(moveNext, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(moveNext).Select(instruction =>
                    instruction.OpCode),
                Is.EqualTo(lifted!.Select(instruction => instruction.OpCode)),
                "the instruction-set hook must select the authenticated state machine");
            Assert.Multiple(() =>
            {
                Assert.That(lifted!.Count, Is.EqualTo(17));
                Assert.That(lifted[2].Operands[0], Is.SameAs(lifted[10]), "zero-state dispatch");
                Assert.That(lifted[11].Operands[0], Is.SameAs(lifted[14]), "one-state dispatch");
                Assert.That(lifted[4].OpCode, Is.EqualTo(ISIL.OpCode.Move),
                    "state becomes -1 before the potentially faulting owner field read");
                Assert.That(lifted[5].OpCode, Is.EqualTo(ISIL.OpCode.Move));
                Assert.That(lifted[6].OpCode, Is.EqualTo(ISIL.OpCode.Move),
                    "current receives the owner field only after the state write");
                Assert.That(lifted[7].OpCode, Is.EqualTo(ISIL.OpCode.Move));
                Assert.That(lifted.Count(instruction => instruction.OpCode == ISIL.OpCode.Return),
                    Is.EqualTo(3));
            });

            var wrongNullBranch = native.ToArray();
            wrongNullBranch[9].NearBranch64 = native[19].IP;
            Assert.That(X64IteratorMoveNextProof.TryProveShape(wrongNullBranch), Is.False);
            var wrongStateBeforeNull = native.ToArray();
            wrongStateBeforeNull[7].MemoryDisplacement64 += 8;
            Assert.That(X64IteratorMoveNextProof.TryProveShape(wrongStateBeforeNull), Is.False);
            var missingStateBeforeNull = native.ToArray();
            missingStateBeforeNull[7].Code = Code.Nopd;
            Assert.That(X64IteratorMoveNextProof.TryProveShape(missingStateBeforeNull), Is.False);
            var wrongCurrent = native.ToArray();
            wrongCurrent[11].Immediate8 = 0x20;
            Assert.That(X64IteratorMoveNextProof.Find(moveNext, wrongCurrent), Is.Null);
            var wrongBarrier = native.ToArray();
            wrongBarrier[13].NearBranch64 = native[26].NearBranchTarget;
            Assert.That(X64IteratorMoveNextProof.Find(moveNext, wrongBarrier), Is.Null);
            var wrongNullHelper = native.ToArray();
            wrongNullHelper[26].NearBranch64 = native[13].NearBranchTarget;
            Assert.That(X64IteratorMoveNextProof.Find(moveNext, wrongNullHelper), Is.Null);
            var extraEffect = native.ToArray();
            extraEffect[14].Code = Code.Inc_rm32;
            Assert.That(X64IteratorMoveNextProof.TryProveShape(extraEffect), Is.False);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
