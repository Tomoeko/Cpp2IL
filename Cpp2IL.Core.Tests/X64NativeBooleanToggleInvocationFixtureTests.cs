using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
public class X64NativeBooleanToggleInvocationFixtureTests
{
    private MethodAnalysisContext _caller = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_BOOLEAN_TOGGLE_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_BOOLEAN_TOGGLE_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _caller = app.GetAssemblyByName("NativeBooleanToggleInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "ToggleAndForward");
        _caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void OwnerPredicateStorePrecedesTheTargetCaptureAndCheckedCall()
    {
        Accept();
        var operations = _caller.ControlFlowGraph!.Instructions.ToArray();
        var comparison = Comparison;
        var capture = Capture;
        var store = Store;
        var receiver = operations.Single(operation => operation is
            { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference { Field.Name: "Target" }] });
        Assert.That(capture.NativeAddress, Is.EqualTo(comparison.NativeAddress));
        Assert.That(capture.IntegerBitWidth, Is.EqualTo(8));
        Assert.That(comparison.IntegerBitWidth, Is.EqualTo(8));
        Assert.That(Array.IndexOf(operations, capture), Is.LessThan(Array.IndexOf(operations, store)));
        Assert.That(Array.IndexOf(operations, store), Is.LessThan(Array.IndexOf(operations, receiver)));
        Assert.That(Call.Operands[Call.OpCode == OpCode.Call ? 3 : 2], Is.SameAs(comparison.Destination));
        var il = Definition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ceq), Is.EqualTo(1));
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
    }

    [TestCase("predicate-opcode")]
    [TestCase("predicate-zero")]
    [TestCase("predicate-type")]
    [TestCase("predicate-address")]
    [TestCase("predicate-width")]
    [TestCase("capture-width")]
    [TestCase("capture-address")]
    [TestCase("capture-type")]
    [TestCase("capture-owner")]
    [TestCase("capture-field")]
    [TestCase("field-offset")]
    [TestCase("raw-field-name-index")]
    [TestCase("owner-raw-flags")]
    [TestCase("call-argument")]
    [TestCase("store-value")]
    [TestCase("store-owner")]
    [TestCase("store-address")]
    [TestCase("store-width")]
    [TestCase("remove-comparison")]
    [TestCase("duplicate-store")]
    [TestCase("store-after-guard")]
    [TestCase("store-after-call")]
    [TestCase("missing-sites")]
    public void NativePredicateAndPreCheckOwnerMutationCannotBeChanged(string mutation)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var comparison = Comparison;
        var capture = Capture;
        var store = Store;
        var access = (FieldReference)capture.Operands[1];
        var predicate = (LocalVariable)comparison.Destination!;
        var captured = (LocalVariable)capture.Destination!;
        var call = Call;
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
                case "predicate-type":
                    var predicateType = predicate.Type;
                    undo.Add(() => predicate.Type = predicateType);
                    predicate.Type = _caller.AppContext.SystemTypes.SystemInt32Type;
                    break;
                case "predicate-address":
                    var predicateAddress = comparison.NativeAddress;
                    undo.Add(() => comparison.NativeAddress = predicateAddress);
                    comparison.NativeAddress++;
                    break;
                case "predicate-width":
                    undo.Add(() => comparison.IntegerBitWidth = 8);
                    comparison.IntegerBitWidth = 32;
                    break;
                case "capture-width":
                    undo.Add(() => capture.IntegerBitWidth = 8);
                    capture.IntegerBitWidth = 32;
                    break;
                case "capture-address":
                    var captureAddress = capture.NativeAddress;
                    undo.Add(() => capture.NativeAddress = captureAddress);
                    capture.NativeAddress++;
                    break;
                case "capture-type":
                    var captureType = captured.Type;
                    undo.Add(() => captured.Type = captureType);
                    captured.Type = _caller.AppContext.SystemTypes.SystemByteType;
                    break;
                case "capture-owner":
                    var owner = access.Local;
                    undo.Add(() => access.Local = owner);
                    access.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
                    break;
                case "capture-field":
                    var field = access.Field;
                    var fieldOffset = access.Offset;
                    undo.Add(() => { access.Field = field; access.Offset = fieldOffset; });
                    access.Field = ((MethodAnalysisContext)call.Operands[0]).DeclaringType!.Fields.Single(candidate => candidate.Name == "Flag");
                    access.Offset = access.Field.Offset;
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
                case "owner-raw-flags":
                    var flags = _caller.DeclaringType!.Definition!.Flags;
                    undo.Add(() => _caller.DeclaringType.Definition.Flags = flags);
                    _caller.DeclaringType.Definition.Flags ^= (uint)TypeAttributes.Sealed;
                    break;
                case "call-argument":
                    var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
                    var argument = call.Operands[argumentIndex];
                    undo.Add(() => call.SetOperand(argumentIndex, argument));
                    call.SetOperand(argumentIndex, new Immediate(0));
                    break;
                case "store-value":
                    var stored = store.Operands[1];
                    undo.Add(() => store.SetOperand(1, stored));
                    store.SetOperand(1, captured);
                    break;
                case "store-owner":
                    var storeAccess = (FieldReference)store.Operands[0];
                    var storeOwner = storeAccess.Local;
                    undo.Add(() => storeAccess.Local = storeOwner);
                    storeAccess.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
                    break;
                case "store-address":
                    var address = store.NativeAddress;
                    undo.Add(() => store.NativeAddress = address);
                    store.NativeAddress++;
                    break;
                case "store-width":
                    undo.Add(() => store.IntegerBitWidth = 0);
                    store.IntegerBitWidth = 8;
                    break;
                case "remove-comparison":
                    var comparisonBlock = graph.FindBlockByInstruction(comparison)!;
                    var comparisonPosition = comparisonBlock.Instructions.IndexOf(comparison);
                    undo.Add(() => comparisonBlock.Instructions.Insert(comparisonPosition, comparison));
                    comparisonBlock.Instructions.Remove(comparison);
                    break;
                case "duplicate-store":
                    var storeBlock = graph.FindBlockByInstruction(store)!;
                    var duplicate = new Instruction(-1, OpCode.Move, store.Operands[0], store.Operands[1])
                        { NativeAddress = store.NativeAddress };
                    undo.Add(() => storeBlock.Instructions.Remove(duplicate));
                    storeBlock.Instructions.Insert(storeBlock.Instructions.IndexOf(store) + 1, duplicate);
                    break;
                case "store-after-guard":
                case "store-after-call":
                    var originalBlock = graph.FindBlockByInstruction(store)!;
                    var originalPosition = originalBlock.Instructions.IndexOf(store);
                    var callBlock = graph.FindBlockByInstruction(call)!;
                    var incoming = graph.Instructions.Where(operation => operation.OpCode is OpCode.Jump or OpCode.ConditionalJump &&
                        operation.Operands.Count > 0 && ReferenceEquals(operation.Operands[0], call)).ToArray();
                    originalBlock.Instructions.Remove(store);
                    callBlock.Instructions.Insert(callBlock.Instructions.IndexOf(call) + (mutation == "store-after-call" ? 1 : 0), store);
                    if (mutation == "store-after-guard") foreach (var branch in incoming) branch.SetOperand(0, store);
                    undo.Add(() =>
                    {
                        foreach (var branch in incoming) branch.SetOperand(0, call);
                        callBlock.Instructions.Remove(store);
                        originalBlock.Instructions.Insert(originalPosition, store);
                    });
                    break;
                case "missing-sites":
                    var sites = _caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
                    undo.Add(() => _caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, sites));
                    _caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
                    Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(_caller), Is.True);
                    break;
            }
            Reject();
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept();
    }

    [TestCase("same")]
    [TestCase("narrow")]
    [TestCase("skipped")]
    public void PredicateCopiesRetainBooleanTypesAndExecuteBeforeTheCall(string mutation)
    {
        Accept();
        var call = Call;
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        var predicate = call.Operands[argumentIndex];
        var copied = new LocalVariable("predicate-copy", new Register(7500, "predicate-copy"), mutation == "narrow"
            ? _caller.AppContext.SystemTypes.SystemByteType : _caller.AppContext.SystemTypes.SystemBooleanType);
        var copy = new Instruction(-1, OpCode.Move, copied, predicate) { NativeAddress = call.NativeAddress };
        var graph = _caller.ControlFlowGraph!;
        var block = graph.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        var incoming = graph.Instructions.Where(operation => operation.OpCode is OpCode.Jump or OpCode.ConditionalJump &&
            operation.Operands.Count > 0 && ReferenceEquals(operation.Operands[0], call)).ToArray();
        Assert.That(position, Is.Zero);
        Assert.That(incoming, Is.Not.Empty);
        block.Instructions.Insert(position, copy);
        _caller.Locals.Add(copied);
        if (mutation != "skipped") foreach (var branch in incoming) branch.SetOperand(0, copy);
        call.SetOperand(argumentIndex, copied);
        try { if (mutation == "same") Accept(); else Reject(); }
        finally
        {
            call.SetOperand(argumentIndex, predicate);
            foreach (var branch in incoming) branch.SetOperand(0, call);
            _caller.Locals.Remove(copied);
            block.Instructions.Remove(copy);
        }
        Accept();
    }

    private Instruction Comparison => _caller.ControlFlowGraph!.Instructions.Single(operation =>
        operation.OpCode == OpCode.CheckEqual && operation.IntegerBitWidth == 8);
    private Instruction Capture => _caller.ControlFlowGraph!.Instructions.Single(operation => operation is
        { OpCode: OpCode.Move, IntegerBitWidth: 8, Operands: [LocalVariable, FieldReference] });
    private Instruction Store => _caller.ControlFlowGraph!.Instructions.Single(operation => operation is
        { OpCode: OpCode.Move, Operands: [FieldReference { Field.Name: "Flag" }, LocalVariable] });
    private Instruction Call => _caller.ControlFlowGraph!.Instructions.Single(operation => operation.IsCall);
    private MethodDefinition Definition => _caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private void Accept()
    {
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(_caller), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(_caller), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_caller, Definition));
    }

    private void Reject()
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(_caller), Is.False);
        Assert.That(() => IlGenerator.GenerateIl(_caller, Definition), Throws.TypeOf<DecompilerException>());
    }
}
