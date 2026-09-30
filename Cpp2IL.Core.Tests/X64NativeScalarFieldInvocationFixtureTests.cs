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
public class X64NativeScalarFieldInvocationFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_FIELD_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_SCALAR_FIELD_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeScalarFieldInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(7));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("SetIntegerField", 1)]
    [TestCase("SetBooleanField", 1)]
    [TestCase("SetFieldPair", 2)]
    [TestCase("SetFieldReverse", 2)]
    [TestCase("SetFieldPairReturning", 2)]
    [TestCase("SetCapturedBeforeEffect", 2)]
    [TestCase("SetCapturedAfterEffect", 2)]
    public void CapturedFieldsRetainTheirNativeLoadTypeAndOwner(string name, int fieldCount)
    {
        var caller = _callers.Single(method => method.Name == name);
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True, name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, name);
        var graph = caller.ControlFlowGraph!;
        var call = graph.Instructions.Single(instruction => instruction.IsCall &&
            instruction.Operands[0] is MethodAnalysisContext { Parameters.Count: 2 });
        var argumentStart = call.OpCode == OpCode.Call ? 3 : 2;
        var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
        Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt),
            Is.EqualTo(1));
        var captures = graph.Instructions.Where(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference access] } &&
            access.Field.Name is "Value" or "Flag").ToArray();
        Assert.That(captures, Has.Length.EqualTo(fieldCount), name);
        foreach (var capture in captures)
        {
            var value = (LocalVariable)capture.Operands[0];
            var access = (FieldReference)capture.Operands[1];
            Assert.That(call.Operands.Skip(argumentStart).Take(2), Does.Contain(value), "Use the single captured value.");
            var field = access.Field;
            var fieldOffset = access.Offset;
            var owner = access.Local;
            var nativeAddress = capture.NativeAddress;
            var type = value.Type;
            var bitWidth = capture.IntegerBitWidth;
            var scalarIndex = Enumerable.Range(argumentStart, 2).Single(index => ReferenceEquals(call.Operands[index], value));
            call.SetOperand(scalarIndex, new Immediate(0));
            try { Reject(caller); }
            finally { call.SetOperand(scalarIndex, value); }

            access.Field = caller.DeclaringType!.Fields.Single(candidate => candidate.Name == "Neighbor");
            access.Offset = (int)access.Field.Offset;
            try { Reject(caller); }
            finally { access.Field = field; access.Offset = fieldOffset; }

            access.Offset++;
            try { Reject(caller); }
            finally { access.Offset = fieldOffset; }

            access.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
            try { Reject(caller); }
            finally { access.Local = owner; }

            capture.NativeAddress = nativeAddress + 1;
            try { Reject(caller); }
            finally { capture.NativeAddress = nativeAddress; }

            value.Type = caller.AppContext.SystemTypes.SystemInt16Type;
            try { Reject(caller); }
            finally { value.Type = type; }

            capture.IntegerBitWidth = 16;
            try { Reject(caller); }
            finally { capture.IntegerBitWidth = bitWidth; }

            var block = graph.FindBlockByInstruction(capture)!;
            var position = block.Instructions.IndexOf(capture);
            block.Instructions.RemoveAt(position);
            try { Reject(caller); }
            finally { block.Instructions.Insert(position, capture); }

            var extra = new Instruction(-1, OpCode.Add,
                new LocalVariable("injected-use", new Register(7300, "injected-use"), type), value, new Immediate(1))
                { IntegerBitWidth = 32, NativeAddress = nativeAddress };
            block.Instructions.Insert(position + 1, extra);
            try { Reject(caller); }
            finally { block.Instructions.Remove(extra); }
        }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
    }

    [TestCase("SetCapturedBeforeEffect", true)]
    [TestCase("SetCapturedAfterEffect", false)]
    public void FieldCapturesKeepTheirOrderAroundTheMutationCall(string name, bool before)
    {
        var caller = _callers.Single(method => method.Name == name);
        var graph = caller.ControlFlowGraph!;
        var ordered = graph.Instructions.ToArray();
        var effect = ordered.Single(instruction => instruction.IsCall &&
            instruction.Operands[0] is MethodAnalysisContext { Name: "ReplaceFields" });
        var captures = ordered.Where(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference access] } &&
            access.Field.Name is "Value" or "Flag").ToArray();
        Assert.That(captures, Has.Length.EqualTo(2));
        foreach (var capture in captures)
            Assert.That(Array.IndexOf(ordered, capture) < Array.IndexOf(ordered, effect), Is.EqualTo(before));
        var effectBlock = graph.FindBlockByInstruction(effect)!;
        var captureBlock = graph.FindBlockByInstruction(captures[0])!;
        var position = captureBlock.Instructions.IndexOf(captures[0]);
        captureBlock.Instructions.RemoveAt(position);
        var changedPosition = effectBlock.Instructions.IndexOf(effect) + (before ? 1 : 0);
        effectBlock.Instructions.Insert(changedPosition, captures[0]);
        try { Reject(caller); }
        finally { effectBlock.Instructions.Remove(captures[0]); captureBlock.Instructions.Insert(position, captures[0]); }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    [Test]
    public void BooleanMemoryLoadAdmissionSurvivesMissingMutableEvidence()
    {
        var caller = _callers.Single(method => method.Name == "SetFieldPair");
        var evidence = caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.BooleanFieldArgumentEvidenceKey)!;
        Assert.That(evidence, Is.Not.Null);
        caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.BooleanFieldArgumentEvidenceKey, null!);
        try
        {
            Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
            Reject(caller);
        }
        finally { caller.PutExtraData(X64NativeNullCheckedInvocationProof.BooleanFieldArgumentEvidenceKey, evidence); }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CapturedIntegerCopiesPreserveEveryManagedWidth(bool narrow)
    {
        var caller = _callers.Single(method => method.Name == "SetIntegerField");
        var graph = caller.ControlFlowGraph!;
        var call = graph.Instructions.Single(instruction => instruction.IsCall &&
            instruction.Operands[0] is MethodAnalysisContext { Parameters.Count: 2 });
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        var value = (LocalVariable)call.Operands[argumentIndex];
        var first = new LocalVariable("first-copy", new Register(7310, "first-copy"), narrow
            ? caller.AppContext.SystemTypes.SystemInt16Type : caller.AppContext.SystemTypes.SystemInt32Type);
        var second = new LocalVariable("second-copy", new Register(7311, "second-copy"),
            caller.AppContext.SystemTypes.SystemInt32Type);
        var firstCopy = new Instruction(-1, OpCode.Move, first, value) { NativeAddress = call.NativeAddress };
        var secondCopy = new Instruction(-1, OpCode.Move, second, first) { NativeAddress = call.NativeAddress };
        var block = graph.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        block.Instructions.Insert(position, firstCopy);
        block.Instructions.Insert(position + 1, secondCopy);
        call.SetOperand(argumentIndex, second);
        try
        {
            if (narrow) Reject(caller);
            else
            {
                Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!));
            }
        }
        finally
        {
            call.SetOperand(argumentIndex, value);
            block.Instructions.Remove(secondCopy);
            block.Instructions.Remove(firstCopy);
        }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    private static void Reject(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False, caller.Name);
        Assert.That(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!),
            Throws.TypeOf<DecompilerException>());
    }
}
