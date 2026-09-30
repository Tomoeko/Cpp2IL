using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeReferenceProducerInvocationFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_REFERENCE_PRODUCER_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_REFERENCE_PRODUCER_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeReferenceProducerInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Forward", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(2));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("Forward")]
    [TestCase("ForwardSnapshot")]
    public void CheckedReturnedReceiverKeepsBothSitesAndTheCapturedProvider(string name)
    {
        var caller = Caller(name);
        Accept(caller);
        var producer = Producer(caller);
        var consumer = Consumer(caller);
        Assert.That(producer.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(consumer.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(consumer.Operands[1], Is.SameAs(producer.Destination));
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        Assert.That(Array.IndexOf(operations, Capture(caller)), Is.LessThan(Array.IndexOf(operations, producer)));
        Assert.That(caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey), Has.Count.EqualTo(2));
        var body = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!.CilMethodBody!;
        Assert.That(body.Instructions.Count(operation => operation.OpCode == CilOpCodes.Callvirt), Is.EqualTo(2));
        Assert.That(body.Instructions.Count(operation => operation.OpCode == CilOpCodes.Ldfld), Is.EqualTo(1));
    }

    [TestCase("target")]
    [TestCase("return-type")]
    [TestCase("raw-byref")]
    [TestCase("raw-modifiers")]
    [TestCase("virtual")]
    [TestCase("address")]
    [TestCase("width")]
    [TestCase("unchecked")]
    [TestCase("capture-type")]
    [TestCase("consumer-literal")]
    [TestCase("receiver-type-confusion")]
    public void ProducerIdentityAndExactResultCannotBeReplaced(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var producer = Producer(caller);
        var consumer = Consumer(caller);
        var target = (MethodAnalysisContext)producer.Operands[0];
        var rawReturn = target.Definition!.RawReturnType!;
        var returnType = target.OverrideReturnType;
        var attributes = target.OverrideAttributes;
        var byref = rawReturn.Byref;
        var modifiers = rawReturn.NumMods;
        var address = producer.NativeAddress;
        var width = producer.IntegerBitWidth;
        var semantics = producer.CallSemantics;
        var captured = (LocalVariable)producer.Destination!;
        var capturedType = captured.Type;
        var argument = consumer.Operands[2];
        try
        {
            switch (mutation)
            {
                case "target": producer.SetOperand(0, consumer.Operands[0]); break;
                case "return-type": target.OverrideReturnType = caller.AppContext.SystemTypes.SystemInt16Type; break;
                case "raw-byref": rawReturn.Byref = 1; break;
                case "raw-modifiers": rawReturn.NumMods = 1; break;
                case "virtual": target.OverrideAttributes = target.Attributes | MethodAttributes.Virtual; break;
                case "address": producer.NativeAddress = address + 1; break;
                case "width": producer.IntegerBitWidth = 32; break;
                case "unchecked": producer.CallSemantics = CallSemantics.Direct; break;
                case "capture-type": captured.Type = caller.AppContext.SystemTypes.SystemInt16Type; break;
                case "consumer-literal": consumer.SetOperand(2, new Immediate(0)); break;
                case "receiver-type-confusion": captured.Type = target.DeclaringType; break;
            }
            Reject(caller);
        }
        finally
        {
            producer.SetOperand(0, target);
            target.OverrideReturnType = returnType;
            target.OverrideAttributes = attributes;
            rawReturn.Byref = byref;
            rawReturn.NumMods = modifiers;
            producer.NativeAddress = address;
            producer.IntegerBitWidth = width;
            producer.CallSemantics = semantics;
            captured.Type = capturedType;
            consumer.SetOperand(2, argument);
        }
        Accept(caller);
    }

    [Test]
    public void ProviderCaptureCannotMoveAcrossTheProducerEffect()
    {
        var caller = Caller("Forward");
        Accept(caller);
        var graph = caller.ControlFlowGraph!;
        var capture = Capture(caller);
        var producer = Producer(caller);
        var original = graph.FindBlockByInstruction(capture)!;
        var position = original.Instructions.IndexOf(capture);
        var changed = graph.FindBlockByInstruction(producer)!;
        original.Instructions.RemoveAt(position);
        changed.Instructions.Insert(changed.Instructions.IndexOf(producer) + 1, capture);
        try { Reject(caller); }
        finally
        {
            changed.Instructions.Remove(capture);
            original.Instructions.Insert(position, capture);
        }
        Accept(caller);
    }

    [TestCase("return-data")]
    [TestCase("source-field")]
    [TestCase("source-offset")]
    [TestCase("source-width")]
    [TestCase("returned-base")]
    public void AdmittedProviderAndReturnedDeclarationsStayOriginal(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var producer = (MethodAnalysisContext)Producer(caller).Operands[0];
        var rawReturn = producer.Definition!.RawReturnType!;
        var data = rawReturn.Data.Dummy;
        var capture = Capture(caller);
        var access = (FieldReference)capture.Operands[1];
        var offset = access.Offset;
        var width = capture.IntegerBitWidth;
        var returned = producer.ReturnType;
        var parent = returned.Definition!.ParentIndex;
        try
        {
            switch (mutation)
            {
                case "return-data": rawReturn.Data.Dummy = producer.DeclaringType!.Definition!.RawType.Data.Dummy; break;
                case "source-field":
                    var replacement = caller.DeclaringType!.Fields.Single(field => field.Name == "Replacement");
                    capture.SetOperand(1, new FieldReference(replacement, access.Local, replacement.Offset));
                    break;
                case "source-offset": access.Offset++; break;
                case "source-width": capture.IntegerBitWidth = 32; break;
                case "returned-base": returned.Definition.ParentIndex = returned.Definition.ByvalTypeIndex; break;
            }
            Reject(caller);
        }
        finally
        {
            rawReturn.Data.Dummy = data;
            capture.SetOperand(1, access);
            access.Offset = offset;
            capture.IntegerBitWidth = width;
            returned.Definition.ParentIndex = parent;
        }
        Accept(caller);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AProviderCaptureCannotMoveToAnUnemittedSentinel(bool exit)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var graph = caller.ControlFlowGraph!;
        var capture = Capture(caller);
        var original = graph.FindBlockByInstruction(capture)!;
        var position = original.Instructions.IndexOf(capture);
        var sentinel = exit ? graph.ExitBlock : graph.EntryBlock;
        original.Instructions.RemoveAt(position);
        sentinel.Instructions.Add(capture);
        try { Reject(caller); }
        finally
        {
            sentinel.Instructions.Remove(capture);
            original.Instructions.Insert(position, capture);
        }
        Accept(caller);
    }

    [Test]
    public void AnExtraReturnedReferenceConsumerCannotEscapeItsBoundUse()
    {
        var caller = Caller("Forward");
        Accept(caller);
        var consumer = Consumer(caller);
        var result = (LocalVariable)Producer(caller).Destination!;
        var condition = new LocalVariable("extra-result-test", new Register(7903, "extra-result-test"),
            caller.AppContext.SystemTypes.SystemBooleanType);
        var extra = new Instruction(-1, OpCode.CheckEqual, condition, result, new Immediate(0))
        { NativeAddress = consumer.NativeAddress, IntegerBitWidth = 64 };
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(consumer)!;
        var position = block.Instructions.IndexOf(consumer);
        var incoming = IncomingBranches(caller, consumer);
        block.Instructions.Insert(position, extra);
        caller.Locals.Add(condition);
        foreach (var branch in incoming) branch.Instruction.SetOperand(0, extra);
        try { Reject(caller); }
        finally
        {
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, branch.Target);
            block.Instructions.Remove(extra);
            caller.Locals.Remove(condition);
        }
        Accept(caller);
    }

    [TestCase("reload")]
    [TestCase("clobber")]
    public void ReturnedReceiverCannotBeReloadedOrOverwritten(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var producer = Producer(caller);
        var consumer = Consumer(caller);
        var receiver = (LocalVariable)consumer.Operands[1];
        var provider = (LocalVariable)producer.Operands[2];
        var resultField = provider.Type!.Fields.Single(field => field.Name == "Result");
        var copy = new LocalVariable("returned-copy", new Register(7900, "returned-copy"), receiver.Type);
        var extra = mutation == "reload"
            ? new Instruction(-1, OpCode.Move, copy, new FieldReference(resultField, provider, resultField.Offset))
            : new Instruction(-1, OpCode.Move, receiver, new Immediate(0));
        extra.NativeAddress = producer.NativeAddress;
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(consumer)!;
        var position = block.Instructions.IndexOf(consumer);
        var incoming = IncomingBranches(caller, consumer);
        block.Instructions.Insert(position, extra);
        caller.Locals.Add(copy);
        foreach (var branch in incoming) branch.Instruction.SetOperand(0, extra);
        if (mutation == "reload") consumer.SetOperand(1, copy);
        try { Reject(caller); }
        finally
        {
            consumer.SetOperand(1, receiver);
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, branch.Target);
            caller.Locals.Remove(copy);
            block.Instructions.Remove(extra);
        }
        Accept(caller);
    }

    [TestCase("same")]
    [TestCase("provider-type")]
    [TestCase("branch-skips-copy")]
    public void PureResultCopiesKeepTheirTypeAndActuallyExecute(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var consumer = Consumer(caller);
        var original = consumer.Operands[1];
        var type = ((LocalVariable)Producer(caller).Destination!).Type!;
        var middle = new LocalVariable("result-middle", new Register(7901, "result-middle"),
            mutation == "provider-type" ? ((MethodAnalysisContext)Producer(caller).Operands[0]).DeclaringType : type);
        var copied = new LocalVariable("result-copy", new Register(7902, "result-copy"), type);
        var address = X64NativeInstructionReader.ReadRootBody(caller)!.Single(native =>
            native.Code == Iced.Intel.Code.Mov_r64_rm64 && native.Op0Register == Iced.Intel.Register.RCX &&
            native.Op1Register == Iced.Intel.Register.RAX).IP;
        var first = new Instruction(-1, OpCode.Move, middle, original) { NativeAddress = address };
        var second = new Instruction(-1, OpCode.Move, copied, middle) { NativeAddress = address };
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(consumer)!;
        var position = block.Instructions.IndexOf(consumer);
        var incoming = IncomingBranches(caller, consumer);
        Assert.That(position, Is.Zero);
        Assert.That(incoming, Is.Not.Empty, "Exercise the emitted success-arm instruction target.");
        block.Instructions.Insert(position, first);
        block.Instructions.Insert(position + 1, second);
        caller.Locals.AddRange([middle, copied]);
        if (mutation != "branch-skips-copy")
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, first);
        consumer.SetOperand(1, copied);
        try
        {
            if (mutation == "same") Accept(caller);
            else Reject(caller);
        }
        finally
        {
            consumer.SetOperand(1, original);
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, branch.Target);
            caller.Locals.Remove(middle);
            caller.Locals.Remove(copied);
            block.Instructions.Remove(second);
            block.Instructions.Remove(first);
        }
        Accept(caller);
    }

    [Test]
    public void RemovingTheProducerSiteCannotLeaveAValidConsumerSite()
    {
        var caller = Caller("Forward");
        Accept(caller);
        var producer = Producer(caller);
        var sites = (IList)caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
        var index = Enumerable.Range(0, sites.Count).Single(position => ReferenceEquals(
            sites[position]!.GetType().GetProperty("Invocation")!.GetValue(sites[position]), producer));
        var site = sites[index];
        sites.RemoveAt(index);
        try
        {
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
            Reject(caller);
        }
        finally { sites.Insert(index, site); }
        Accept(caller);
    }

    [Test]
    public void EarlierProducerEffectsCannotDisappear()
    {
        var caller = Caller("Forward");
        Accept(caller);
        var producer = Producer(caller);
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(producer)!;
        var position = block.Instructions.IndexOf(producer);
        block.Instructions.RemoveAt(position);
        try { Reject(caller); }
        finally { block.Instructions.Insert(position, producer); }
        Accept(caller);
    }

    private MethodAnalysisContext Caller(string name) => _callers.Single(method => method.Name == name);
    private static Instruction Producer(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction.IsCall && instruction.Operands[0] is MethodAnalysisContext { Name: "ReadTarget" });
    private static Instruction Consumer(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction.IsCall && instruction.Operands[0] is MethodAnalysisContext { Name: "SetFlag" });
    private static Instruction Capture(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference { Field.Name: "Source" }] });
    private static (Instruction Instruction, IOperand Target)[] IncomingBranches(MethodAnalysisContext caller, Instruction target) =>
        caller.ControlFlowGraph!.Instructions.Where(instruction =>
            instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump && instruction.Operands.Count > 0 &&
            ReferenceEquals(instruction.Operands[0], target)).Select(instruction =>
                (instruction, instruction.Operands[0])).ToArray();

    private static void Accept(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True, caller.Name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, caller.Name);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!));
    }

    private static void Reject(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False, caller.Name);
        Assert.That(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!),
            Throws.TypeOf<DecompilerException>());
    }
}
