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
public class X64NativeScalarProducerInvocationFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_PRODUCER_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_SCALAR_PRODUCER_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeScalarProducerInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Forward", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(2));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("Forward")]
    [TestCase("ForwardSnapshot")]
    public void CheckedProducerResultKeepsBothSitesAndTheCapturedConsumer(string name)
    {
        var caller = Caller(name);
        Accept(caller);
        var producer = Producer(caller);
        var consumer = Consumer(caller);
        Assert.That(producer.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(consumer.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(consumer.Operands[2], Is.SameAs(producer.Destination));
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        Assert.That(Array.IndexOf(operations, Capture(caller)), Is.LessThan(Array.IndexOf(operations, producer)));
        Assert.That(caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey), Has.Count.EqualTo(2));
        var body = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!.CilMethodBody!;
        Assert.That(body.Instructions.Count(operation => operation.OpCode == CilOpCodes.Callvirt), Is.EqualTo(2));
        Assert.That(body.Instructions.Count(operation => operation.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
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
    public void ConsumerCaptureCannotMoveAcrossTheProducerEffect()
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

    [TestCase("reload")]
    [TestCase("clobber")]
    [TestCase("arithmetic-use")]
    public void NewReadsAndResultWritesCannotChangeTheCapturedValues(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var consumer = Consumer(caller);
        var captured = (LocalVariable)Producer(caller).Destination!;
        var copy = new LocalVariable("changed-value", new Register(7900, "changed-value"),
            mutation == "reload" ? ((LocalVariable)consumer.Operands[1]).Type : captured.Type);
        var extra = mutation switch
        {
            "reload" => new Instruction(-1, OpCode.Move, copy, Capture(caller).Operands[1]),
            "clobber" => new Instruction(-1, OpCode.Move, captured, new Immediate(0)),
            _ => new Instruction(-1, OpCode.Add, copy, captured, new Immediate(1)) { IntegerBitWidth = 32 },
        };
        extra.NativeAddress = Capture(caller).NativeAddress;
        var receiver = consumer.Operands[1];
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
    [TestCase("narrow")]
    [TestCase("branch-skips-copy")]
    public void PureResultCopiesKeepTheirTypeAndActuallyExecute(string mutation)
    {
        var caller = Caller("Forward");
        Accept(caller);
        var consumer = Consumer(caller);
        var original = consumer.Operands[2];
        var type = caller.AppContext.SystemTypes.SystemInt32Type;
        var middle = new LocalVariable("result-middle", new Register(7901, "result-middle"),
            mutation == "narrow" ? caller.AppContext.SystemTypes.SystemInt16Type : type);
        var copied = new LocalVariable("result-copy", new Register(7902, "result-copy"), type);
        var address = X64NativeInstructionReader.ReadRootBody(caller)!.Single(native =>
            native.Code == Iced.Intel.Code.Mov_r32_rm32 && native.Op0Register == Iced.Intel.Register.EDX &&
            native.Op1Register == Iced.Intel.Register.EAX).IP;
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
        consumer.SetOperand(2, copied);
        try
        {
            if (mutation == "same") Accept(caller);
            else Reject(caller);
        }
        finally
        {
            consumer.SetOperand(2, original);
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
        instruction => instruction.IsCall && instruction.Operands[0] is MethodAnalysisContext { Name: "ReadValue" });
    private static Instruction Consumer(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction.IsCall && instruction.Operands[0] is MethodAnalysisContext { Name: "SetValue" });
    private static Instruction Capture(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference { Field.Name: "Target" }] });
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
