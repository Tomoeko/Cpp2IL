using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeBooleanPredicateInvocationFixtureTests
{
    private Dictionary<string, MethodAnalysisContext> _callers = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_BOOLEAN_PREDICATE_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_BOOLEAN_PREDICATE_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeBooleanPredicateInvocationFixture")!.Types.Single(type => type.Name == "Node")
            .Methods.Where(method => method.Name.StartsWith("Forward", StringComparison.Ordinal))
            .ToDictionary(method => method.Name);
        foreach (var caller in _callers.Values) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("ForwardNegated", 1)]
    [TestCase("ForwardStoredPair", 2)]
    [TestCase("ForwardLivePair", 2)]
    [TestCase("ForwardComplementThenLive", 2)]
    public void CapturedPredicatesAndLaterLiveReadsRetainEveryCheckedCall(string name, int count)
    {
        var caller = _callers[name];
        Accept(caller);
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        var calls = operations.Where(operation => operation.IsCall).ToArray();
        Assert.That(calls, Has.Length.EqualTo(count));
        Assert.That(calls.All(call => call.CallSemantics == CallSemantics.NullCheckedInstance), Is.True);
        var comparison = Comparison(caller);
        Assert.That(Capture(caller).NativeAddress, Is.EqualTo(comparison.NativeAddress));
        var predicateCall = name is "ForwardStoredPair" or "ForwardLivePair" ? calls[^1] : calls[0];
        Assert.That(predicateCall.Operands[ArgumentIndex(predicateCall)], Is.SameAs(comparison.Destination));
        if (name is "ForwardStoredPair" or "ForwardLivePair")
            Assert.That(Array.IndexOf(operations, Capture(caller)), Is.GreaterThan(Array.IndexOf(operations, calls[0])));
        if (name == "ForwardComplementThenLive")
            Assert.That(Array.IndexOf(operations, operations.Single(operation => operation is
                { OpCode: OpCode.Move, IntegerBitWidth: 0, Operands: [LocalVariable, FieldReference { Field.Name: "Flag" }] })),
                Is.GreaterThan(Array.IndexOf(operations, calls[0])));
        var il = Definition(caller).CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(count));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld),
            Is.EqualTo(name == "ForwardStoredPair" ? 1 : 0));
    }

    [TestCase("predicate-opcode")]
    [TestCase("predicate-zero")]
    [TestCase("predicate-width")]
    [TestCase("predicate-type")]
    [TestCase("predicate-address")]
    [TestCase("capture-width")]
    [TestCase("capture-type")]
    [TestCase("capture-address")]
    [TestCase("capture-owner")]
    [TestCase("capture-field")]
    [TestCase("field-offset")]
    [TestCase("raw-field-name-index")]
    [TestCase("call-argument")]
    [TestCase("call-target")]
    [TestCase("remove-comparison")]
    [TestCase("capture-before-first-call")]
    [TestCase("capture-entry")]
    [TestCase("extra-predicate-consumer")]
    [TestCase("extra-capture-consumer")]
    [TestCase("unchecked-call")]
    [TestCase("missing-sites")]
    public void PredicateLoadAndInterveningEffectsRemainImmutable(string mutation)
    {
        var caller = _callers["ForwardLivePair"];
        Accept(caller);
        var graph = caller.ControlFlowGraph!;
        var comparison = Comparison(caller);
        var capture = Capture(caller);
        var captured = (LocalVariable)capture.Destination!;
        var predicate = (LocalVariable)comparison.Destination!;
        var access = (FieldReference)capture.Operands[1];
        var calls = graph.Instructions.Where(operation => operation.IsCall).ToArray();
        var call = calls[^1];
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "predicate-opcode":
                    undo.Add(() => comparison.OpCode = OpCode.CheckEqual);
                    comparison.OpCode = OpCode.CheckNotEqual;
                    break;
                case "predicate-zero":
                    var zero = comparison.Operands[2];
                    undo.Add(() => comparison.SetOperand(2, zero));
                    comparison.SetOperand(2, new Immediate(1));
                    break;
                case "predicate-width":
                    undo.Add(() => comparison.IntegerBitWidth = 8);
                    comparison.IntegerBitWidth = 32;
                    break;
                case "predicate-type":
                    var predicateType = predicate.Type;
                    undo.Add(() => predicate.Type = predicateType);
                    predicate.Type = caller.AppContext.SystemTypes.SystemInt32Type;
                    break;
                case "predicate-address":
                    var predicateAddress = comparison.NativeAddress;
                    undo.Add(() => comparison.NativeAddress = predicateAddress);
                    comparison.NativeAddress++;
                    break;
                case "capture-width":
                    undo.Add(() => capture.IntegerBitWidth = 8);
                    capture.IntegerBitWidth = 32;
                    break;
                case "capture-type":
                    var captureType = captured.Type;
                    undo.Add(() => captured.Type = captureType);
                    captured.Type = caller.AppContext.SystemTypes.SystemByteType;
                    break;
                case "capture-address":
                    var captureAddress = capture.NativeAddress;
                    undo.Add(() => capture.NativeAddress = captureAddress);
                    capture.NativeAddress++;
                    break;
                case "capture-owner":
                    var owner = access.Local;
                    undo.Add(() => access.Local = owner);
                    access.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
                    break;
                case "capture-field":
                    var field = access.Field;
                    undo.Add(() => access.Field = field);
                    access.Field = caller.DeclaringType!.Fields.Single(candidate => candidate.Name == "Calls");
                    break;
                case "field-offset":
                    var offset = access.Field.OverrideOffset;
                    undo.Add(() => access.Field.OverrideOffset = offset);
                    access.Field.OverrideOffset = access.Field.DefaultOffset + 1;
                    break;
                case "raw-field-name-index":
                    var nameIndex = access.Field.BackingData!.Field.nameIndex;
                    undo.Add(() => access.Field.BackingData.Field.nameIndex = nameIndex);
                    access.Field.BackingData.Field.nameIndex++;
                    break;
                case "call-argument":
                    var argumentIndex = ArgumentIndex(call);
                    var argument = call.Operands[argumentIndex];
                    undo.Add(() => call.SetOperand(argumentIndex, argument));
                    call.SetOperand(argumentIndex, new Immediate(0));
                    break;
                case "call-target":
                    var target = call.Operands[0];
                    undo.Add(() => call.SetOperand(0, target));
                    call.SetOperand(0, caller.DeclaringType!.Methods.Single(method => method.Name == "ForwardStoredPair"));
                    break;
                case "remove-comparison":
                    var comparisonBlock = graph.FindBlockByInstruction(comparison)!;
                    var comparisonPosition = comparisonBlock.Instructions.IndexOf(comparison);
                    undo.Add(() => comparisonBlock.Instructions.Insert(comparisonPosition, comparison));
                    comparisonBlock.Instructions.Remove(comparison);
                    break;
                case "capture-before-first-call":
                case "capture-entry":
                    var originalBlock = graph.FindBlockByInstruction(capture)!;
                    var originalPosition = originalBlock.Instructions.IndexOf(capture);
                    var destination = mutation == "capture-entry" ? graph.EntryBlock : graph.FindBlockByInstruction(calls[0])!;
                    originalBlock.Instructions.Remove(capture);
                    destination.Instructions.Insert(mutation == "capture-entry" ? 0 : destination.Instructions.IndexOf(calls[0]), capture);
                    undo.Add(() => { destination.Instructions.Remove(capture); originalBlock.Instructions.Insert(originalPosition, capture); });
                    break;
                case "extra-predicate-consumer":
                    var extra = new Instruction(-1, OpCode.Not, predicate, predicate) { NativeAddress = comparison.NativeAddress };
                    var block = graph.FindBlockByInstruction(comparison)!;
                    block.Instructions.Insert(block.Instructions.IndexOf(comparison) + 1, extra);
                    undo.Add(() => block.Instructions.Remove(extra));
                    break;
                case "extra-capture-consumer":
                    var unused = new LocalVariable("extra-capture-use", new Register(7601, "extra-capture-use"), predicate.Type);
                    caller.Locals.Add(unused);
                    undo.Add(() => caller.Locals.Remove(unused));
                    var additional = new Instruction(-1, OpCode.CheckNotEqual, unused, captured, new Immediate(0))
                        { NativeAddress = comparison.NativeAddress, IntegerBitWidth = 8 };
                    var captureBlock = graph.FindBlockByInstruction(comparison)!;
                    captureBlock.Instructions.Insert(captureBlock.Instructions.IndexOf(comparison) + 1, additional);
                    undo.Add(() => captureBlock.Instructions.Remove(additional));
                    break;
                case "unchecked-call":
                    undo.Add(() => call.CallSemantics = CallSemantics.NullCheckedInstance);
                    call.CallSemantics = CallSemantics.Direct;
                    break;
                case "missing-sites":
                    var sites = caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
                    undo.Add(() => caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, sites));
                    caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
                    Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
                    break;
            }
            Reject(caller);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept(caller);
    }

    [TestCase("remove")]
    [TestCase("duplicate")]
    [TestCase("value")]
    [TestCase("after-first-call")]
    public void IncomingOwnerStoreRemainsBeforeTheFirstNullFailure(string mutation)
    {
        var caller = _callers["ForwardStoredPair"];
        Accept(caller);
        var graph = caller.ControlFlowGraph!;
        var store = graph.Instructions.Single(operation => operation is
            { OpCode: OpCode.Move, Operands: [FieldReference { Field.Name: "Flag" }, LocalVariable] });
        var block = graph.FindBlockByInstruction(store)!;
        var position = block.Instructions.IndexOf(store);
        var value = store.Operands[1];
        Instruction? duplicate = null;
        Cpp2IL.Core.Graphs.Block? destination = null;
        try
        {
            switch (mutation)
            {
                case "remove": block.Instructions.Remove(store); break;
                case "duplicate":
                    duplicate = new(-1, OpCode.Move, store.Operands[0], value) { NativeAddress = store.NativeAddress };
                    block.Instructions.Insert(position, duplicate);
                    break;
                case "value": store.SetOperand(1, new Immediate(0)); break;
                case "after-first-call":
                    var call = graph.Instructions.First(operation => operation.IsCall);
                    destination = graph.FindBlockByInstruction(call)!;
                    block.Instructions.Remove(store);
                    destination.Instructions.Insert(destination.Instructions.IndexOf(call) + 1, store);
                    break;
            }
            Reject(caller);
        }
        finally
        {
            store.SetOperand(1, value);
            if (duplicate != null) block.Instructions.Remove(duplicate);
            if (mutation is "remove" or "after-first-call")
            {
                destination?.Instructions.Remove(store);
                block.Instructions.Insert(position, store);
            }
        }
        Accept(caller);
    }

    [TestCase("same")]
    [TestCase("narrow")]
    [TestCase("skipped")]
    public void PredicateCopiesExecuteWithTheExactBooleanType(string mutation)
    {
        var caller = _callers["ForwardLivePair"];
        Accept(caller);
        var call = caller.ControlFlowGraph!.Instructions.Last(operation => operation.IsCall);
        var index = ArgumentIndex(call);
        var predicate = call.Operands[index];
        var copied = new LocalVariable("predicate-copy", new Register(7600, "predicate-copy"), mutation == "narrow"
            ? caller.AppContext.SystemTypes.SystemByteType : caller.AppContext.SystemTypes.SystemBooleanType);
        var copy = new Instruction(-1, OpCode.Move, copied, predicate) { NativeAddress = call.NativeAddress };
        var graph = caller.ControlFlowGraph!;
        var block = graph.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        var incoming = graph.Instructions.Where(operation => operation.OpCode is OpCode.Jump or OpCode.ConditionalJump &&
            operation.Operands.Count > 0 && ReferenceEquals(operation.Operands[0], block.Instructions[0])).ToArray();
        Assert.That(incoming, Is.Not.Empty);
        var first = block.Instructions[0];
        // Insert before the call; if it is the block entry, every instruction
        // branch must enter the copy too, rather than skip its definition.
        block.Instructions.Insert(position, copy);
        caller.Locals.Add(copied);
        if (position == 0 && mutation != "skipped") foreach (var branch in incoming) branch.SetOperand(0, copy);
        call.SetOperand(index, copied);
        if (mutation == "skipped" && position != 0)
        {
            block.Instructions.Remove(copy);
            block.Instructions.Insert(0, copy);
        }
        try { if (mutation == "same") Accept(caller); else Reject(caller); }
        finally
        {
            call.SetOperand(index, predicate);
            foreach (var branch in incoming) branch.SetOperand(0, first);
            caller.Locals.Remove(copied);
            block.Instructions.Remove(copy);
        }
        Accept(caller);
    }

    private static Instruction Comparison(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(operation =>
        operation.OpCode == OpCode.CheckEqual && operation.IntegerBitWidth == 8);
    private static Instruction Capture(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(operation => operation is
        { OpCode: OpCode.Move, IntegerBitWidth: 8, Operands: [LocalVariable, FieldReference] });
    private static int ArgumentIndex(Instruction call) => call.OpCode == OpCode.Call ? 3 : 2;
    private static MethodDefinition Definition(MethodAnalysisContext caller) => caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private static void Accept(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, Definition(caller)));
    }

    private static void Reject(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False);
        Assert.That(() => IlGenerator.GenerateIl(caller, Definition(caller)), Throws.TypeOf<DecompilerException>());
    }
}
