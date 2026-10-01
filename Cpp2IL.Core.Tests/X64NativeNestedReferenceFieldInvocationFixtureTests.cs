using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeNestedReferenceFieldInvocationFixtureTests
{
    private MethodAnalysisContext _caller = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NESTED_REFERENCE_FIELD_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_NESTED_REFERENCE_FIELD_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _caller = app.GetAssemblyByName("NativeNestedReferenceFieldInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "Forward");
        _caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void SourceReadPerformsTheEarlierNullCheckBeforeTheConsumerCall()
    {
        Accept();
        var call = Call();
        Assert.That(call.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(_caller.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.Jump), Is.EqualTo(2));
        Assert.That(_caller.ControlFlowGraph.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
        var body = Definition().CilMethodBody!;
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(3));
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
        var read = body.Instructions.Single(instruction => instruction.OpCode == CilOpCodes.Ldfld &&
            instruction.Operand is IFieldDescriptor field && field.Name == "Payload");
        Assert.That(body.Instructions.IndexOf(read), Is.LessThan(body.Instructions.ToList().FindIndex(instruction => instruction.OpCode == CilOpCodes.Callvirt)));
    }

    [TestCase("source-width")]
    [TestCase("payload-width")]
    [TestCase("source-address")]
    [TestCase("payload-address")]
    [TestCase("source-type")]
    [TestCase("payload-type")]
    [TestCase("payload-owner")]
    [TestCase("source-offset")]
    [TestCase("payload-offset")]
    [TestCase("source-raw-data")]
    [TestCase("payload-raw-data")]
    [TestCase("source-modifiers")]
    [TestCase("payload-modifiers")]
    [TestCase("source-token")]
    [TestCase("payload-token")]
    [TestCase("parameter-type")]
    [TestCase("parameter-raw-data")]
    [TestCase("parameter-byref")]
    [TestCase("source-owner-name-index")]
    [TestCase("payload-base-index")]
    [TestCase("null-argument")]
    [TestCase("source-argument")]
    [TestCase("direct-call")]
    [TestCase("native-cache")]
    [TestCase("missing-sites")]
    public void NestedCaptureAndImmutableFactsRejectMutation(string mutation)
    {
        Accept();
        var source = Capture("Source");
        var payload = Capture("Payload");
        var operation = mutation.StartsWith("source-", StringComparison.Ordinal) ? source : payload;
        var local = (LocalVariable)operation.Operands[0];
        var access = (FieldReference)operation.Operands[1];
        var field = access.Field;
        var call = Call();
        var target = (MethodAnalysisContext)call.Operands[0];
        var parameter = target.Parameters.Single();
        Action restore;
        switch (mutation)
        {
            case "source-width": case "payload-width":
                var width = operation.IntegerBitWidth;
                operation.IntegerBitWidth = 32;
                restore = () => operation.IntegerBitWidth = width;
                break;
            case "source-address": case "payload-address":
                var address = operation.NativeAddress;
                operation.NativeAddress++;
                restore = () => operation.NativeAddress = address;
                break;
            case "source-type": case "payload-type":
                var type = local.Type;
                local.Type = _caller.DeclaringType;
                restore = () => local.Type = type;
                break;
            case "payload-owner":
                var owner = access.Local;
                access.Local = (LocalVariable)Capture("Target").Operands[0];
                restore = () => access.Local = owner;
                break;
            case "source-offset": case "payload-offset":
                var offset = access.Offset;
                access.Offset++;
                restore = () => access.Offset = offset;
                break;
            case "source-raw-data": case "payload-raw-data":
                var raw = field.BackingData!.Field.RawFieldType!;
                var data = raw.Data;
                raw.Data = null!;
                restore = () => raw.Data = data;
                break;
            case "source-modifiers": case "payload-modifiers":
                var descriptor = field.BackingData!.Field.RawFieldType!;
                var modifiers = descriptor.NumMods;
                descriptor.NumMods++;
                restore = () => descriptor.NumMods = modifiers;
                break;
            case "source-token": case "payload-token":
                var definition = field.BackingData!.Field;
                var token = definition.token;
                definition.token++;
                restore = () => definition.token = token;
                break;
            case "parameter-type":
                var parameterType = parameter.OverrideParameterType;
                parameter.ParameterType = _caller.DeclaringType!;
                restore = () => parameter.OverrideParameterType = parameterType;
                break;
            case "parameter-raw-data":
                var parameterRaw = parameter.Definition!.RawType!;
                var parameterData = parameterRaw.Data;
                parameterRaw.Data = null!;
                restore = () => parameterRaw.Data = parameterData;
                break;
            case "parameter-byref":
                var rawParameter = parameter.Definition!.RawType!;
                var byref = rawParameter.Byref;
                rawParameter.Byref = 1;
                restore = () => rawParameter.Byref = byref;
                break;
            case "source-owner-name-index":
                var sourceOwner = field.FieldType.Definition!;
                var nameIndex = sourceOwner.NameIndex;
                sourceOwner.NameIndex++;
                restore = () => sourceOwner.NameIndex = nameIndex;
                break;
            case "payload-base-index":
                var payloadType = field.FieldType.Definition!;
                var parentIndex = payloadType.ParentIndex;
                payloadType.ParentIndex = _caller.DeclaringType!.Definition!.ByvalTypeIndex;
                restore = () => payloadType.ParentIndex = parentIndex;
                break;
            case "null-argument": case "source-argument":
                var argument = call.Operands[2];
                call.SetOperand(2, mutation == "null-argument" ? new Immediate(0) : source.Operands[0]);
                restore = () => call.SetOperand(2, argument);
                break;
            case "direct-call":
                var semantics = call.CallSemantics;
                call.CallSemantics = CallSemantics.Direct;
                restore = () => call.CallSemantics = semantics;
                break;
            case "native-cache":
                var bytes = _caller.RawBytes;
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                _caller.RawBytes = new BinarySlice(changed);
                restore = () => _caller.RawBytes = bytes;
                break;
            case "missing-sites":
                var sites = _caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
                _caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
                restore = () => _caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, sites);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        try { Reject(); }
        finally { restore(); }
        Accept();
    }

    [TestCase("skip-target")]
    [TestCase("conditional")]
    [TestCase("move-payload")]
    [TestCase("duplicate-payload")]
    [TestCase("extra-store")]
    [TestCase("entry")]
    [TestCase("exit")]
    [TestCase("block-order")]
    public void SourceGuardCannotSkipOrHideARequiredCapture(string mutation)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var source = Capture("Source");
        var sourceBlock = graph.FindBlockByInstruction(source)!;
        var sourceBranch = sourceBlock.Instructions.Last(instruction => instruction.OpCode != OpCode.Nop);
        var payload = Capture("Payload");
        var callBlock = graph.FindBlockByInstruction(payload)!;
        Action restore;
        switch (mutation)
        {
            case "skip-target":
                var target = sourceBranch.Operands[0];
                sourceBranch.SetOperand(0, callBlock);
                restore = () => sourceBranch.SetOperand(0, target);
                break;
            case "conditional":
                var code = sourceBranch.OpCode;
                sourceBranch.OpCode = OpCode.ConditionalJump;
                restore = () => sourceBranch.OpCode = code;
                break;
            case "move-payload": case "entry": case "exit":
                var moved = mutation == "move-payload" ? payload : source;
                var original = graph.FindBlockByInstruction(moved)!;
                var position = original.Instructions.IndexOf(moved);
                var destination = mutation == "entry" ? graph.EntryBlock : mutation == "exit" ? graph.ExitBlock : sourceBlock;
                original.Instructions.RemoveAt(position);
                destination.Instructions.Insert(0, moved);
                restore = () => { destination.Instructions.Remove(moved); original.Instructions.Insert(position, moved); };
                break;
            case "duplicate-payload":
                var payloadPosition = callBlock.Instructions.IndexOf(payload);
                callBlock.Instructions.Insert(payloadPosition + 1, payload);
                restore = () => callBlock.Instructions.RemoveAt(payloadPosition + 1);
                break;
            case "extra-store":
                var store = new Instruction(-1, OpCode.Move, payload.Operands[1], new Immediate(0))
                    { NativeAddress = payload.NativeAddress };
                sourceBlock.Instructions.Insert(sourceBlock.Instructions.Count - 1, store);
                restore = () => sourceBlock.Instructions.Remove(store);
                break;
            case "block-order":
                var other = graph.FindBlockByInstruction(Capture("Target"))!;
                var sourceIndex = graph.Blocks.IndexOf(sourceBlock);
                var targetIndex = graph.Blocks.IndexOf(other);
                graph.Blocks[sourceIndex] = other;
                graph.Blocks[targetIndex] = sourceBlock;
                restore = () => { graph.Blocks[sourceIndex] = sourceBlock; graph.Blocks[targetIndex] = other; };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        try { Reject(); }
        finally { restore(); }
        Accept();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void NestedOwnerCopiesRemainTypedAndCannotBeSkipped(bool changeType, bool skipCopy)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var read = Capture("Payload");
        var access = (FieldReference)read.Operands[1];
        var source = access.Local;
        var copy = new LocalVariable("nested-owner-copy", new Register(7560, "nested-owner-copy"),
            changeType ? _caller.DeclaringType : source.Type);
        var operation = new Instruction(-1, OpCode.Move, copy, source) { NativeAddress = read.NativeAddress };
        var block = graph.FindBlockByInstruction(read)!;
        var position = block.Instructions.IndexOf(read);
        var incoming = graph.Instructions.Where(instruction => instruction.OpCode == OpCode.Jump &&
            instruction.Operands.Count == 1 &&
            (ReferenceEquals(instruction.Operands[0], block) || ReferenceEquals(instruction.Operands[0], read))).ToArray();
        var targets = incoming.Select(instruction => instruction.Operands[0]).ToArray();
        block.Instructions.Insert(position, operation);
        _caller.Locals.Add(copy);
        access.Local = copy;
        foreach (var jump in incoming) jump.SetOperand(0, skipCopy ? read : operation);
        try
        {
            if (changeType || skipCopy) Reject();
            else Accept();
        }
        finally
        {
            access.Local = source;
            for (var index = 0; index < incoming.Length; index++) incoming[index].SetOperand(0, targets[index]);
            block.Instructions.Remove(operation);
            _caller.Locals.Remove(copy);
        }
        Accept();
    }

    private Instruction Capture(string field) => _caller.ControlFlowGraph!.Instructions.Single(instruction => instruction is
        { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference access] } && access.Field.Name == field);
    private Instruction Call() => _caller.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall);
    private MethodDefinition Definition() => _caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private void Accept()
    {
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(_caller), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(_caller), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_caller, Definition()));
    }

    private void Reject()
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(_caller), Is.False);
        Assert.That(() => IlGenerator.GenerateIl(_caller, Definition()), Throws.TypeOf<DecompilerException>());
    }
}
