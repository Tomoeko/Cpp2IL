using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ReferenceScalarFieldEffectsFixtureTests
{
    private MethodAnalysisContext _method = null!;
    private MethodDefinition _definition = null!;

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
        _method = app.GetAssemblyByName("NativeScalarFieldInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "ReplaceFields");
        _method.Analyze();
        _definition = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void AllReferenceAndScalarEffectsRetainTheirNativeOrderAndValues()
    {
        Assert.That(X64ReferenceScalarFieldEffectsProof.HasEvidence(_method), Is.True);
        Assert.That(X64ReferenceScalarFieldEffectsProof.IsValidFor(_method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_method, _definition));
        Assert.That(_definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld),
            Is.EqualTo(4));
        Assert.That(_definition.CilMethodBody.Instructions.Any(instruction => instruction.OpCode.FlowControl is
            CilFlowControl.Call), Is.False, "Managed reference storage supplies the GC barrier.");
        var block = _method.ControlFlowGraph!.Blocks.Single(candidate => candidate.Instructions.Any(instruction =>
            instruction.OpCode == OpCode.CheckEqual));
        var firstRead = block.Instructions[0];
        block.Instructions.RemoveAt(0);
        _method.ControlFlowGraph.EntryBlock.Instructions.Add(firstRead);
        try { Reject(); }
        finally { _method.ControlFlowGraph.EntryBlock.Instructions.Clear(); block.Instructions.Insert(0, firstRead); }
        var successor = block.Successors[0];
        block.Successors[0] = block;
        try { Reject(); }
        finally { block.Successors[0] = successor; }
        var comparison = block.Instructions.Single(instruction => instruction.OpCode == OpCode.CheckEqual);
        comparison.OpCode = OpCode.CheckNotEqual;
        try { Reject(); }
        finally { comparison.OpCode = OpCode.CheckEqual; }
        var increment = block.Instructions.Single(instruction => instruction.OpCode == OpCode.Add);
        var one = increment.Operands[2];
        increment.SetOperand(2, new Immediate(2));
        try { Reject(); }
        finally { increment.SetOperand(2, one); }
        var integerWrite = block.Instructions.Single(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [FieldReference access, _] } && access.Field.Name == "Value");
        var position = block.Instructions.IndexOf(integerWrite);
        block.Instructions.Remove(integerWrite);
        block.Instructions.Insert(block.Instructions.IndexOf(increment) + 1, integerWrite);
        try { Reject(); }
        finally { block.Instructions.Remove(integerWrite); block.Instructions.Insert(position, integerWrite); }
        var referenceRead = block.Instructions.First();
        var original = (FieldReference)referenceRead.Operands[1];
        var other = _method.DeclaringType!.Fields.Single(field => field.Name == "Target");
        referenceRead.SetOperand(1, new FieldReference(other, original.Local, other.Offset));
        try { Reject(); }
        finally { referenceRead.SetOperand(1, original); }
        Assert.That(X64ReferenceScalarFieldEffectsProof.IsValidFor(_method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_method, _definition));
    }

    [Test]
    public void CachedBytesLayoutAndAdmissionRemainNecessaryAfterLifting()
    {
        var bytes = _method.RawBytes;
        var changed = bytes.AsSpan().ToArray();
        changed[0] ^= 1;
        _method.RawBytes = new BinarySlice(changed);
        try { Reject(); }
        finally { _method.RawBytes = bytes; }
        var flag = _method.DeclaringType!.Fields.Single(field => field.Name == "Flag");
        flag.OverrideFieldType = _method.AppContext.SystemTypes.SystemByteType;
        try { Reject(); }
        finally { flag.OverrideFieldType = null; }
        var admitted = _method.GetExtraData<object>(X64ReferenceScalarFieldEffectsProof.EvidenceKey);
        _method.PutExtraData<object>(X64ReferenceScalarFieldEffectsProof.EvidenceKey, null!);
        try { Assert.That(X64ReferenceScalarFieldEffectsProof.HasEvidence(_method), Is.True); Reject(); }
        finally { _method.PutExtraData(X64ReferenceScalarFieldEffectsProof.EvidenceKey, admitted!); }
        Assert.That(X64ReferenceScalarFieldEffectsProof.IsValidFor(_method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_method, _definition));
    }

    private void Reject()
    {
        Assert.That(X64ReferenceScalarFieldEffectsProof.IsValidFor(_method), Is.False);
        Assert.That(() => IlGenerator.GenerateIl(_method, _definition), Throws.TypeOf<DecompilerException>());
    }
}
