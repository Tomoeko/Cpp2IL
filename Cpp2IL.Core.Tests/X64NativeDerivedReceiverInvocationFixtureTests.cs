using System;
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
public class X64NativeDerivedReceiverInvocationFixtureTests
{
    private MethodAnalysisContext[] _callers = [];
    private TypeAnalysisContext _derived = null!;
    private TypeAnalysisContext _base = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_DERIVED_RECEIVER_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_DERIVED_RECEIVER_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        var assembly = app.GetAssemblyByName("NativeDerivedReceiverInvocationFixture")!;
        _derived = assembly.Types.Single(type => type.Name == "DerivedNode");
        _base = assembly.Types.Single(type => type.Name == "BaseNode");
        _callers = assembly.Types.Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Forward", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(2));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("ForwardInt", "SetInt")]
    [TestCase("ForwardFlag", "SetFlag")]
    public void InheritedScalarCallKeepsTheDerivedFieldAndExactNativeCapture(string name, string targetName)
    {
        var caller = Caller(name);
        var capture = Capture(caller);
        var access = (FieldReference)capture.Operands[1];
        var loaded = (LocalVariable)capture.Operands[0];
        var call = Call(caller);
        Assert.That(access.Field.FieldType, Is.SameAs(_derived));
        Assert.That(_derived.BaseType, Is.SameAs(_base));
        Assert.That(((MethodAnalysisContext)call.Operands[0]).DeclaringType, Is.SameAs(_base));
        Assert.That(((MethodAnalysisContext)call.Operands[0]).Name, Is.EqualTo(targetName));
        Assert.That(loaded.Type, Is.SameAs(_base), "Exercise the widened capture whose declared field remains derived.");
        Accept(caller);
        var body = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!.CilMethodBody!;
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(1));
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));

        var nativeAddress = capture.NativeAddress;
        var offset = access.Offset;
        var width = capture.IntegerBitWidth;
        capture.NativeAddress = nativeAddress + 1;
        try { Reject(caller); }
        finally { capture.NativeAddress = nativeAddress; }
        access.Offset++;
        try { Reject(caller); }
        finally { access.Offset = offset; }
        capture.IntegerBitWidth = 32;
        try { Reject(caller); }
        finally { capture.IntegerBitWidth = width; }
        var receiverIndex = call.OpCode == OpCode.Call ? 2 : 1;
        var receiver = call.Operands[receiverIndex];
        call.SetOperand(receiverIndex, access.Local);
        try { Reject(caller); }
        finally { call.SetOperand(receiverIndex, receiver); }
        var target = call.Operands[0];
        call.SetOperand(0, _base.Methods.Single(method => method.Name != targetName && method.Name.StartsWith("Set", StringComparison.Ordinal)));
        try { Reject(caller); }
        finally { call.SetOperand(0, target); }
        Accept(caller);
    }

    [TestCase("base-override")]
    [TestCase("raw-base-index")]
    [TestCase("raw-base-cycle")]
    [TestCase("raw-base-data")]
    [TestCase("raw-name-index")]
    [TestCase("namespace")]
    [TestCase("name")]
    [TestCase("interface")]
    [TestCase("field-type")]
    [TestCase("raw-field-type")]
    public void AdmittedAncestorAndFieldDeclarationsCannotBeRebasedOrRenamed(string mutation)
    {
        var caller = Caller("ForwardInt");
        var field = ((FieldReference)Capture(caller).Operands[1]).Field;
        var definition = _derived.Definition!;
        var rawBase = definition.RawBaseType!;
        var baseOverride = _derived.OverrideBaseType;
        var parentIndex = definition.ParentIndex;
        var baseData = rawBase.Data.Dummy;
        var nameIndex = definition.NameIndex;
        var name = _derived.OverrideName;
        var ns = _derived.OverrideNamespace;
        var attributes = _derived.OverrideAttributes;
        var fieldType = field.OverrideFieldType;
        var rawFieldType = field.BackingData!.Field.typeIndex;
        try
        {
            switch (mutation)
            {
                case "base-override": _derived.OverrideBaseType = caller.AppContext.SystemTypes.SystemObjectType; break;
                case "raw-base-index": definition.ParentIndex = _base.Definition!.ParentIndex; break;
                case "raw-base-cycle": definition.ParentIndex = definition.ByvalTypeIndex; break;
                case "raw-base-data": rawBase.Data.Dummy = caller.AppContext.SystemTypes.SystemObjectType.Definition!.RawType.Data.Dummy; break;
                case "raw-name-index": definition.NameIndex++; break;
                case "namespace": _derived.OverrideNamespace = "ChangedNamespace"; break;
                case "name": _derived.OverrideName = "ChangedDerivedNode"; break;
                case "interface": _derived.OverrideAttributes = _derived.Attributes | TypeAttributes.Interface; break;
                case "field-type": field.OverrideFieldType = _base; break;
                case "raw-field-type": field.BackingData.Field.typeIndex = _base.Definition!.ByvalTypeIndex; break;
            }
            Reject(caller);
        }
        finally
        {
            _derived.OverrideBaseType = baseOverride;
            definition.ParentIndex = parentIndex;
            rawBase.Data.Dummy = baseData;
            definition.NameIndex = nameIndex;
            _derived.OverrideName = name;
            _derived.OverrideNamespace = ns;
            _derived.OverrideAttributes = attributes;
            field.OverrideFieldType = fieldType;
            field.BackingData.Field.typeIndex = rawFieldType;
        }
        Accept(caller);
    }

    [TestCase("same")]
    [TestCase("downcast")]
    [TestCase("unrelated")]
    [TestCase("branch-skips-copy")]
    public void EveryReferenceCopyMustPreserveOrWidenTheCapturedType(string mutation)
    {
        var caller = Caller("ForwardInt");
        // Emission materializes block targets as their first instruction. A
        // valid inserted copy must become that target, so it actually executes.
        Accept(caller);
        var call = Call(caller);
        var receiverIndex = call.OpCode == OpCode.Call ? 2 : 1;
        var receiver = (LocalVariable)call.Operands[receiverIndex];
        var copy = new LocalVariable("receiver-copy", new Register(7400, "receiver-copy"),
            mutation == "downcast" ? _base : receiver.Type);
        var middle = new LocalVariable("receiver-copy-middle", new Register(7401, "receiver-copy-middle"),
            mutation == "downcast" ? _derived : mutation == "unrelated" ? caller.DeclaringType : receiver.Type);
        var copied = new LocalVariable("receiver-copy-result", new Register(7402, "receiver-copy-result"), receiver.Type);
        var first = new Instruction(-1, OpCode.Move, copy, receiver) { NativeAddress = call.NativeAddress };
        var second = new Instruction(-1, OpCode.Move, middle, copy) { NativeAddress = call.NativeAddress };
        var third = new Instruction(-1, OpCode.Move, copied, middle) { NativeAddress = call.NativeAddress };
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        var incoming = caller.ControlFlowGraph.Instructions.Where(instruction =>
            instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump && instruction.Operands.Count > 0 &&
            ReferenceEquals(instruction.Operands[0], call)).Select(instruction =>
                (Instruction: instruction, Target: instruction.Operands[0])).ToArray();
        Assert.That(position, Is.Zero, "Copies extend the authenticated success-arm entry.");
        Assert.That(incoming, Is.Not.Empty, "Exercise the instruction target materialized by emission.");
        block.Instructions.Insert(position, first);
        block.Instructions.Insert(position + 1, second);
        block.Instructions.Insert(position + 2, third);
        caller.Locals.AddRange([copy, middle, copied]);
        if (mutation != "branch-skips-copy")
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, first);
        call.SetOperand(receiverIndex, copied);
        try
        {
            if (mutation != "same") Reject(caller);
            else Accept(caller);
        }
        finally
        {
            call.SetOperand(receiverIndex, receiver);
            foreach (var branch in incoming) branch.Instruction.SetOperand(0, branch.Target);
            caller.Locals.Remove(copied);
            caller.Locals.Remove(middle);
            caller.Locals.Remove(copy);
            block.Instructions.Remove(third);
            block.Instructions.Remove(second);
            block.Instructions.Remove(first);
        }
        Accept(caller);
    }

    [Test]
    public void CapturedReferenceCannotGainAnUnevidencedArithmeticUse()
    {
        var caller = Caller("ForwardInt");
        var capture = Capture(caller);
        var value = (LocalVariable)capture.Operands[0];
        var extra = new Instruction(-1, OpCode.Add,
            new LocalVariable("invalid-reference-use", new Register(7403, "invalid-reference-use"), value.Type),
            value, new Immediate(1)) { NativeAddress = capture.NativeAddress };
        var block = caller.ControlFlowGraph!.FindBlockByInstruction(capture)!;
        block.Instructions.Insert(block.Instructions.IndexOf(capture) + 1, extra);
        try { Reject(caller); }
        finally { block.Instructions.Remove(extra); }
        Accept(caller);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeCaptureCannotMoveIntoAnUnemittedSentinelBlock(bool exit)
    {
        var caller = Caller("ForwardInt");
        var graph = caller.ControlFlowGraph!;
        var capture = Capture(caller);
        var block = graph.FindBlockByInstruction(capture)!;
        var position = block.Instructions.IndexOf(capture);
        block.Instructions.RemoveAt(position);
        var sentinel = exit ? graph.ExitBlock : graph.EntryBlock;
        sentinel.Instructions.Add(capture);
        try { Reject(caller); }
        finally
        {
            sentinel.Instructions.Remove(capture);
            block.Instructions.Insert(position, capture);
        }
        Accept(caller);
    }

    [Test]
    public void RemovingMutableSitesCannotRemoveNativeAdmission()
    {
        var caller = Caller("ForwardFlag");
        var sites = caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
        caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
        try
        {
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
            Reject(caller);
        }
        finally { caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, sites); }
        Accept(caller);
    }

    private MethodAnalysisContext Caller(string name) => _callers.Single(method => method.Name == name);

    private static Instruction Capture(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(
        instruction => instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference { Field.Name: "Target" }] });

    private static Instruction Call(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall);

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
