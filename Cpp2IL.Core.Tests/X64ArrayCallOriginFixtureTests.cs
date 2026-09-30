using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional player-only call-result provenance and ordered typed capture regressions.</summary>
[NonParallelizable]
public class X64ArrayCallOriginFixtureTests
{
    [TestCase("ReadProduced", 1)]
    [TestCase("CaptureBeforeEffect", 1)]
    [TestCase("ReadAfterEffect", 1)]
    [TestCase("ReadAroundEffect", 2)]
    [TestCase("ReadPairCaptured", 2)]
    public void ExactCallsRetainEachReturnCaptureAndReceiverCheck(string name, int count)
    {
        WithFixture(app =>
        {
            var method = Method(app, name);
            var decoded = X86Utils.Iterate(method).ToArray();
            var evidence = X64GuardedArrayOperationProof.Find(method, decoded);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Sites, Has.Count.EqualTo(count));
            Assert.That(evidence.Sites.All(site => site.ArrayOrigin.Call != null), Is.True);
            var origins = evidence.Sites.Select(site => site.ArrayOrigin.Call!).ToArray();
            Assert.That(origins.Select(origin => origin.Ip).Distinct().ToArray(), Has.Length.EqualTo(count));
            if (name == "ReadAroundEffect")
                Assert.That(origins[0].Target, Is.SameAs(origins[1].Target),
                    "Separate invocations of one producer retain separate definitions.");
            if (name == "ReadPairCaptured")
            {
                Assert.That(origins[0].Target, Is.Not.SameAs(origins[1].Target));
                Assert.That(origins[1].Ip, Is.LessThan(evidence.Sites[0].OperationIp),
                    "The first result remains captured across the second producer invocation.");
            }
            var cache = method.RawBytes;
            try
            {
                var changed = cache.ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { method.RawBytes = cache; }

            method.Analyze();
            IlGenerator.ValidateGuardedArrayOperations(method);
            var instructions = method.ControlFlowGraph!.Instructions.ToArray();
            var bridge = instructions.First(instruction => instruction.OpCode == OpCode.Jump);
            var bridgeTarget = bridge.Operands[0];
            try
            {
                bridge.SetOperand(0, bridge);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                    "The emitted bridge must agree with its authenticated successor, not just cached graph edges.");
            }
            finally { bridge.SetOperand(0, bridgeTarget); }
            var calls = origins.Select(origin => instructions.Single(instruction =>
                instruction.NativeAddress == origin.Ip && instruction.OpCode == OpCode.Call)).ToArray();
            var accesses = evidence.Sites.Select(site => instructions.Single(instruction =>
                instruction.NativeAddress == site.OperationIp &&
                instruction.Operands.Any(operand => operand is ArrayAccess))).ToArray();

            if (name == "ReadAroundEffect" || name == "ReadPairCaptured")
            {
                var access = accesses[1].Operands.OfType<ArrayAccess>().Single();
                var originalArray = access.Array;
                try
                {
                    access.Array = (LocalVariable)calls[0].Destination!;
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                        "A same-typed earlier result is not the second native producer result.");
                }
                finally { access.Array = originalArray; }
            }

            foreach (var check in evidence.NullCheckedCalls)
            {
                var call = instructions.Single(instruction => instruction.IsCall && instruction.NativeAddress == check.Ip);
                Assert.That(call.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
                try
                {
                    call.CallSemantics = CallSemantics.Direct;
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                        "A receiver check cannot disappear after its native guard was coalesced.");
                }
                finally { call.CallSemantics = CallSemantics.NullCheckedInstance; }
            }

            foreach (var call in instructions.Where(instruction => instruction.IsCall &&
                         instruction.Operands[0] is MethodAnalysisContext { IsStatic: false } &&
                         evidence.NullCheckedCalls.All(check => check.Ip != instruction.NativeAddress)))
            {
                Assert.That(call.CallSemantics, Is.EqualTo(CallSemantics.Direct));
                try
                {
                    call.CallSemantics = CallSemantics.NullCheckedInstance;
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                        "An unevidenced receiver check cannot change the native invocation convention.");
                }
                finally { call.CallSemantics = CallSemantics.Direct; }
            }

            foreach (var call in calls)
            {
                var originalReceiver = call.Operands[2];
                try
                {
                    call.SetOperand(2, new LocalVariable("changed-receiver", new ISIL.Register(null, "other"),
                        method.Parameters[0].ParameterType));
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                        "A same-typed receiver without the original incoming value is not native provenance.");
                }
                finally { call.SetOperand(2, originalReceiver); }
            }

            var removedCall = calls[0];
            var block = method.ControlFlowGraph.FindBlockByInstruction(removedCall)!;
            var position = block.Instructions.IndexOf(removedCall);
            try
            {
                block.Instructions.RemoveAt(position);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { block.Instructions.Insert(position, removedCall); }
            var targetOperand = calls[0].Operands[0];
            try
            {
                calls[0].SetOperand(0, Method(app, origins[0].Target.Name == "GetValues" ? "GetOther" : "GetValues"));
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                    "Matching array return types cannot replace the original native call target.");
            }
            finally { calls[0].SetOperand(0, targetOperand); }
            foreach (var effect in instructions.Where(instruction => instruction.IsCall &&
                         instruction.Operands[0] is MethodAnalysisContext { Name: "ChangeValue" }))
            {
                Assert.That(evidence.InvocationArguments.Count(argument => argument.CallIp == effect.NativeAddress),
                    Is.EqualTo(3), "The receiver and both scalar operands have independent native origins.");
                var argumentStart = effect.OpCode == OpCode.Call ? 3 : 2;
                if (name == "ReadAfterEffect")
                {
                    Assert.That(effect.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance),
                        "The complete array proof authenticates this two-scalar receiver check.");
                    Assert.That(evidence.InvocationArguments.Where(argument => argument.CallIp == effect.NativeAddress)
                        .Select(argument => argument.ParameterIndex), Is.EqualTo(new[] { -1, 0, 1 }));
                    var originalOperands = effect.Operands.ToList();
                    try
                    {
                        effect.RemoveOperandAt(argumentStart + 1);
                        Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                            "The checked array-composition call must retain both scalar arguments.");
                    }
                    finally { effect.SetOperands(originalOperands); }
                }
                for (var index = argumentStart; index < argumentStart + 2; index++)
                {
                    var original = effect.Operands[index];
                    try
                    {
                        effect.SetOperand(index, new Immediate(123));
                        Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                            "A matching scalar type cannot replace the native argument value.");
                    }
                    finally { effect.SetOperand(index, original); }
                    if (original is LocalVariable scalar)
                    {
                        var type = scalar.Type;
                        try
                        {
                            scalar.Type = app.SystemTypes.SystemInt64Type;
                            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                                "The same register origin cannot change the managed argument width.");
                        }
                        finally { scalar.Type = type; }
                    }
                    if (original is LocalVariable local && method.ParameterLocals.Contains(local))
                    {
                        var overwritten = new Instruction(-1, OpCode.Move, local, new Immediate(123));
                        var owner = method.ControlFlowGraph.FindBlockByInstruction(effect)!;
                        var at = owner.Instructions.IndexOf(effect);
                        try
                        {
                            owner.Instructions.Insert(at, overwritten);
                            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method),
                                "Incoming storage identity cannot authenticate an overwritten parameter value.");
                        }
                        finally { owner.Instructions.Remove(overwritten); }
                    }
                }
            }
            IlGenerator.ValidateGuardedArrayOperations(method);
        });
    }

    [Test]
    public void ProducerRequiresOriginalArrayReturnDescriptorAndUniqueManagedAbi()
    {
        WithFixture(app =>
        {
            var method = Method(app, "CaptureBeforeEffect");
            var target = Method(app, "GetValues");
            var decoded = X86Utils.Iterate(method).ToArray();
            Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Not.Null);
            var raw = target.Definition!.RawReturnType!;
            var mods = raw.NumMods;
            var byref = raw.Byref;
            var pinned = raw.Pinned;
            try
            {
                raw.NumMods = 1;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
                raw.NumMods = mods;
                raw.Byref = 1;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
                raw.Byref = byref;
                raw.Pinned = 1;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { raw.NumMods = mods; raw.Byref = byref; raw.Pinned = pinned; }
            var flags = target.Definition.iflags;
            try
            {
                foreach (var flag in new[] { MethodImplAttributes.Native, MethodImplAttributes.Runtime,
                             MethodImplAttributes.Unmanaged, MethodImplAttributes.Synchronized })
                {
                    target.Definition.iflags = (ushort)(flags | (ushort)flag);
                    Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null, flag.ToString());
                }
                target.Definition.iflags = (ushort)(flags | 0xF000);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { target.Definition.iflags = flags; }
            var bindings = app.MethodsByAddress[target.UnderlyingPointer];
            try
            {
                bindings.Add(target);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null,
                    "An ambiguous native entry cannot identify an array producer.");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }
            try
            {
                target.OverrideReturnType = app.SystemTypes.SystemObjectType;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { target.OverrideReturnType = null; }
            Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Not.Null);
        });
    }

    private static MethodAnalysisContext Method(ApplicationAnalysisContext app, string name) =>
        app.GetAssemblyByName("ArrayCallOriginsFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);

    private static void WithFixture(Action<ApplicationAnalysisContext> action)
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_CALL_ORIGINS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_ARRAY_CALL_ORIGINS_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            action(Cpp2IlApi.CurrentAppContext!);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
