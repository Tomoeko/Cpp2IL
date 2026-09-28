using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class CallResultFalseTailNullGuardProofTests
{
    [Test]
    public void ExactPlayerBindsEachFoldedCallerAndRejectsChangedBooleanOrExit()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_RESULT_FALSE_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_FALSE_TAIL_FIXTURE_INPUT to the neutral exact player input.");

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
            var assembly = app.GetAssemblyByName("CallResultFalseTailFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "TailOwner");
            var derived = assembly.Types.Single(type =>
                type.Name == "VirtualTailOwner");
            var node = assembly.Types.Single(type => type.Name == "TailNode");
            var producer = owner.Methods.Single(method =>
                method.Name == "GetNode");
            var target = node.Methods.Single(method => method.Name == "Apply");
            var ordinary = owner.Methods.Single(method =>
                method.Name == "ForwardFalse");
            var virtualCaller = derived.Methods.Single(method =>
                method.Name == "VirtualForwardFalse");

            Assert.That(ordinary.UnderlyingPointer,
                Is.EqualTo(virtualCaller.UnderlyingPointer),
                "The exact player must demonstrate a folded caller address.");
            Assert.That(app.MethodsByAddress[ordinary.UnderlyingPointer],
                Has.Count.EqualTo(2));

            foreach (var caller in new[] { ordinary, virtualCaller })
            {
                var body = CallResultFalseTailNullGuardProof.ReadBody(caller);
                Assert.That(body, Is.Not.Null,
                    "The whole 38-byte body and one-byte padding must be file-backed and unwind-closed.");
                var shape = CallResultFalseTailNullGuardProof.TryProveShape(body!);
                Assert.That(shape, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(shape!.Value.ProducerTarget,
                        Is.EqualTo(producer.UnderlyingPointer));
                    Assert.That(shape.Value.GuardedTarget,
                        Is.EqualTo(target.UnderlyingPointer));
                    Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                        shape.Value.NullHelper), Is.Not.Null);
                });

                caller.Analyze();
                var calls = caller.ControlFlowGraph!.Instructions
                    .Where(instruction => instruction.IsCall).ToArray();
                Assert.That(calls, Has.Length.EqualTo(2));
                var origin = calls.Single(instruction =>
                    ReferenceEquals(instruction.Operands[0], producer));
                var guarded = calls.Single(instruction =>
                    ReferenceEquals(instruction.Operands[0], target));
                var result = (LocalVariable)origin.Destination!;
                Assert.That(CallResultNullGuardProof.HasBoundTarget(caller,
                    result, origin, guarded, target), Is.True,
                    "Each folded managed caller must bind independently.");
                Assert.That(guarded.CallSemantics,
                    Is.EqualTo(CallSemantics.NullCheckedInstance));
                Assert.That(RuntimeNullGuardCoalescer.Run(caller), Is.Zero);

                var originalArgument = guarded.Operands[2];
                guarded.SetOperand(2, new Immediate(1));
                try
                {
                    Assert.That(CallResultNullGuardProof.HasBoundTarget(caller,
                        result, origin, guarded, target), Is.False,
                        "True is not the native Boolean-false argument.");
                }
                finally { guarded.SetOperand(2, originalArgument); }

                var originalAddress = guarded.NativeAddress;
                guarded.NativeAddress = shape!.Value.NullCallsite;
                try
                {
                    Assert.That(CallResultNullGuardProof.HasBoundTarget(caller,
                        result, origin, guarded, target), Is.False,
                        "The managed call must bind the terminal jump.");
                }
                finally { guarded.NativeAddress = originalAddress; }

                var changedBranch = body!.ToArray();
                changedBranch[4].NearBranch64 = changedBranch[9].IP;
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    changedBranch), Is.Null);
                var changedBoolean = body.ToArray();
                changedBoolean[6].Op0Register = NativeRegister.R8D;
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    changedBoolean), Is.Null);
                var changedMetadata = body.ToArray();
                changedMetadata[5].Op0Register = NativeRegister.EDX;
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    changedMetadata), Is.Null);
                var changedReceiver = body.ToArray();
                changedReceiver[7].Op0Register = NativeRegister.RDX;
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    changedReceiver), Is.Null);
                var changedExit = body.ToArray();
                changedExit[9].Code = Code.Call_rel32_64;
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    changedExit), Is.Null);
                Assert.That(CallResultFalseTailNullGuardProof.TryProveShape(
                    body.Skip(1).ToArray()), Is.Null);
            }

            var bindings = app.MethodsByAddress[ordinary.UnderlyingPointer];
            bindings.Add(ordinary);
            try
            {
                Assert.That(CallResultFalseTailNullGuardProof.ReadBody(ordinary),
                    Is.Not.Null,
                    "Native closure is independent of duplicate metadata.");
                var calls = ordinary.ControlFlowGraph!.Instructions
                    .Where(instruction => instruction.IsCall).ToArray();
                var origin = calls.Single(instruction =>
                    ReferenceEquals(instruction.Operands[0], producer));
                var guarded = calls.Single(instruction =>
                    ReferenceEquals(instruction.Operands[0], target));
                Assert.That(CallResultNullGuardProof.HasBoundTarget(ordinary,
                    (LocalVariable)origin.Destination!, origin, guarded,
                    target), Is.False,
                    "A duplicate caller identity is not a safe folded alias group.");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
