using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player gate for two ordered Int32 field-array accesses.</summary>
[NonParallelizable]
public class X64SequentialInt32FieldArrayFixtureTests
{
    [Test]
    public void ExactBodyPreservesBothArrayAccessesAndInterveningEffects()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_SEQUENCE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_SEQUENCE_FIXTURE_INPUT to the fixture's player-input directory.");
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
            var method = app.GetAssemblyByName("ArraySequenceFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "ReadThenWrite");
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X64SequentialInt32FieldArrayProof.TryProveShape(native);
            Assert.That(shape, Is.Not.Null);
            var evidence = X64SequentialInt32FieldArrayProof.Find(method, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Add,
                    IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Return }));

            var wrongNullBranch = native.ToArray();
            wrongNullBranch[3].NearBranch64 = native[18].IP;
            Assert.That(X64SequentialInt32FieldArrayProof.TryProveShape(wrongNullBranch), Is.Null);
            var wrongFirstBranch = native.ToArray();
            wrongFirstBranch[5].NearBranch64 = native[16].IP;
            Assert.That(X64SequentialInt32FieldArrayProof.TryProveShape(wrongFirstBranch), Is.Null);
            var wrongSecondBranch = native.ToArray();
            wrongSecondBranch[11].NearBranch64 = native[16].IP;
            Assert.That(X64SequentialInt32FieldArrayProof.TryProveShape(wrongSecondBranch), Is.Null);
            var wrongIndexWidth = native.ToArray();
            wrongIndexWidth[12].Code = Code.Mov_r64_rm64;
            Assert.That(X64SequentialInt32FieldArrayProof.TryProveShape(wrongIndexWidth), Is.Null);
            var wrongArrayField = native.ToArray();
            wrongArrayField[1].MemoryDisplacement64 += 8;
            Assert.That(X64SequentialInt32FieldArrayProof.TryLift(method, wrongArrayField), Is.Null);
            var wrongCounterField = native.ToArray();
            wrongCounterField[8].MemoryDisplacement64 += 4;
            Assert.That(X64SequentialInt32FieldArrayProof.TryLift(method, wrongCounterField), Is.Null);
            var wrongObservedField = native.ToArray();
            wrongObservedField[9].MemoryDisplacement64 += 4;
            Assert.That(X64SequentialInt32FieldArrayProof.TryLift(method, wrongObservedField), Is.Null);
            var wrongBoundsHelper = native.ToArray();
            wrongBoundsHelper[18].NearBranch64 = native[16].NearBranchTarget;
            Assert.That(X64SequentialInt32FieldArrayProof.TryLift(method, wrongBoundsHelper), Is.Null);
            var extraInstruction = native.Append(native[^1]).ToArray();
            Assert.That(X64SequentialInt32FieldArrayProof.TryProveShape(extraInstruction), Is.Null);

            method.Analyze();
            var recovered = method.ControlFlowGraph!.Instructions.ToArray();
            var accesses = recovered.Select((instruction, position) => (instruction, position))
                .Where(item => item.instruction.Operands.Any(operand =>
                    operand is Cpp2IL.Core.ISIL.ArrayAccess)).ToArray();
            Assert.That(accesses, Has.Length.EqualTo(2));
            Assert.That(accesses[0].instruction.OpCode, Is.EqualTo(IsilOpCode.Move));
            Assert.That(accesses[0].instruction.Operands[1],
                Is.InstanceOf<Cpp2IL.Core.ISIL.ArrayAccess>());
            Assert.That(accesses[1].instruction.OpCode, Is.EqualTo(IsilOpCode.Move));
            Assert.That(accesses[1].instruction.Operands[0],
                Is.InstanceOf<Cpp2IL.Core.ISIL.ArrayAccess>());
            Assert.That(accesses[0].position, Is.LessThan(accesses[1].position));
            var between = recovered[(accesses[0].position + 1)..accesses[1].position];
            Assert.That(between.Any(instruction => instruction.Operands.Any(operand =>
                operand is Cpp2IL.Core.ISIL.FieldReference field &&
                ReferenceEquals(field.Field, evidence!.CounterField))), Is.True);
            Assert.That(between.Any(instruction => instruction.Operands.Any(operand =>
                operand is Cpp2IL.Core.ISIL.FieldReference field &&
                ReferenceEquals(field.Field, evidence!.ObservedField))), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
