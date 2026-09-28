using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class DualCallResultTailNullGuardProofTests
{
    [Test]
    public void ExactPlayerBindsBothChecksAndRejectsChangedOrderOrExit()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_DUAL_RESULT_TAIL_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_DUAL_RESULT_TAIL_GUARD_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("DualResultTailGuardFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "GuardOwner");
            var firstNode = assembly.Types.Single(type =>
                type.Name == "FirstNode");
            var secondNode = assembly.Types.Single(type =>
                type.Name == "SecondNode");
            var firstProducer = owner.Methods.Single(method =>
                method.Name == "GetFirst");
            var secondProducer = owner.Methods.Single(method =>
                method.Name == "GetSecond");
            var firstSink = firstNode.Methods.Single(method =>
                method.Name == "Apply");
            var secondSink = secondNode.Methods.Single(method =>
                method.Name == "Finish");
            var callers = assembly.Types.SelectMany(type => type.Methods)
                .Where(method => method.Name == "Forward").ToArray();
            Assert.That(callers, Has.Length.EqualTo(3));
            Assert.That(callers.Select(method => method.UnderlyingPointer)
                .Distinct().Count(), Is.EqualTo(1));
            Assert.That(app.MethodsByAddress[callers[0].UnderlyingPointer],
                Has.Count.EqualTo(3));

            foreach (var caller in callers)
            {
                var body = DualCallResultTailNullGuardProof.ReadBody(caller);
                Assert.That(body, Is.Not.Null,
                    "The entire 69-byte body and one-byte trap must be file-backed and unwind-closed.");
                var shape = DualCallResultTailNullGuardProof.TryProveShape(body!);
                Assert.That(shape, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(shape!.Value.FirstProducerTarget,
                        Is.EqualTo(firstProducer.UnderlyingPointer));
                    Assert.That(shape.Value.FirstSinkTarget,
                        Is.EqualTo(firstSink.UnderlyingPointer));
                    Assert.That(shape.Value.SecondProducerTarget,
                        Is.EqualTo(secondProducer.UnderlyingPointer));
                    Assert.That(shape.Value.TailTarget,
                        Is.EqualTo(secondSink.UnderlyingPointer));
                    Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                        shape.Value.NullHelper), Is.Not.Null);
                });

                caller.Analyze();
                var calls = caller.ControlFlowGraph!.Instructions
                    .Where(instruction => instruction.IsCall).ToArray();
                Assert.That(calls, Has.Length.EqualTo(4));
                Assert.That(calls.Select(instruction => instruction.NativeAddress),
                    Is.EqualTo(new ulong?[] { shape!.Value.FirstProducerCallsite,
                        shape.Value.FirstSinkCallsite,
                        shape.Value.SecondProducerCallsite,
                        shape.Value.TailCallsite }));
                Assert.That(calls[1].CallSemantics,
                    Is.EqualTo(CallSemantics.NullCheckedInstance));
                Assert.That(calls[3].CallSemantics,
                    Is.EqualTo(CallSemantics.NullCheckedInstance));
                var result = (LocalVariable)calls[2].Destination!;
                bool Bound() => CallResultNullGuardProof.HasBoundTarget(caller,
                    result, calls[2], calls[3], secondSink);
                Assert.That(Bound(), Is.True,
                    "Each folded caller must bind all four native calls separately.");
                Assert.That(RuntimeNullGuardCoalescer.Run(caller), Is.Zero);

                var firstArgument = calls[1].Operands[2];
                calls[1].SetOperand(2, new Immediate(1));
                try
                {
                    Assert.That(Bound(), Is.False,
                        "The first call's Boolean value is native false.");
                }
                finally { calls[1].SetOperand(2, firstArgument); }

                var finalAddress = calls[3].NativeAddress;
                calls[3].NativeAddress = shape.Value.NullCallsite;
                try
                {
                    Assert.That(Bound(), Is.False,
                        "The final managed call must bind the terminal jump.");
                }
                finally { calls[3].NativeAddress = finalAddress; }

                RejectNativeMutation(body!, changed =>
                    changed[6].NearBranch64 = body[20].IP,
                    "The first null arm must reach the shared helper.");
                RejectNativeMutation(body, changed =>
                    changed[15].NearBranch64 = body[20].IP,
                    "The second null arm must reach the shared helper.");
                RejectNativeMutation(body, changed =>
                    changed[7].Op0Register = NativeRegister.EDX,
                    "The first sink MethodInfo argument must be zeroed in R8D.");
                RejectNativeMutation(body, changed =>
                    changed[8].Op0Register = NativeRegister.R8D,
                    "The first sink Boolean argument must be zeroed in EDX.");
                RejectNativeMutation(body, changed =>
                    changed[12].Op1Register = NativeRegister.RAX,
                    "The second getter must receive the saved owner.");
                RejectNativeMutation(body, changed =>
                    changed[17].Op1Register = NativeRegister.RBX,
                    "The final sink must receive the second call result.");
                RejectNativeMutation(body, changed =>
                    changed[20].Code = Code.Call_rel32_64,
                    "The final call must be a terminal transfer.");
                Assert.That(DualCallResultTailNullGuardProof.TryProveShape(
                    body.Skip(1).ToArray()), Is.Null);

                var firstBindings = app.MethodsByAddress[
                    firstSink.UnderlyingPointer];
                firstBindings.Add(firstSink);
                try
                {
                    Assert.That(Bound(), Is.False,
                        "Duplicate applicable first-sink identities are ambiguous.");
                }
                finally { firstBindings.RemoveAt(firstBindings.Count - 1); }
            }

            var callerBindings = app.MethodsByAddress[
                callers[0].UnderlyingPointer];
            callerBindings.Add(callers[0]);
            try
            {
                var caller = callers[0];
                var calls = caller.ControlFlowGraph!.Instructions
                    .Where(instruction => instruction.IsCall).ToArray();
                Assert.That(CallResultNullGuardProof.HasBoundTarget(caller,
                    (LocalVariable)calls[2].Destination!, calls[2], calls[3],
                    secondSink), Is.False,
                    "A duplicate caller binding invalidates the folded group.");
            }
            finally { callerBindings.RemoveAt(callerBindings.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectNativeMutation(Iced.Intel.Instruction[] body,
        Action<Iced.Intel.Instruction[]> mutate, string message)
    {
        var changed = body.ToArray();
        mutate(changed);
        Assert.That(DualCallResultTailNullGuardProof.TryProveShape(changed),
            Is.Null, message);
    }
}
