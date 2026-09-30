using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeNullCheckedInvocationFixtureTests
{
    [Test]
    public void NaturalCoalescingRetainsSingleScalarArgumentEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var callers = app.GetAssemblyByName("NativeNullCheckedInvocationFixture")!.Types
                .Single(type => type.Name == "InvocationHolder").Methods
                .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)).ToArray();
            Assert.That(callers, Has.Length.EqualTo(7));
            foreach (var caller in callers)
            {
                caller.Analyze();
                Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True, caller.Name);
                Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, caller.Name);
                var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition), caller.Name);
                var call = caller.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall &&
                    instruction.Operands[0] is MethodAnalysisContext { Parameters.Count: 1 });
                var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
                var argument = call.Operands[argumentIndex];
                // A legal typed literal still has to be the value established by the native callsite.
                call.SetOperand(argumentIndex, argument is Immediate { Value: 0 } ? new Immediate(1) : new Immediate(0));
                try
                {
                    Reject(caller);
                    Assert.That(() => IlGenerator.GenerateIl(caller, definition), Throws.TypeOf<DecompilerException>());
                }
                finally { call.SetOperand(argumentIndex, argument); }

                var admitted = caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey);
                caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
                try
                {
                    Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
                    Assert.That(() => IlGenerator.GenerateIl(caller, definition), Throws.TypeOf<DecompilerException>());
                }
                finally { caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, admitted!); }
                Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, caller.Name);
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition), caller.Name);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void AnalysisAndIlGenerationAuthenticateTheComposedSnapshotWrite()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var method = app.GetAssemblyByName("NativeNullCheckedInvocationFixture")!.Types
                .Single(type => type.Name == "InvocationHolder").Methods
                .Single(candidate => candidate.Name == "ReadReplacedSnapshot");
            method.Analyze();
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(method), Is.True);
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True);
            Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction =>
                instruction.Operands is [MemoryOperand, _]), Is.False);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld),
                Is.EqualTo(2), "Both the captured reference replacement and counter effect must remain.");
            Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt),
                Is.EqualTo(1), "The invocation retains the receiver null check.");
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True,
                "Lowering a branch Block to its first Instruction keeps the authenticated destination.");
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            method.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(method), Is.True);
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Native null-checked invocation"));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void EveryExactInvocationRebindsAfterSsaRemovalAndRejectsMutableEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_NULL_CHECKED_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var callers = app.GetAssemblyByName("NativeNullCheckedInvocationFixture")!.Types
                .Single(type => type.Name == "InvocationHolder").Methods
                .Where(method => method.Name is not (".ctor" or "Produce")).ToArray();
            Assert.That(callers, Has.Length.EqualTo(11));
            foreach (var caller in callers)
            {
                caller.EnsureRawBytes();
                PrepareSsa(caller);
                var graph = caller.ControlFlowGraph!;
                var guard = graph.Blocks.Single(block => block.Instructions.LastOrDefault()?.OpCode == OpCode.ConditionalJump);
                var branch = guard.Instructions[^1];
                var comparison = guard.Instructions.Single(instruction => instruction.OpCode == OpCode.CheckEqual);
                var receiver = (LocalVariable)comparison.Operands[1];
                var invocation = graph.Instructions.Single(instruction => instruction.IsCall &&
                    instruction.Operands[0] is MethodAnalysisContext method && method.DeclaringType!.Name == "InvocationNode");
                Assert.That(X64NativeNullCheckedInvocationProof.TryGetCandidate(caller, invocation, out var target, out _), Is.True);
                Assert.That(X64NativeNullCheckedInvocationProof.TryRecord(caller, comparison, branch, receiver, invocation, target),
                    Is.True, caller.Name);
                var nullArm = (Block)branch.Operands[0];
                var intrinsic = nullArm.Instructions.Single(instruction => instruction.OpCode == OpCode.RuntimeNullThrow);
                var normalArm = guard.Successors.Single(block => block != nullArm);
                invocation.CallSemantics = CallSemantics.NullCheckedInstance;
                branch.OpCode = OpCode.Jump;
                branch.SetOperands(normalArm);
                guard.Successors.Remove(nullArm);
                nullArm.Predecessors.Remove(guard);
                guard.CalculateBlockType();
                graph.RemoveUnreachableBlocks();
                Finish(caller);
                graph = caller.ControlFlowGraph!;
                Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, caller.Name);

                var normalTarget = branch.Operands[0];
                branch.SetOperand(0, graph.EntryBlock);
                try { Reject(caller); }
                finally { branch.SetOperand(0, normalTarget); }

                var callBlock = graph.FindBlockByInstruction(invocation)!;
                callBlock.Instructions.Insert(callBlock.Instructions.IndexOf(invocation), intrinsic);
                try { Reject(caller); }
                finally { callBlock.Instructions.Remove(intrinsic); }
                var allocation = new Instruction(-1, OpCode.Newobj,
                    new LocalVariable("injected-object", new Register(7100, "injected-object"), target.DeclaringType),
                    target.DeclaringType!) { NativeAddress = invocation.NativeAddress };
                callBlock.Instructions.Insert(callBlock.Instructions.IndexOf(invocation), allocation);
                try { Reject(caller); }
                finally { callBlock.Instructions.Remove(allocation); }
                var returned = graph.Instructions.FirstOrDefault(instruction =>
                    instruction.OpCode == OpCode.Return && instruction.Operands.Count == 1);
                if (returned != null)
                {
                    var value = returned.Operands[0];
                    returned.SetOperand(0, value is Immediate { Value: 0 } ? new Immediate(1) : new Immediate(0));
                    try { Reject(caller); }
                    finally { returned.SetOperand(0, value); }
                }

                var cache = caller.RawBytes;
                var changed = cache.AsSpan().ToArray();
                changed[0] ^= 1;
                caller.RawBytes = new BinarySlice(changed);
                try { Reject(caller); }
                finally { caller.RawBytes = cache; }
                var interior = caller.UnderlyingPointer + 1;
                app.MethodsByAddress[interior] = [caller];
                try { Reject(caller); }
                finally { app.MethodsByAddress.Remove(interior); }
                foreach (var method in new[] { caller, target })
                {
                    var name = method.Name;
                    method.Name = "ChangedIdentity";
                    try { Reject(caller); }
                    finally { method.Name = name; }
                    var flags = method.Definition!.iflags;
                    method.Definition.iflags = (ushort)(flags | (ushort)MethodImplAttributes.Synchronized);
                    try { Reject(caller); }
                    finally { method.Definition.iflags = flags; }
                    var bindings = app.MethodsByAddress[method.UnderlyingPointer];
                    app.MethodsByAddress[method.UnderlyingPointer] = bindings.Concat(new[] { method }).ToList();
                    try { Reject(caller); }
                    finally { app.MethodsByAddress[method.UnderlyingPointer] = bindings; }
                }
                var rawBase = caller.DeclaringType!.Definition!.RawBaseType!;
                var pinned = rawBase.Pinned;
                rawBase.Pinned = 1;
                try { Reject(caller); }
                finally { rawBase.Pinned = pinned; }

                var producerCall = graph.Instructions.FirstOrDefault(instruction => instruction.IsCall &&
                    !ReferenceEquals(instruction, invocation));
                if (producerCall != null)
                {
                    var semantics = producerCall.CallSemantics;
                    producerCall.CallSemantics = CallSemantics.NullCheckedInstance;
                    try { Reject(caller); }
                    finally { producerCall.CallSemantics = semantics; }
                    var producer = (MethodAnalysisContext)producerCall.Operands[0];
                    var pointer = producer.Definition!.GetType().GetField("_methodPointer",
                        BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var originalPointer = pointer.GetValue(producer.Definition);
                    pointer.SetValue(producer.Definition, producer.UnderlyingPointer + 1);
                    try { Reject(caller); }
                    finally { pointer.SetValue(producer.Definition, originalPointer); }
                }

                if (target.Parameters.Count != 0)
                {
                    var argumentIndex = invocation.OpCode == OpCode.Call ? 3 : 2;
                    var argument = invocation.Operands[argumentIndex];
                    invocation.SetOperand(argumentIndex, argument is Immediate { Value: 0 } ? new Immediate(1) : new Immediate(0));
                    try { Reject(caller); }
                    finally { invocation.SetOperand(argumentIndex, argument); }
                }
                var priorCount = invocation.Operands.Count;
                invocation.AddOperands([new Immediate(1)]);
                try { Reject(caller); }
                finally { invocation.RemoveOperandAt(priorCount); }

                var store = graph.Instructions.FirstOrDefault(instruction =>
                    instruction.Operands.Count != 0 && instruction.Operands[0] is FieldReference field && field.Field.Name == "BeforeCount");
                if (store != null)
                {
                    var valueIndex = store.Operands.Count - 1;
                    var stored = store.Operands[valueIndex];
                    store.SetOperand(valueIndex, new Immediate(123));
                    try { Reject(caller); }
                    finally { store.SetOperand(valueIndex, stored); }
                }
                if (caller.Name == "ReadReplacedSnapshot")
                {
                    var replacement = graph.Instructions.Single(instruction => instruction.Operands is
                        [LocalVariable, FieldReference field] && field.Field.Name == "Replacement").Destination!;
                    var receiverIndex = invocation.OpCode == OpCode.Call ? 2 : 1;
                    var original = invocation.Operands[receiverIndex];
                    invocation.SetOperand(receiverIndex, replacement);
                    try { Reject(caller); }
                    finally { invocation.SetOperand(receiverIndex, original); }
                    var stores = graph.Instructions.Where(instruction => instruction.Destination is FieldReference).ToArray();
                    Assert.That(stores, Has.Length.EqualTo(2));
                    var writtenField = ((FieldReference)stores[0].Destination!).Field;
                    var attributes = writtenField.OverrideAttributes;
                    writtenField.Attributes |= FieldAttributes.InitOnly;
                    try { Reject(caller); }
                    finally { writtenField.OverrideAttributes = attributes; }
                    var block = graph.FindBlockByInstruction(stores[0])!;
                    Assert.That(graph.FindBlockByInstruction(stores[1]), Is.SameAs(block));
                    var first = block.Instructions.IndexOf(stores[0]);
                    var second = block.Instructions.IndexOf(stores[1]);
                    block.Instructions[first] = stores[1];
                    block.Instructions[second] = stores[0];
                    try { Reject(caller); }
                    finally { block.Instructions[first] = stores[0]; block.Instructions[second] = stores[1]; }
                }
                Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, "Restored " + caller.Name);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void Reject(MethodAnalysisContext method) =>
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.False, method.Name);

    // Exercise admission at the same SSA stage as the shared coalescer, then
    // authenticate the retained operation after the normal final transformations.
    private static void PrepareSsa(MethodAnalysisContext method)
    {
        method.ConvertedIsil = method.AppContext.InstructionSet.GetIsilFromMethod(method);
        method.ParameterOperands = method.AppContext.InstructionSet.GetParameterOperandsFromMethod(method);
        method.ControlFlowGraph = new ISILControlFlowGraph(method.ConvertedIsil);
        StackAnalyzer.Analyze(method);
        method.DominatorInfo = new DominatorInfo(method.ControlFlowGraph);
        SsaForm.Build(method);
        LocalVariables.CreateAll(method);
        FlagConditionRecovery.Run(method);
        DeadCodeEliminator.Run(method);
        MetadataResolver.ResolveAll(method);
        KeyFunctionRecovery.Run(method);
        DeadCodeEliminator.Run(method);
        WriteBarrierRecovery.Run(method);
        InterfaceDispatchRecovery.Run(method);
        LocalVariables.ResolveTypesAndFields(method);
        IntegerTruncationRecovery.Run(method);
        DelegateInvokeRecovery.Run(method);
        BooleanFlagSimplifier.Run(method);
        DeadCodeEliminator.Run(method);
        SsaSimplifier.Run(method);
        for (var index = 0; index < 8 && ConstantFolder.Run(method); index++) SsaSimplifier.Run(method);
        ArrayLengthReadRecovery.Run(method);
        LocalVariables.PropagateLateSignedIntegerTypes(method);
        MetadataResolver.ResolveProvedInertObjectConstructorTailCalls(method);
        InternalCallGuardRemover.Run(method);
        KeyFunctionRecovery.Run(method);
    }

    private static void Finish(MethodAnalysisContext method)
    {
        DeadCodeEliminator.Run(method);
        method.DominatorInfo = new DominatorInfo(method.ControlFlowGraph!);
        CallArgumentTrimmer.Run(method);
        NativeEntryValueValidator.Record(method);
        SsaForm.Remove(method);
        CopyCoalescer.Run(method);
        Simplifier.Simplify(method);
        FloatLiteralRecovery.Run(method);
        ArrayRecovery.Run(method);
        LocalVariables.TypeAddressedLocals(method);
        ConstantBranchFolder.Run(method);
        EqualityBranchInverter.Run(method);
        CallArgumentTrimmer.Run(method);
        ConstructorChainRecovery.Run(method);
        DeadCodeEliminator.Run(method);
        LocalVariables.RemoveUnused(method);
    }
}
