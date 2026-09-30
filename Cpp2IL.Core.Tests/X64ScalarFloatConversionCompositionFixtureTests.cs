using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarFloatConversionCompositionFixtureTests
{
    private ApplicationAnalysisContext _app = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FLOAT_CONVERSION_COMPOSITION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_FLOAT_CONVERSION_COMPOSITION_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("NarrowDivide")]
    [TestCase("NegativeToPositive")]
    [TestCase("InstanceDivide")]
    [TestCase("InstanceNegativeToPositive")]
    [TestCase("FieldDivide")]
    [TestCase("FieldNegativeToPositive")]
    [TestCase("AggregateNegativeToPositive")]
    public void CompleteCompositionRetainsOperationPrecisionInputsFieldAndFinalResult(string name)
    {
        var method = Method(name);
        Assert.That(X64ScalarFloatConversionCompositionProof.Find(method), Is.Not.Null);
        var cache = method.RawBytes;
        try
        {
            var changed = cache.ToArray();
            changed[0] ^= 1;
            method.RawBytes = new BinarySlice(changed);
            Assert.That(X64ScalarFloatConversionCompositionProof.Find(method), Is.Null);
        }
        finally { method.RawBytes = cache; }
        var interior = method.UnderlyingPointer + 1;
        try
        {
            _app.MethodsByAddress.Add(interior, [method]);
            Assert.That(X64ScalarFloatConversionCompositionProof.Find(method), Is.Null);
        }
        finally { _app.MethodsByAddress.Remove(interior); }
        method.Analyze();
        Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.True);
        var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
        var instructions = method.ControlFlowGraph!.Instructions;
        var conversion = instructions.Single(instruction => instruction.OpCode == OpCode.FloatConvert);
        var division = instructions.Single(instruction => instruction.OpCode == OpCode.FloatDivide);
        var returned = instructions.Single(instruction => instruction.OpCode == OpCode.Return);
        var numerator = division.Operands[1];
        try
        {
            division.SetOperand(1, division.Operands[2]);
            Rejected();
        }
        finally { division.SetOperand(1, numerator); }
        var divisor = division.Operands[2];
        try
        {
            division.SetOperand(2, new LocalVariable("unbound-divisor", new Register(null, "unbound"),
                _app.SystemTypes.SystemSingleType));
            Rejected();
        }
        finally { division.SetOperand(2, divisor); }
        try
        {
            division.OpCode = OpCode.Multiply;
            Rejected();
        }
        finally { division.OpCode = OpCode.FloatDivide; }
        var width = division.Operands[3];
        try
        {
            division.SetOperand(3, new Immediate(64));
            Rejected();
        }
        finally { division.SetOperand(3, width); }
        var quotient = (LocalVariable)division.Operands[0];
        var quotientType = quotient.Type;
        try
        {
            quotient.Type = _app.SystemTypes.SystemDoubleType;
            Rejected();
        }
        finally { quotient.Type = quotientType; }
        var originalReturn = returned.Operands[0];
        try
        {
            returned.SetOperand(0, conversion.Operands[0]);
            Rejected();
        }
        finally { returned.SetOperand(0, originalReturn); }
        var block = method.ControlFlowGraph.FindBlockByInstruction(division)!;
        var successor = block.Successors.Single();
        try
        {
            block.Successors.Clear();
            block.Successors.Add(block);
            Rejected();
        }
        finally { block.Successors.Clear(); block.Successors.Add(successor); }
        if (instructions.SingleOrDefault(instruction => instruction.OpCode == OpCode.Move) is { } capture)
        {
            var read = (FieldReference)capture.Operands[1];
            var owner = read.Local;
            try
            {
                capture.SetOperand(1, new FieldReference(read.Field,
                    new LocalVariable("other-owner", new Register(null, "rdx"), owner.Type), read.Offset));
                Rejected();
            }
            finally { capture.SetOperand(1, read); }
        }
        if (instructions.SingleOrDefault(instruction => instruction.OpCode == OpCode.FloatNegateNegative) is { } selection)
        {
            var selectedSource = selection.Operands[1];
            try
            {
                selection.SetOperand(1, conversion.Operands[0]);
                Rejected();
            }
            finally { selection.SetOperand(1, selectedSource); }
        }
        var proof = method.GetExtraData<X64ScalarFloatConversionCompositionProof.Proof>(
            X64ScalarFloatConversionCompositionProof.EvidenceKey)!;
        try
        {
            method.PutExtraData<object>(X64ScalarFloatConversionCompositionProof.EvidenceKey, null!);
            Assert.That(X64ScalarFloatConversionCompositionProof.HasEvidence(method), Is.True);
            Rejected();
        }
        finally { method.PutExtraData(X64ScalarFloatConversionCompositionProof.EvidenceKey, proof); }
        Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
        return;

        void Rejected()
        {
            Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.False);
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating conversion composition"));
        }
    }

    [Test]
    public void NativeSignMaskAndIgnoredAggregateLayoutRemainAuthenticated()
    {
        var method = Method("AggregateNegativeToPositive");
        var proof = X64ScalarFloatConversionCompositionProof.Find(method)!;
        Assert.That(proof, Is.Not.Null);
        Assert.That(TypeSizes.UnboxedSize(method.Parameters[0].ParameterType, 8), Is.EqualTo(1));
        Assert.That(_app.MethodsByAddress[method.UnderlyingPointer].Count, Is.GreaterThan(1),
            "the complete proof preserves each declaration even when the linker shares an identical body");
        method.Analyze();
        Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.True);
        var options = method.Parameters[0].ParameterType;
        var size = options.Definition!.RawSizes.instance_size;
        var pe = _app.Binary;
        var buffer = ((MemoryStream)pe.BaseStream).GetBuffer();
        var sizePointer = pe.TypeDefinitionSizePointers[options.Definition.TypeIndex.Value];
        var sizeOffset = checked((int)pe.MapVirtualAddressToRaw(sizePointer, false));
        var sizeBytes = buffer.AsSpan(sizeOffset, 4).ToArray();
        try
        {
            BitConverter.GetBytes(size + 8).CopyTo(buffer, sizeOffset);
            Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.False);
        }
        finally { sizeBytes.CopyTo(buffer, sizeOffset); }
        var raw = method.Parameters[0].Definition!.RawType!;
        var kind = raw.Type;
        try
        {
            raw.Type = LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_R4;
            Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.False);
        }
        finally { raw.Type = kind; }
        var maskOffset = pe.MapVirtualAddressToRaw(proof.Native.SignFlip!.Value.IPRelativeMemoryAddress, false);
        var original = buffer[checked((int)maskOffset)];
        try
        {
            buffer[checked((int)maskOffset)] ^= 1;
            Assert.That(X64ScalarFloatConversionCompositionProof.Find(method), Is.Null);
            Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.False);
        }
        finally { buffer[checked((int)maskOffset)] = original; }
        Assert.That(X64ScalarFloatConversionCompositionProof.IsValidFor(method), Is.True);
    }

    private MethodAnalysisContext Method(string name) => _app.GetAssemblyByName("ScalarFloatConversionCompositionFixture")!
        .Types.SelectMany(type => type.Methods).Single(method => method.Name == name);
}
