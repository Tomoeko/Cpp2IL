using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GuardedArrayTailInvocationFixtureTests
{
    private ApplicationAnalysisContext _app = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_GUARDED_ARRAY_TAIL_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_GUARDED_ARRAY_TAIL_INVOCATION_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("ReadTail")]
    [TestCase("AcceptTail")]
    [TestCase("ReadAfterMarker")]
    [TestCase("ReadProducedTail")]
    [TestCase("ReadParameterTail")]
    public void TerminalInvocationRetainsItsCheckedElementReceiverAndFrame(string name)
    {
        WithFixture(app =>
        {
            var method = Method(app, name);
            var evidence = X64GuardedArrayOperationProof.Find(method, X86Utils.Iterate(method).ToArray());
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Sites, Has.Count.EqualTo(1));
            var native = evidence.Body.Single(instruction =>
                X64GuardedArrayOperationProof.IsTailInvocation(instruction));
            Assert.That(evidence.EffectAddresses.Last(), Is.EqualTo(native.IP));
            Assert.That(evidence.NullCheckedCalls.Single(check => check.Ip == native.IP).Receiver.DefinitionIp,
                Is.EqualTo(evidence.Sites[0].OperationIp));
            var values = X64NativeInvocationValues.Create(evidence.Body, evidence.NoReturnCallAddresses)!;
            Assert.That(values.HasCallFrame(native.IP, true), Is.True);
            Assert.That(X64NativeInvocationFrameProof.IsValid(method, evidence.Body, values), Is.True);

            method.Analyze();
            IlGenerator.ValidateGuardedArrayOperations(method);
            var recorded = method.GetExtraData<X64GuardedArrayOperationProof.Evidence>(
                X64GuardedArrayOperationProof.EvidenceKey)!;
            try
            {
                method.PutExtraData<X64GuardedArrayOperationProof.Evidence>(
                    X64GuardedArrayOperationProof.EvidenceKey, null!);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { method.PutExtraData(X64GuardedArrayOperationProof.EvidenceKey, recorded); }
            var instructions = method.ControlFlowGraph!.Instructions.ToArray();
            if (name == "ReadAfterMarker")
            {
                var marker = instructions.Single(instruction => instruction.OpCode == OpCode.Add);
                try
                {
                    marker.OpCode = OpCode.Subtract;
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { marker.OpCode = OpCode.Add; }
                var increment = marker.Operands[2];
                try
                {
                    marker.SetOperand(2, new Immediate(0));
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { marker.SetOperand(2, increment); }
            }
            var invocation = instructions.Single(instruction => instruction.IsCall && instruction.NativeAddress == native.IP);
            Assert.That(invocation.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
            var bridge = instructions.First(instruction => instruction.OpCode == OpCode.Jump);
            var bridgeTarget = bridge.Operands[0];
            try
            {
                bridge.SetOperand(0, bridge);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { bridge.SetOperand(0, bridgeTarget); }
            var finalReturn = instructions.Single(instruction => instruction.OpCode == OpCode.Return);
            var returnOperands = finalReturn.Operands.ToArray();
            try
            {
                finalReturn.SetOperands(new Immediate(0));
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                finalReturn.SetOperands();
                if (!method.IsVoid)
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { finalReturn.SetOperands(returnOperands.ToList()); }
            var returnAddress = finalReturn.NativeAddress;
            try
            {
                finalReturn.NativeAddress = native.IP + 1;
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { finalReturn.NativeAddress = returnAddress; }
            if (!method.IsVoid)
            {
                var returned = (LocalVariable)returnOperands.Single();
                try
                {
                    finalReturn.SetOperand(0, new LocalVariable("changed-result", new ISIL.Register(null, "other"),
                        returned.Type));
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { finalReturn.SetOperands(returnOperands.ToList()); }
                var returnBlock = method.ControlFlowGraph.FindBlockByInstruction(finalReturn)!;
                var returnPosition = returnBlock.Instructions.IndexOf(finalReturn);
                var copied = new LocalVariable("copied-tail-result", new ISIL.Register(null, "copy"), returned.Type);
                var copy = new Instruction(0, OpCode.Move, copied, returned) { NativeAddress = native.IP };
                try
                {
                    returnBlock.Instructions.Insert(returnPosition, copy);
                    finalReturn.SetOperand(0, copied);
                    Assert.DoesNotThrow(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally
                {
                    finalReturn.SetOperands(returnOperands.ToList());
                    returnBlock.Instructions.Remove(copy);
                }
                var repeatedInvocation = new Instruction(0, OpCode.Jump, invocation) { NativeAddress = native.IP };
                try
                {
                    returnBlock.Instructions.Insert(returnPosition, repeatedInvocation);
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { returnBlock.Instructions.Remove(repeatedInvocation); }
                var overwrite = new Instruction(0, OpCode.Move, returned, new Immediate(0))
                    { NativeAddress = native.IP };
                try
                {
                    returnBlock.Instructions.Insert(returnPosition, overwrite);
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { returnBlock.Instructions.Remove(overwrite); }
                var result = invocation.Destination!;
                try
                {
                    invocation.Destination = new LocalVariable("changed-result-width", new ISIL.Register(null, "other"),
                        app.SystemTypes.SystemInt64Type);
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { invocation.Destination = result; }
            }
            var start = invocation.OpCode == OpCode.Call ? 2 : 1;
            var receiver = invocation.Operands[start];
            try
            {
                invocation.SetOperand(start, new LocalVariable("changed-element", new ISIL.Register(null, "other"),
                    ((LocalVariable)receiver).Type));
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { invocation.SetOperand(start, receiver); }
            try
            {
                invocation.CallSemantics = CallSemantics.Direct;
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { invocation.CallSemantics = CallSemantics.NullCheckedInstance; }
            if (name == "AcceptTail")
            {
                var argument = invocation.Operands[start + 1];
                try
                {
                    invocation.SetOperand(start + 1, new Immediate(123));
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { invocation.SetOperand(start + 1, argument); }
            }
            var block = method.ControlFlowGraph.FindBlockByInstruction(invocation)!;
            var position = block.Instructions.IndexOf(invocation);
            try
            {
                block.Instructions.RemoveAt(position);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { block.Instructions.Insert(position, invocation); }
            IlGenerator.ValidateGuardedArrayOperations(method);
        });
    }

    [Test]
    public void TailRequiresOriginalReturnSignatureUniqueTargetAndUnchangedBytes()
    {
        WithFixture(app =>
        {
            var method = Method(app, "ReadTail");
            var decoded = X86Utils.Iterate(method).ToArray();
            Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Not.Null);
            var target = Method(app, "Read");
            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            try
            {
                bindings.Add(target);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }
            var cache = method.RawBytes;
            try
            {
                var changed = cache.ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { method.RawBytes = cache; }
            try
            {
                target.OverrideReturnType = app.SystemTypes.SystemInt64Type;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { target.OverrideReturnType = null; }
            Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Not.Null);
        });
    }

    private static MethodAnalysisContext Method(ApplicationAnalysisContext app, string name) =>
        app.GetAssemblyByName("GuardedArrayTailInvocationFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);

    private void WithFixture(Action<ApplicationAnalysisContext> action) => action(_app);
}
