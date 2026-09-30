using System;
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
public class X64NativeReferenceFieldInvocationFixtureTests
{
    private MethodAnalysisContext _caller = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_REFERENCE_FIELD_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_REFERENCE_FIELD_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _caller = app.GetAssemblyByName("NativeReferenceFieldInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "Forward");
        _caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void CapturedReferenceArgumentRetainsItsDeclaredFieldAndNativeValue()
    {
        Accept();
        var capture = Capture();
        var argument = (LocalVariable)capture.Operands[0];
        var access = (FieldReference)capture.Operands[1];
        var call = Call();
        var target = (MethodAnalysisContext)call.Operands[0];
        Assert.That(call.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        Assert.That(call.Operands[ArgumentIndex(call)], Is.SameAs(argument));
        Assert.That(argument.Type, Is.SameAs(access.Field.FieldType));
        Assert.That(target.Parameters.Single().ParameterType, Is.SameAs(argument.Type));
        Assert.That(target.DeclaringType, Is.Not.SameAs(argument.Type), "The receiver and argument are distinct classes.");
        var body = Definition().CilMethodBody!;
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(2));
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
    }

    [TestCase("null")]
    [TestCase("owner")]
    [TestCase("capture-address")]
    [TestCase("capture-width")]
    [TestCase("capture-type")]
    [TestCase("access-offset")]
    [TestCase("access-owner")]
    [TestCase("field-offset")]
    [TestCase("field-type-index")]
    [TestCase("field-modifiers")]
    [TestCase("field-raw-data")]
    [TestCase("parameter-type")]
    [TestCase("parameter-byref")]
    [TestCase("parameter-raw-data")]
    [TestCase("type-name")]
    [TestCase("raw-type-name-index")]
    [TestCase("raw-base-index")]
    public void ArgumentCaptureAndImmutableDeclarationsRejectMutation(string mutation)
    {
        Accept();
        var capture = Capture();
        var call = Call();
        var index = ArgumentIndex(call);
        var originalArgument = call.Operands[index];
        var value = (LocalVariable)capture.Operands[0];
        var access = (FieldReference)capture.Operands[1];
        var field = access.Field;
        var type = field.FieldType;
        var parameter = ((MethodAnalysisContext)call.Operands[0]).Parameters.Single();
        Action restore;
        switch (mutation)
        {
            case "null":
                call.SetOperand(index, new Immediate(0));
                restore = () => call.SetOperand(index, originalArgument);
                break;
            case "owner":
                call.SetOperand(index, access.Local);
                restore = () => call.SetOperand(index, originalArgument);
                break;
            case "capture-address":
                var address = capture.NativeAddress;
                capture.NativeAddress = address + 1;
                restore = () => capture.NativeAddress = address;
                break;
            case "capture-width":
                var width = capture.IntegerBitWidth;
                capture.IntegerBitWidth = 32;
                restore = () => capture.IntegerBitWidth = width;
                break;
            case "capture-type":
                var localType = value.Type;
                value.Type = _caller.DeclaringType;
                restore = () => value.Type = localType;
                break;
            case "access-offset":
                var offset = access.Offset;
                access.Offset++;
                restore = () => access.Offset = offset;
                break;
            case "access-owner":
                var owner = access.Local;
                access.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
                restore = () => access.Local = owner;
                break;
            case "field-offset":
                var overrideOffset = field.OverrideOffset;
                field.Offset++;
                restore = () => field.OverrideOffset = overrideOffset;
                break;
            case "field-type-index":
                var definition = field.BackingData!.Field;
                var typeIndex = definition.typeIndex;
                definition.typeIndex = _caller.DeclaringType!.Fields.Single(candidate => candidate.Name == "Target")
                    .BackingData!.Field.typeIndex;
                restore = () => definition.typeIndex = typeIndex;
                break;
            case "field-modifiers":
                var rawType = field.BackingData!.Field.RawFieldType!;
                var modifiers = rawType.NumMods;
                rawType.NumMods++;
                restore = () => rawType.NumMods = modifiers;
                break;
            case "field-raw-data":
                var fieldRaw = field.BackingData!.Field.RawFieldType!;
                var fieldData = fieldRaw.Data;
                fieldRaw.Data = null!;
                restore = () => fieldRaw.Data = fieldData;
                break;
            case "parameter-type":
                var overrideType = parameter.OverrideParameterType;
                parameter.ParameterType = _caller.DeclaringType!;
                restore = () => parameter.OverrideParameterType = overrideType;
                break;
            case "parameter-byref":
                var rawParameter = parameter.Definition!.RawType!;
                var byref = rawParameter.Byref;
                rawParameter.Byref = 1;
                restore = () => rawParameter.Byref = byref;
                break;
            case "parameter-raw-data":
                var parameterRaw = parameter.Definition!.RawType!;
                var parameterData = parameterRaw.Data;
                parameterRaw.Data = null!;
                restore = () => parameterRaw.Data = parameterData;
                break;
            case "type-name":
                var overrideName = type.OverrideName;
                type.Name = "ChangedPayload";
                restore = () => type.OverrideName = overrideName;
                break;
            case "raw-type-name-index":
                var nameIndex = type.Definition!.NameIndex;
                type.Definition.NameIndex++;
                restore = () => type.Definition.NameIndex = nameIndex;
                break;
            case "raw-base-index":
                var parentIndex = type.Definition!.ParentIndex;
                type.Definition.ParentIndex = _caller.DeclaringType!.Definition!.ByvalTypeIndex;
                restore = () => type.Definition.ParentIndex = parentIndex;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        try { Reject(); }
        finally { restore(); }
        Accept();
    }

    [TestCase("remove")]
    [TestCase("duplicate")]
    [TestCase("after-call")]
    public void OwnerMutationBeforeTheCheckedCallCannotBeOmittedOrReordered(string mutation)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var store = graph.Instructions.Single(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [FieldReference field, _] } && field.Field.Name == "Flag");
        var owner = graph.FindBlockByInstruction(store)!;
        var position = owner.Instructions.IndexOf(store);
        var call = Call();
        var normal = graph.FindBlockByInstruction(call)!;
        if (mutation == "duplicate") owner.Instructions.Insert(position + 1, store);
        else
        {
            owner.Instructions.RemoveAt(position);
            if (mutation == "after-call") normal.Instructions.Insert(normal.Instructions.IndexOf(call) + 1, store);
        }
        try { Reject(); }
        finally
        {
            if (mutation == "duplicate") owner.Instructions.RemoveAt(position + 1);
            else
            {
                if (mutation == "after-call") normal.Instructions.Remove(store);
                owner.Instructions.Insert(position, store);
            }
        }
        Accept();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SyntheticSentinelsCannotHideTheArgumentCapture(bool exit)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var capture = Capture();
        var block = graph.FindBlockByInstruction(capture)!;
        var position = block.Instructions.IndexOf(capture);
        var sentinel = exit ? graph.ExitBlock : graph.EntryBlock;
        block.Instructions.RemoveAt(position);
        sentinel.Instructions.Add(capture);
        try { Reject(); }
        finally { sentinel.Instructions.Remove(capture); block.Instructions.Insert(position, capture); }
        Accept();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PureReferenceCopiesPreserveTheCapturedClass(bool changeType)
    {
        Accept();
        var graph = _caller.ControlFlowGraph!;
        var call = Call();
        var index = ArgumentIndex(call);
        var source = (LocalVariable)call.Operands[index];
        var copy = new LocalVariable("reference-copy", new Register(7540, "reference-copy"),
            changeType ? _caller.DeclaringType : source.Type);
        var operation = new Instruction(-1, OpCode.Move, copy, source) { NativeAddress = call.NativeAddress };
        var block = graph.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        var incoming = graph.Instructions.Where(instruction => instruction.OpCode == OpCode.Jump &&
            instruction.Operands.Count == 1 && ReferenceEquals(instruction.Operands[0], call)).ToArray();
        block.Instructions.Insert(position, operation);
        _caller.Locals.Add(copy);
        foreach (var jump in incoming) jump.SetOperand(0, operation);
        call.SetOperand(index, copy);
        try
        {
            if (changeType) Reject();
            else Accept();
        }
        finally
        {
            call.SetOperand(index, source);
            foreach (var jump in incoming) jump.SetOperand(0, call);
            block.Instructions.Remove(operation);
            _caller.Locals.Remove(copy);
        }
        Accept();
    }

    [Test]
    public void ExtraReferenceConsumerRequiresItsOwnEvidence()
    {
        Accept();
        var capture = Capture();
        var value = (LocalVariable)capture.Operands[0];
        var destination = new LocalVariable("extra-test", new Register(7541, "extra-test"),
            _caller.AppContext.SystemTypes.SystemBooleanType);
        var comparison = new Instruction(-1, OpCode.CheckEqual, destination, value, new Immediate(0))
            { NativeAddress = capture.NativeAddress, IntegerBitWidth = 64 };
        var block = _caller.ControlFlowGraph!.FindBlockByInstruction(capture)!;
        var position = block.Instructions.IndexOf(capture);
        block.Instructions.Insert(position + 1, comparison);
        try { Reject(); }
        finally { block.Instructions.Remove(comparison); }
        Accept();
    }

    [Test]
    public void ImmutableAdmissionRejectsMissingMutableSite()
    {
        Accept();
        var sites = _caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
        _caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
        try
        {
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(_caller), Is.True);
            Reject();
        }
        finally { _caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, sites); }
        Accept();
    }

    private Instruction Capture() => _caller.ControlFlowGraph!.Instructions.Single(instruction => instruction is
        { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference field] } && field.Field.Name == "Source");
    private Instruction Call() => _caller.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall &&
        instruction.Operands[0] is MethodAnalysisContext { Name: "Accept" });
    private static int ArgumentIndex(Instruction call) => call.OpCode == OpCode.Call ? 3 : 2;
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
