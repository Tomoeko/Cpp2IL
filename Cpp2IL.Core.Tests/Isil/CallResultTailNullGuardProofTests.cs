using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class CallResultTailNullGuardProofTests
{
    [Test]
    public void ExactPlayerBindsCompleteTailAndRejectsChangedAbiOrIdentity()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_RESULT_TAIL_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_TAIL_GUARD_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("CallResultTailGuardFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "TailOwner");
            var node = assembly.Types.Single(type => type.Name == "TailNode");
            var method = owner.Methods.Single(candidate => candidate.Name == "Forward");
            var producer = owner.Methods.Single(candidate => candidate.Name == "GetNode");
            var target = node.Methods.Single(candidate => candidate.Name == "Apply");

            method.EnsureRawBytes();
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            var body = X64Stack28BodyProof.Read(method, 11, 41);
            Assert.That(body, Is.Not.Null,
                $"The full file-backed handler-free frame must be proved: {region}, " +
                $"raw={method.RawBytes.Length}, native={X86Utils.Iterate(method).Count()}, " +
                $"unwind={unwind.MatchesUnwind(region.Start, region.End, 4, 0, [4, 0x42])}.");
            Assert.That(method.RawBytes.Length, Is.EqualTo(40));
            var shape = CallResultTailNullGuardProof.TryProveShape(body!);
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(shape!.Value.ProducerTarget, Is.EqualTo(producer.UnderlyingPointer));
                Assert.That(shape.Value.GuardedTarget, Is.EqualTo(target.UnderlyingPointer));
                Assert.That(shape.Value.Argument, Is.EqualTo(7));
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, shape.Value.NullHelper),
                    Is.Not.Null);
            });

            method.Analyze();
            var calls = method.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.IsCall).ToArray();
            var origin = calls.Single(instruction =>
                ReferenceEquals(instruction.Operands[0], producer));
            var guarded = calls.Single(instruction =>
                ReferenceEquals(instruction.Operands[0], target));
            var result = (LocalVariable)origin.Destination!;
            Assert.That(CallResultNullGuardProof.HasBoundTarget(method, result,
                origin, guarded, target), Is.True);

            var argument = (LocalVariable)guarded.Operands[2];
            var definition = method.ControlFlowGraph.Instructions.Single(instruction =>
                ReferenceEquals(instruction.Destination, argument));
            var originalLiteral = definition.Operands[2];
            definition.SetOperand(2, new Immediate(8));
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                    result, origin, guarded, target), Is.False,
                    "The lifted argument must equal the native LEA literal.");
            }
            finally { definition.SetOperand(2, originalLiteral); }
            definition.OpCode = OpCode.Divide;
            definition.SetOperand(2, new Immediate(0));
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                    result, origin, guarded, target), Is.False,
                    "A potentially throwing argument computation is not the native LEA.");
            }
            finally
            {
                definition.OpCode = OpCode.Add;
                definition.SetOperand(2, originalLiteral);
            }
            var callBlock = method.ControlFlowGraph.FindBlockByInstruction(guarded)!;
            var extra = new LocalVariable("extraArgumentUse",
                new Cpp2IL.Core.ISIL.Register(997, "extra", 1),
                app.SystemTypes.SystemInt32Type);
            var extraUse = new Cpp2IL.Core.ISIL.Instruction(-1, OpCode.Move, extra, argument);
            var callIndex = callBlock.Instructions.IndexOf(guarded);
            callBlock.Instructions.Insert(callIndex, extraUse);
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                    result, origin, guarded, target), Is.False,
                    "The one native argument definition cannot feed another managed operation.");
            }
            finally { callBlock.Instructions.Remove(extraUse); }

            var changedBranch = body!.ToArray();
            changedBranch[4].NearBranch64 = changedBranch[9].IP;
            Assert.That(CallResultTailNullGuardProof.TryProveShape(changedBranch), Is.Null,
                "The null edge must reach only the authenticated helper call.");
            var changedTail = body.ToArray();
            changedTail[9].Code = Code.Call_rel32_64;
            Assert.That(CallResultTailNullGuardProof.TryProveShape(changedTail), Is.Null,
                "A returning call is not the proved terminal transfer.");
            var changedReceiver = body.ToArray();
            changedReceiver[6].Op0Register = NativeRegister.RDX;
            Assert.That(CallResultTailNullGuardProof.TryProveShape(changedReceiver), Is.Null,
                "The call-result receiver must enter RCX unchanged.");
            var changedMetadata = body.ToArray();
            changedMetadata[5].Op0Register = NativeRegister.EDX;
            Assert.That(CallResultTailNullGuardProof.TryProveShape(changedMetadata), Is.Null,
                "The hidden MethodInfo argument must be zeroed in R8D.");
            var changedLiteral = body.ToArray();
            changedLiteral[7].MemoryBase = NativeRegister.RCX;
            Assert.That(CallResultTailNullGuardProof.TryProveShape(changedLiteral), Is.Null,
                "The literal must derive from the cleared R8D register.");
            Assert.That(CallResultTailNullGuardProof.TryProveShape(body.Skip(1).ToArray()),
                Is.Null, "A shifted native entry is outside the closed caller.");
            Assert.That(CallResultTailNullGuardProof.TryProveShape(body.Append(body[^1]).ToArray()),
                Is.Null, "An extra native suffix is outside the closed caller.");

            var address = guarded.NativeAddress;
            guarded.NativeAddress = shape!.Value.NullCallsite;
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                    result, origin, guarded, target), Is.False,
                    "The lifted target must coincide with the native terminal jump.");
            }
            finally { guarded.NativeAddress = address; }

            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            bindings.Add(target);
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                    result, origin, guarded, target), Is.False,
                    "Duplicate applicable managed identities are ambiguous.");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }
            Assert.That(CallResultNullGuardProof.HasBoundTarget(method,
                result, origin, guarded, target), Is.True);
            Assert.That(guarded.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance),
                "The exact player analysis must preserve the producer call and coalesce the guard.");
            Assert.That(RuntimeNullGuardCoalescer.Run(method), Is.Zero,
                "A second pass must not change the already recovered graph.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
