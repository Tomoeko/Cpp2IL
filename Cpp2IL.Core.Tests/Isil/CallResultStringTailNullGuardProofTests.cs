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
public class CallResultStringTailNullGuardProofTests
{
    [Test]
    public void ExactPlayerBindsStringTailAndRejectsChangedOrderOrIdentity()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_RESULT_STRING_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_STRING_TAIL_FIXTURE_INPUT to the neutral exact player input.");

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
            var assembly = app.GetAssemblyByName("CallResultStringTailFixture")!;
            var host = assembly.Types.Single(type => type.Name == "Host");
            var node = assembly.Types.Single(type => type.Name == "TextNode");
            var method = host.Methods.Single(candidate => candidate.Name == "ReadText");
            var producer = host.Methods.Single(candidate => candidate.Name == "GetNode");
            var target = node.Methods.Single(candidate => candidate.Name == "Text");

            var body = CallResultStringTailNullGuardProof.ReadBody(method);
            Assert.That(body, Is.Not.Null,
                "The complete, file-backed handler-free saved-stack frame is required.");
            var shape = CallResultStringTailNullGuardProof.TryProveShape(body!);
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(method.RawBytes.Length, Is.EqualTo(35));
                Assert.That(body, Has.Length.EqualTo(10));
                Assert.That(shape!.Value.ProducerTarget,
                    Is.EqualTo(producer.UnderlyingPointer));
                Assert.That(shape.Value.TailTarget,
                    Is.EqualTo(target.UnderlyingPointer));
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                    shape.Value.NullHelper), Is.Not.Null);
                Assert.That(app.MethodsByAddress[producer.UnderlyingPointer].Count,
                    Is.GreaterThan(1),
                    "The exact getter is folded, so an unrelated alias is not ambiguity.");
                Assert.That(app.MethodsByAddress[method.UnderlyingPointer],
                    Has.Count.EqualTo(1));
                Assert.That(app.MethodsByAddress[target.UnderlyingPointer],
                    Has.Count.EqualTo(1));
            });

            RejectNativeMutation(body!, changed =>
                changed[1].Op0Register = NativeRegister.ECX,
                "The instance producer's hidden MethodInfo register is EDX.");
            RejectNativeMutation(body, changed =>
                changed[4].NearBranch64 = body[8].IP,
                "The null branch must reach only the authenticated helper.");
            RejectNativeMutation(body, changed =>
                changed[5].Op0Register = NativeRegister.ECX,
                "The sink's hidden MethodInfo argument must be zeroed in EDX.");
            RejectNativeMutation(body, changed =>
                changed[6].Op1Register = NativeRegister.RBX,
                "The tail receiver must be the producer result.");
            RejectNativeMutation(body, changed =>
                changed[8].Code = Code.Call_rel32_64,
                "A returning call is not the proved terminal transfer.");
            Assert.That(CallResultStringTailNullGuardProof.TryProveShape(
                body.Skip(1).ToArray()), Is.Null);
            Assert.That(CallResultStringTailNullGuardProof.TryProveShape(
                body.Append(body[^1]).ToArray()), Is.Null);

            method.Analyze();
            var calls = method.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.IsCall).ToArray();
            Assert.That(calls, Has.Length.EqualTo(2));
            var origin = calls[0];
            var guarded = calls[1];
            var result = (LocalVariable)origin.Destination!;
            bool Bound() => CallResultNullGuardProof.HasBoundTarget(method,
                result, origin, guarded, target);
            Assert.That(Bound(), Is.True);
            Assert.That(guarded.CallSemantics,
                Is.EqualTo(CallSemantics.NullCheckedInstance));
            Assert.That(RuntimeNullGuardCoalescer.Run(method), Is.Zero);

            var returnInstruction = method.ControlFlowGraph.Instructions
                .Single(instruction => instruction.OpCode == OpCode.Return);
            var originalReturn = returnInstruction.Operands[0];
            returnInstruction.SetOperand(0, result);
            try
            {
                Assert.That(Bound(), Is.False,
                    "The native terminal result must flow to the managed return.");
            }
            finally { returnInstruction.SetOperand(0, originalReturn); }

            var targetAddress = guarded.NativeAddress;
            guarded.NativeAddress = shape!.Value.NullCallsite;
            try { Assert.That(Bound(), Is.False); }
            finally { guarded.NativeAddress = targetAddress; }

            var producerAddress = origin.NativeAddress;
            origin.NativeAddress = shape.Value.NullCallsite;
            try { Assert.That(Bound(), Is.False); }
            finally { origin.NativeAddress = producerAddress; }

            method.OverrideReturnType = app.SystemTypes.SystemObjectType;
            try { Assert.That(Bound(), Is.False); }
            finally { method.OverrideReturnType = null; }

            target.OverrideReturnType = app.SystemTypes.SystemObjectType;
            try { Assert.That(Bound(), Is.False); }
            finally { target.OverrideReturnType = null; }

            RejectAlias(app.MethodsByAddress[method.UnderlyingPointer],
                method, Bound);
            RejectAlias(app.MethodsByAddress[producer.UnderlyingPointer],
                producer, Bound);
            RejectAlias(app.MethodsByAddress[target.UnderlyingPointer],
                target, Bound);
            Assert.That(Bound(), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectNativeMutation(Iced.Intel.Instruction[] body,
        Action<Iced.Intel.Instruction[]> mutate, string message)
    {
        var changed = body.ToArray();
        mutate(changed);
        Assert.That(CallResultStringTailNullGuardProof.TryProveShape(changed),
            Is.Null, message);
    }

    private static void RejectAlias<T>(System.Collections.Generic.List<T> bindings,
        T target, Func<bool> bound)
    {
        bindings.Add(target);
        try { Assert.That(bound(), Is.False); }
        finally { bindings.RemoveAt(bindings.Count - 1); }
    }
}
