using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class OrderedCallResultTailNullGuardProofTests
{
    [Test]
    public void ExactPlayerPreservesCallOrderAndRejectsChangedNativeOrManagedBindings()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ORDERED_CALL_TAIL_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ORDERED_CALL_TAIL_GUARD_FIXTURE_INPUT to the neutral exact player input.");

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
            var assembly = app.GetAssemblyByName("OrderedCallTailGuardFixture")!;
            var baseType = assembly.Types.Single(type => type.Name == "GuardBase");
            var owner = assembly.Types.Single(type => type.Name == "GuardOwner");
            var node = assembly.Types.Single(type => type.Name == "GuardNode");
            var method = owner.Methods.Single(candidate => candidate.Name == "Forward");
            var effect = baseType.Methods.Single(candidate => candidate.Name == "Mark");
            var producer = owner.Methods.Single(candidate => candidate.Name == "GetNode");
            var target = node.Methods.Single(candidate => candidate.Name == "Apply");

            var body = OrderedCallResultTailNullGuardProof.ReadBody(method);
            Assert.That(body, Is.Not.Null,
                "The complete, file-backed handler-free unwind region is required.");
            var shape = OrderedCallResultTailNullGuardProof.TryProveShape(body!);
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(method.RawBytes.Length, Is.EqualTo(54));
                Assert.That(shape!.Value.EffectTarget, Is.EqualTo(effect.UnderlyingPointer));
                Assert.That(shape.Value.ProducerTarget, Is.EqualTo(producer.UnderlyingPointer));
                Assert.That(shape.Value.TailTarget, Is.EqualTo(target.UnderlyingPointer));
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                    shape.Value.NullHelper), Is.Not.Null);
                Assert.That(app.MethodsByAddress[method.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(app.MethodsByAddress[effect.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(app.MethodsByAddress[producer.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(app.MethodsByAddress[target.UnderlyingPointer],
                    Has.Count.EqualTo(1));
            });

            RejectNativeMutation(body!, changed =>
                changed[9].NearBranch64 = body[15].IP,
                "The null edge must reach the authenticated helper.");
            RejectNativeMutation(body, changed =>
                changed[15].Code = Code.Call_rel32_64,
                "The target transfer must be terminal.");
            RejectNativeMutation(body, changed =>
                changed[11].Immediate8 = 0,
                "The Boolean value is the native literal one.");
            RejectNativeMutation(body, changed =>
                changed[12].Op1Register = NativeRegister.RBX,
                "The terminal receiver must be the getter result.");
            RejectNativeMutation(body, changed =>
                changed[3].Op1Register = NativeRegister.RDX,
                "The first call must receive the original instance.");
            RejectNativeMutation(body, changed =>
                changed[5].Op0Register = NativeRegister.R8D,
                "The getter MethodInfo slot must be cleared.");
            Assert.That(OrderedCallResultTailNullGuardProof.TryProveShape(
                body.Skip(1).ToArray()), Is.Null);
            Assert.That(OrderedCallResultTailNullGuardProof.TryProveShape(
                body.Append(body[^1]).ToArray()), Is.Null);

            method.Analyze();
            var calls = method.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.IsCall).ToArray();
            Assert.That(calls, Has.Length.EqualTo(3));
            Assert.That(calls.Select(instruction => instruction.NativeAddress),
                Is.EqualTo(new ulong?[] { shape!.Value.EffectCallsite,
                    shape.Value.ProducerCallsite, shape.Value.TailCallsite }));
            var origin = calls[1];
            var guarded = calls[2];
            var result = (LocalVariable)origin.Destination!;
            Assert.That(guarded.CallSemantics,
                Is.EqualTo(CallSemantics.NullCheckedInstance));
            bool Bound() => CallResultNullGuardProof.HasBoundTarget(method,
                result, origin, guarded, target);
            Assert.That(Bound(), Is.True);

            var originalArgument = guarded.Operands[2];
            guarded.SetOperand(2, new Immediate(0));
            try { Assert.That(Bound(), Is.False); }
            finally { guarded.SetOperand(2, originalArgument); }

            var effectAddress = calls[0].NativeAddress;
            calls[0].NativeAddress = shape.Value.ProducerCallsite;
            try { Assert.That(Bound(), Is.False); }
            finally { calls[0].NativeAddress = effectAddress; }

            RejectAlias(app.MethodsByAddress[method.UnderlyingPointer], method,
                Bound);
            RejectAlias(app.MethodsByAddress[effect.UnderlyingPointer], effect,
                Bound);
            RejectAlias(app.MethodsByAddress[producer.UnderlyingPointer], producer,
                Bound);
            RejectAlias(app.MethodsByAddress[target.UnderlyingPointer], target,
                Bound);
            Assert.That(Bound(), Is.True);
            Assert.That(RuntimeNullGuardCoalescer.Run(method), Is.Zero);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectNativeMutation(NativeInstruction[] body,
        Action<NativeInstruction[]> mutate, string reason)
    {
        var changed = body.ToArray();
        mutate(changed);
        Assert.That(OrderedCallResultTailNullGuardProof.TryProveShape(changed),
            Is.Null, reason);
    }

    private static void RejectAlias(
        System.Collections.Generic.List<Cpp2IL.Core.Model.Contexts.MethodAnalysisContext> bindings,
        Cpp2IL.Core.Model.Contexts.MethodAnalysisContext method,
        Func<bool> bound)
    {
        bindings.Add(method);
        try { Assert.That(bound(), Is.False); }
        finally { bindings.RemoveAt(bindings.Count - 1); }
    }
}
