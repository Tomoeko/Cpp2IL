using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional input-only checks; successful IL emission is not a Unity behavioral oracle.</summary>
[NonParallelizable]
public class X64ScalarLaneCopyFixtureTests
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var binary = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_LANE_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_LANE_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Set both scalar-lane input paths to an original exact-target player.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>(),
            "Freshness mutations must only touch the loaded in-memory image.");
        _methods = _app.Assemblies.SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.UnderlyingPointer != 0 && method.IsStatic && method.Parameters.Count <= 4 &&
                (ReferenceEquals(method.ReturnType, _app.SystemTypes.SystemSingleType) ||
                 ReferenceEquals(method.ReturnType, _app.SystemTypes.SystemDoubleType)))
            .Where(method => X64ScalarLaneDemandProof.Find(method) is { } proof &&
                proof.Shape.Projections.ToArray().Any(site => !site.IsZero) &&
                // Double arithmetic remains outside the existing general lifter.
                proof.Body.ToArray().All(native => native.Mnemonic is not
                    (Iced.Intel.Mnemonic.Addsd or Iced.Intel.Mnemonic.Subsd or
                     Iced.Intel.Mnemonic.Mulsd or Iced.Intel.Mnemonic.Divsd))).ToArray();
        Assert.That(_methods, Is.Not.Empty, "The player must supply complete scalar-lane transport bodies.");
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _methods) method.Analyze();
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void ActualAnalysisAndIlKeepCopiesTypedAndPreserveArithmeticCoercions()
    {
        foreach (var method in _methods)
        {
            Assert.That(X64ScalarLaneDemandProof.HasEvidence(method), Is.True);
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.DoesNotThrow(() => IlGenerator.ValidateScalarLaneCopies(method));
            var proof = method.GetExtraData<X64ScalarLaneDemandProof.Evidence>(X64ScalarLaneDemandProof.EvidenceKey)!;
            var lifted = _app.InstructionSet.GetIsilFromMethod(method);
            foreach (var site in proof.Shape.Projections)
            {
                var copy = lifted.Single(instruction => instruction.NativeAddress == site.Address);
                Assert.That(copy.OpCode, Is.EqualTo(OpCode.Move),
                    "Native bit copies must not become rounding FloatProject operations.");
                Assert.That(copy.IntegerBitWidth, Is.Zero);
            }
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            IlGenerator.GenerateIl(method, output);
            Assert.That(output.CilMethodBody!.Instructions.Last().OpCode, Is.EqualTo(CilOpCodes.Ret));
            var arithmetic = method.ControlFlowGraph!.Instructions.Where(instruction =>
                instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide).ToArray();
            foreach (var width in new[] { 32, 64 })
            {
                var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
                var conversion = width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8;
                var operations = arithmetic.Count(instruction =>
                    instruction.Operands[0] is LocalVariable local && ReferenceEquals(local.Type, type));
                Assert.That(output.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == conversion),
                    Is.EqualTo(operations * 2),
                    "Preserve the established arithmetic operand coercions without adding a copy conversion.");
            }
        }
    }

    [TestCase("parameter-attributes")]
    [TestCase("parameter-data")]
    [TestCase("return-data")]
    public void CoherentOriginalDescriptorChangesStillInvalidateTheSavedBinding(string defect)
    {
        var method = _methods.First(item => item.Parameters.Count != 0);
        var saved = method.GetExtraData<X64ScalarLaneDemandProof.Evidence>(X64ScalarLaneDemandProof.EvidenceKey)!;
        var raw = defect == "return-data" ? method.Definition!.RawReturnType! : method.Parameters[0].Definition!.RawType!;
        Assert.That(_app.Binary.TryGetTypeVirtualAddress(raw, out var address), Is.True);
        var pe = (PE)_app.Binary;
        var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
        var originalBytes = pe.GetRawBinaryContent().Slice(offset, 12).ToArray();
        var originalBits = raw.Bits;
        var originalAttributes = raw.Attrs;
        var originalData = raw.Datapoint;
        var originalUnion = raw.Data.Dummy;
        var position = pe.BaseStream.Position;
        try
        {
            if (defect == "parameter-attributes")
            {
                raw.Bits ^= (uint)ParameterAttributes.Optional;
                raw.Attrs = raw.Bits & 0xFFFF;
                pe.BaseStream.Position = offset + 8;
                pe.BaseStream.Write(BitConverter.GetBytes(raw.Bits));
            }
            else
            {
                raw.Datapoint ^= 1;
                raw.Data.Dummy = raw.Datapoint;
                pe.BaseStream.Position = offset;
                pe.BaseStream.Write(BitConverter.GetBytes(raw.Datapoint));
            }
            Assert.That(X64ScalarLaneDemandProof.Find(method), Is.Not.Null,
                "This control preserves current descriptor coherence and scalar eligibility.");
            Assert.That(saved.IsUnchanged(), Is.False,
                "A new coherent descriptor value must not replace the saved consumed binding.");
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateScalarLaneCopies(method));
        }
        finally
        {
            raw.Bits = originalBits;
            raw.Attrs = originalAttributes;
            raw.Datapoint = originalData;
            raw.Data.Dummy = originalUnion;
            pe.BaseStream.Position = offset;
            pe.BaseStream.Write(originalBytes);
            pe.BaseStream.Position = position;
        }
        Assert.That(saved.IsUnchanged(), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.ValidateScalarLaneCopies(method));
    }

    [TestCase("alias-duplicate")]
    [TestCase("alias-removed")]
    [TestCase("parameter-type")]
    [TestCase("return-type")]
    [TestCase("cached-body")]
    [TestCase("file-backed-body")]
    [TestCase("evidence-removed")]
    public void ChangedOriginalAbiAliasesOrBytesCannotReuseSavedProof(string defect)
    {
        var method = _methods.First(item => item.Parameters.Count != 0);
        var undo = new Stack<Action>();
        try
        {
            void Assign<T>(Func<T> read, Action<T> write, T value)
            {
                var previous = read();
                undo.Push(() => write(previous));
                write(value);
            }
            var aliases = _app.MethodsByAddress[method.UnderlyingPointer];
            switch (defect)
            {
                case "alias-duplicate": undo.Push(() => aliases.RemoveAt(aliases.Count - 1)); aliases.Add(method); break;
                case "alias-removed":
                    var ordinal = aliases.IndexOf(method);
                    undo.Push(() => aliases.Insert(ordinal, method)); aliases.RemoveAt(ordinal); break;
                case "parameter-type":
                    Assign(() => method.Parameters[0].OverrideParameterType,
                        value => method.Parameters[0].OverrideParameterType = value, _app.SystemTypes.SystemInt32Type); break;
                case "return-type":
                    Assign(() => method.OverrideReturnType, value => method.OverrideReturnType = value, _app.SystemTypes.SystemInt32Type); break;
                case "cached-body":
                    var altered = method.RawBytes.AsSpan().ToArray();
                    altered[0] ^= 1;
                    Assign(() => method.RawBytes, value => method.RawBytes = value, new BinarySlice(altered)); break;
                case "file-backed-body":
                    var pe = (PE)_app.Binary;
                    var offset = checked((int)pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false));
                    var original = pe.GetRawBinaryContent()[offset];
                    void WriteByte(byte value)
                    {
                        var position = pe.BaseStream.Position;
                        try { pe.BaseStream.Position = offset; pe.BaseStream.WriteByte(value); }
                        finally { pe.BaseStream.Position = position; }
                    }
                    undo.Push(() => WriteByte(original));
                    WriteByte((byte)(original ^ 1)); break;
                case "evidence-removed":
                    Assign(() => method.GetExtraData<X64ScalarLaneDemandProof.Evidence>(X64ScalarLaneDemandProof.EvidenceKey),
                        value => method.PutExtraData(X64ScalarLaneDemandProof.EvidenceKey, value!), null); break;
            }
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateScalarLaneCopies(method));
        }
        finally { Restore(undo); }
        Assert.DoesNotThrow(() => IlGenerator.ValidateScalarLaneCopies(method));
    }

    [TestCase("operand")]
    [TestCase("operation")]
    [TestCase("native-site")]
    [TestCase("precision")]
    [TestCase("integer-width")]
    [TestCase("return-value")]
    [TestCase("extra-effect")]
    public void FinalTypedDataFlowAndOrderedArithmeticRemainMandatory(string defect)
    {
        var method = _methods.First(item => item.ControlFlowGraph!.Instructions.Any(instruction =>
            instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide));
        var arithmetic = method.ControlFlowGraph!.Instructions.First(instruction =>
            instruction.OpCode is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide);
        var undo = new Stack<Action>();
        try
        {
            void Assign<T>(Func<T> read, Action<T> write, T value)
            {
                var previous = read();
                undo.Push(() => write(previous));
                write(value);
            }
            switch (defect)
            {
                case "operand":
                    Assign(() => arithmetic.Operands[1], value => arithmetic.SetOperand(1, value), new FloatLiteral(-0f)); break;
                case "operation":
                    Assign(() => arithmetic.OpCode, value => arithmetic.OpCode = value,
                        arithmetic.OpCode == OpCode.Add ? OpCode.Subtract : OpCode.Add); break;
                case "native-site": Assign(() => arithmetic.NativeAddress, value => arithmetic.NativeAddress = value, 1UL); break;
                case "precision":
                    var output = (LocalVariable)arithmetic.Operands[0];
                    Assign(() => output.Type, value => output.Type = value, _app.SystemTypes.SystemInt64Type); break;
                case "integer-width": Assign(() => arithmetic.IntegerBitWidth, value => arithmetic.IntegerBitWidth = value, 32); break;
                case "return-value":
                    var returned = method.ControlFlowGraph.Instructions.Last();
                    Assign(() => returned.Operands[0], value => returned.SetOperand(0, value), new FloatLiteral(-0f)); break;
                case "extra-effect":
                    var block = method.ControlFlowGraph.Blocks.Single(item => item.Instructions.Contains(arithmetic));
                    var injected = new Instruction(-1, OpCode.Interrupt);
                    undo.Push(() => block.Instructions.Remove(injected));
                    block.Instructions.Insert(0, injected); break;
            }
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateScalarLaneCopies(method));
        }
        finally { Restore(undo); }
        Assert.DoesNotThrow(() => IlGenerator.ValidateScalarLaneCopies(method));
    }

    private static void Restore(Stack<Action> undo)
    {
        var errors = new List<Exception>();
        while (undo.Count != 0)
        {
            try { undo.Pop()(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
}
