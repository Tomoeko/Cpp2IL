using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Utf8String = AsmResolver.Utf8String;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Utils.AsmResolver;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional original player checks; IL emission alone is not Unity behavior verification.</summary>
[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
public class X64ScalarDoubleLeafFixtureTests
{
    private readonly bool _syntheticFixture;
    public X64ScalarDoubleLeafFixtureTests(bool syntheticFixture) => _syntheticFixture = syntheticFixture;

    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_DOUBLE_LEAF_FIXTURE_INPUT");
        var binary = _syntheticFixture && !string.IsNullOrEmpty(input) ? Path.Combine(input, "GameAssembly.dll") :
            _syntheticFixture ? null : Environment.GetEnvironmentVariable("CPP2IL_SCALAR_DOUBLE_LEAF_BINARY");
        var metadata = _syntheticFixture && !string.IsNullOrEmpty(input) ?
            Path.Combine(input, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat") :
            _syntheticFixture ? null : Environment.GetEnvironmentVariable("CPP2IL_SCALAR_DOUBLE_LEAF_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore(_syntheticFixture ? "Set CPP2IL_NATIVE_SCALAR_DOUBLE_LEAF_FIXTURE_INPUT to the neutral exact player input." :
                "Set both Double leaf input paths to an original exact-target player.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>(),
            "Native freshness controls must only touch the loaded in-memory image.");
        if (_syntheticFixture)
        {
            var assembly = _app.Assemblies.Single(candidate => candidate.Name == "NativeScalarDoubleLeafFixture");
            var types = assembly.Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(types.Length, Is.EqualTo(2));
            Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(7));
            Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(4));
            Assert.That(types.SelectMany(type => type.Methods).Count(method => method.Name == ".cctor"), Is.EqualTo(2),
                "The complete declaration denominator retains both original initializer bodies.");
        }
        _methods = _app.Assemblies.Where(assembly => !_syntheticFixture || assembly.Name == "NativeScalarDoubleLeafFixture")
            .SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.UnderlyingPointer != 0 && ReferenceEquals(method.ReturnType, _app.SystemTypes.SystemDoubleType))
            .Where(method => X64ScalarDoubleLeafProof.Find(method) != null).ToArray();
        Assert.That(_methods, Is.Not.Empty);
        Assert.That(_methods.Select(method => X64ScalarDoubleLeafProof.Find(method)!.Native.Operation).Distinct().Count(), Is.EqualTo(2),
            "This control input must contain both complete Double leaf families.");
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _methods) method.Analyze();
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    private MethodAnalysisContext Method(X64ScalarDoubleLeafProof.Kind operation) =>
        _methods.First(method => method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)?.Native.Operation == operation);

    [Test]
    public void ActualAnalysisAndIlPreserveOrderedReadsSignedConversionAndOperationRounding()
    {
        foreach (var method in _methods)
        {
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
            var proof = method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)!;
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            IlGenerator.GenerateIl(method, output);
            var code = output.CilMethodBody!.Instructions;
            Assert.That(code.Last().OpCode, Is.EqualTo(CilOpCodes.Ret));
            Assert.That(code.Any(site => site.OpCode == CilOpCodes.Conv_R_Un), Is.False);
            if (proof.Native.Operation == X64ScalarDoubleLeafProof.Kind.FieldSum)
            {
                var fields = code.Where(site => site.OpCode == CilOpCodes.Ldfld).Select(site => site.Operand).ToArray();
                Assert.That(fields, Is.EqualTo(new object[]
                {
                    proof.FirstField!.GetExtraData<FieldDefinition>("AsmResolverField")!,
                    proof.SecondField!.GetExtraData<FieldDefinition>("AsmResolverField")!
                }));
                Assert.That(code.Count(site => site.OpCode == CilOpCodes.Conv_R8), Is.EqualTo(3));
                var add = code.ToList().FindIndex(site => site.OpCode == CilOpCodes.Add);
                Assert.That(code[add + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R8));
            }
            else
            {
                Assert.That(code.Count(site => site.OpCode == CilOpCodes.Conv_I8), Is.EqualTo(1));
                Assert.That(code.Count(site => site.OpCode == CilOpCodes.Conv_R8), Is.EqualTo(4));
                var conversion = code.ToList().FindIndex(site => site.OpCode == CilOpCodes.Conv_I8);
                Assert.That(code[conversion + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R8));
                var multiply = code.ToList().FindIndex(site => site.OpCode == CilOpCodes.Mul);
                Assert.That(code[multiply + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R8));
            }
        }
    }

    [TestCase("alias-duplicate")]
    [TestCase("alias-removed")]
    [TestCase("generic-registered-alias")]
    [TestCase("parameter-type")]
    [TestCase("return-type")]
    [TestCase("owner-name")]
    [TestCase("cached-body")]
    [TestCase("native-body")]
    [TestCase("readonly-coefficient")]
    [TestCase("evidence-removed")]
    public void ChangedOriginalSignatureAliasBodyOrLiteralCannotReuseAdmission(string defect)
    {
        var method = Method(X64ScalarDoubleLeafProof.Kind.SignedParameterScale);
        var undo = new Stack<Action>();
        try
        {
            var aliases = _app.MethodsByAddress[method.UnderlyingPointer];
            switch (defect)
            {
                case "alias-duplicate": undo.Push(() => aliases.RemoveAt(aliases.Count - 1)); aliases.Add(method); break;
                case "alias-removed":
                    var ordinal = aliases.IndexOf(method);
                    undo.Push(() => aliases.Insert(ordinal, method)); aliases.RemoveAt(ordinal);
                    Assert.That(X64ScalarDoubleLeafProof.Find(method), Is.Null,
                        "A missing original alias must also reject a fresh capture."); break;
                case "generic-registered-alias":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var registration), Is.True);
                    Assert.That(registration.MethodPointerCount, Is.GreaterThan(0));
                    Assert.That(_app.ConcreteGenericMethodsByRef.Values.Any(alias => alias.UnderlyingPointer == method.UnderlyingPointer), Is.False);
                    WriteNative(undo, registration.MethodPointersAddress, BitConverter.GetBytes(method.UnderlyingPointer));
                    Assert.That(X64ScalarDoubleLeafProof.Find(method), Is.Null,
                        "An original generic pointer table entry cannot be hidden by an absent mutable concrete-method alias.");
                    break;
                case "parameter-type": Change(undo, () => method.Parameters[0].OverrideParameterType,
                    value => method.Parameters[0].OverrideParameterType = value, _app.SystemTypes.SystemUInt64Type); break;
                case "return-type": Change(undo, () => method.OverrideReturnType,
                    value => method.OverrideReturnType = value, _app.SystemTypes.SystemSingleType); break;
                case "owner-name": Change(undo, () => method.DeclaringType!.OverrideName,
                    value => method.DeclaringType!.OverrideName = value, "ChangedOwner"); break;
                case "cached-body":
                    var changed = method.RawBytes.AsSpan().ToArray(); changed[0] ^= 1;
                    Change(undo, () => method.RawBytes, value => method.RawBytes = value, new BinarySlice(changed)); break;
                case "native-body": MutateNative(undo, method.UnderlyingPointer, 1); break;
                case "readonly-coefficient":
                    var proof = method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)!;
                    MutateNative(undo, proof.Native.Arithmetic.IPRelativeMemoryAddress, 1); break;
                case "evidence-removed": Change(undo,
                    () => method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey),
                    value => method.PutExtraData(X64ScalarDoubleLeafProof.EvidenceKey, value!), null); break;
            }
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [Test]
    public void AnOriginalFoldedAliasCannotBeRemovedBeforeFreshCapture()
    {
        var method = _methods.FirstOrDefault(candidate => _app.MethodsByAddress[candidate.UnderlyingPointer].Count > 1);
        if (method == null) Assert.Ignore("This optional original player has no admitted folded ordinary alias.");
        var bindings = _app.MethodsByAddress[method!.UnderlyingPointer];
        var alias = bindings.First(candidate => !ReferenceEquals(candidate, method));
        var ordinal = bindings.IndexOf(alias);
        try
        {
            bindings.RemoveAt(ordinal);
            Assert.That(bindings.Contains(method), Is.True);
            Assert.That(X64ScalarDoubleLeafProof.Find(method), Is.Null);
            Reject(method);
        }
        finally { bindings.Insert(ordinal, alias); }
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [TestCase("parameter-attributes")]
    [TestCase("parameter-data")]
    [TestCase("return-data")]
    [TestCase("field-data")]
    public void CoherentConsumedDescriptorChangesRejectSavedProofEvenWhenFreshCaptureRemainsEligible(string defect)
    {
        var method = Method(defect == "field-data" ? X64ScalarDoubleLeafProof.Kind.FieldSum : X64ScalarDoubleLeafProof.Kind.SignedParameterScale);
        var proof = method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)!;
        var raw = defect == "field-data" ? proof.FirstField!.BackingData!.Field.RawFieldType! :
            defect == "return-data" ? method.Definition!.RawReturnType! : method.Parameters[0].Definition!.RawType!;
        Assert.That(_app.Binary.TryGetTypeVirtualAddress(raw, out var address), Is.True);
        var bits = raw.Bits; var attributes = raw.Attrs; var data = raw.Datapoint; var union = raw.Data.Dummy;
        var undo = new Stack<Action>();
        undo.Push(() => { raw.Bits = bits; raw.Attrs = attributes; raw.Datapoint = data; raw.Data.Dummy = union; });
        try
        {
            if (defect == "parameter-attributes")
            {
                raw.Bits ^= (uint)ParameterAttributes.Optional; raw.Attrs = raw.Bits & 0xFFFF;
                WriteNative(undo, address + 8, BitConverter.GetBytes(raw.Bits));
            }
            else
            {
                raw.Datapoint ^= 1; raw.Data.Dummy = raw.Datapoint;
                WriteNative(undo, address, BitConverter.GetBytes(raw.Datapoint));
            }
            Assert.That(X64ScalarDoubleLeafProof.Find(method), Is.Not.Null);
            Assert.That(proof.IsUnchanged(), Is.False);
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(proof.IsUnchanged(), Is.True);
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [TestCase("field-type")]
    [TestCase("field-offset")]
    [TestCase("field-static")]
    [TestCase("native-layout")]
    [TestCase("original-field-row")]
    public void OriginalFieldOwnershipAndFileBackedLayoutRemainMandatory(string defect)
    {
        var method = Method(X64ScalarDoubleLeafProof.Kind.FieldSum);
        var proof = method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)!;
        var field = proof.FirstField!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "field-type": Change(undo, () => field.OverrideFieldType, value => field.OverrideFieldType = value, _app.SystemTypes.SystemInt64Type); break;
                case "field-offset": Change(undo, () => field.OverrideOffset, value => field.OverrideOffset = value, field.Offset + 8); break;
                case "field-static": Change(undo, () => field.OverrideAttributes, value => field.OverrideAttributes = value, field.Attributes | FieldAttributes.Static); break;
                case "original-field-row":
                    var row = field.BackingData!.Field;
                    var sibling = proof.SecondField!.BackingData!.Field;
                    Change(undo, () => field.BackingData.Field, value => field.BackingData.Field = value, sibling);
                    Assert.That(row, Is.Not.SameAs(sibling), "This mutation needs distinct original field rows."); break;
                case "native-layout":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var origin), Is.True);
                    var registration = _app.Binary.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(origin.MetadataRegistrationAddress);
                    var offsets = _app.Binary.ReadPointerAtVirtualAddress(registration.fieldOffsetListAddress +
                        (ulong)field.DeclaringType.Definition!.TypeIndex.Value * 8);
                    MutateNative(undo, offsets + (ulong)field.BackingData!.IndexInParent * 4, 1); break;
            }
            Assert.That(X64ScalarDoubleLeafProof.Find(method), Is.Null);
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [TestCase("read-order")]
    [TestCase("precision")]
    [TestCase("ordinary-add")]
    [TestCase("site")]
    [TestCase("integer-width")]
    [TestCase("return-value")]
    [TestCase("extra-effect")]
    public void TypedSumCannotExchangeReadsPrecisionOrEffects(string defect)
    {
        var method = Method(X64ScalarDoubleLeafProof.Kind.FieldSum);
        var instructions = method.ControlFlowGraph!.Instructions.ToArray();
        var arithmetic = instructions.Single(site => site.OpCode == OpCode.FloatAdd);
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "read-order":
                    var reads = instructions.Where(site => site.Operands is [LocalVariable, FieldReference]).ToArray();
                    Change(undo, () => reads[0].Operands[1], value => reads[0].SetOperand(1, value), reads[1].Operands[1]); break;
                case "precision": Change(undo, () => arithmetic.Operands[3], value => arithmetic.SetOperand(3, value), new Immediate(32)); break;
                case "ordinary-add": Change(undo, () => arithmetic.OpCode, value => arithmetic.OpCode = value, OpCode.Add); break;
                case "site": Change(undo, () => arithmetic.NativeAddress, value => arithmetic.NativeAddress = value, 1UL); break;
                case "integer-width": Change(undo, () => arithmetic.IntegerBitWidth, value => arithmetic.IntegerBitWidth = value, 64); break;
                case "return-value":
                    var returned = instructions.Last();
                    Change(undo, () => returned.Operands[0], value => returned.SetOperand(0, value), new DoubleLiteral(-0d)); break;
                case "extra-effect":
                    var block = method.ControlFlowGraph.Blocks.Single(item => item.Instructions.Contains(arithmetic));
                    var effect = new Instruction(-1, OpCode.Interrupt);
                    undo.Push(() => block.Instructions.Remove(effect)); block.Instructions.Insert(0, effect); break;
            }
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [TestCase("conversion-width")]
    [TestCase("source-type")]
    [TestCase("coefficient")]
    [TestCase("precision")]
    public void TypedScaleCannotReinterpretSigned64OrChangeTheCapturedCoefficient(string defect)
    {
        var method = Method(X64ScalarDoubleLeafProof.Kind.SignedParameterScale);
        var conversion = method.ControlFlowGraph!.Instructions.Single(site => site.OpCode == OpCode.Int64ToDouble);
        var multiply = method.ControlFlowGraph.Instructions.Single(site => site.OpCode == OpCode.FloatMultiply);
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "conversion-width": Change(undo, () => conversion.OpCode, value => conversion.OpCode = value, OpCode.Int32ToSingle); break;
                case "source-type":
                    var source = (LocalVariable)conversion.Operands[1];
                    Change(undo, () => source.Type, value => source.Type = value, _app.SystemTypes.SystemUInt64Type); break;
                case "coefficient": Change(undo, () => multiply.Operands[2], value => multiply.SetOperand(2, value), new DoubleLiteral(0.5)); break;
                case "precision": Change(undo, () => multiply.Operands[3], value => multiply.SetOperand(3, value), new Immediate(32)); break;
            }
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True);
    }

    [TestCase("method-name")]
    [TestCase("explicit-this")]
    [TestCase("owner-name")]
    [TestCase("field-name")]
    [TestCase("field-binding")]
    [TestCase("class-layout")]
    [TestCase("field-layout")]
    [TestCase("vararg")]
    [TestCase("sentinel")]
    [TestCase("field-count")]
    [TestCase("field-order")]
    [TestCase("other-field-type")]
    [TestCase("base-type")]
    public void RejectedOutputBindingsPreserveThePreviousBody(string defect)
    {
        var method = Method(X64ScalarDoubleLeafProof.Kind.FieldSum);
        var proof = method.GetExtraData<X64ScalarDoubleLeafProof.Evidence>(X64ScalarDoubleLeafProof.EvidenceKey)!;
        var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        IlGenerator.GenerateIl(method, output);
        var previousBody = output.CilMethodBody;
        var field = proof.FirstField!.GetExtraData<FieldDefinition>("AsmResolverField")!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "method-name": Change<Utf8String?>(undo, () => output.Name, value => output.Name = value, "ChangedMethod"); break;
                case "explicit-this": Change(undo, () => output.Signature!.ExplicitThis, value => output.Signature!.ExplicitThis = value, true); break;
                case "owner-name": Change<Utf8String?>(undo, () => output.DeclaringType!.Name, value => output.DeclaringType!.Name = value, "ChangedOwner"); break;
                case "field-name": Change<Utf8String?>(undo, () => field.Name, value => field.Name = value, "ChangedField"); break;
                case "class-layout": Change(undo, () => output.DeclaringType!.ClassLayout,
                    value => output.DeclaringType!.ClassLayout = value, new ClassLayout(1, 1)); break;
                case "field-layout": Change(undo, () => field.FieldOffset, value => field.FieldOffset = value, field.FieldOffset == null ? 24 : field.FieldOffset + 8); break;
                case "vararg": Change(undo, () => output.Signature!.Attributes,
                    value => output.Signature!.Attributes = value, CallingConventionAttributes.VarArg | CallingConventionAttributes.HasThis); break;
                case "sentinel":
                    undo.Push(() => output.Signature!.SentinelParameterTypes.RemoveAt(output.Signature.SentinelParameterTypes.Count - 1));
                    output.Signature!.SentinelParameterTypes.Add(_app.SystemTypes.SystemInt32Type.ToTypeSignature()); break;
                case "field-count":
                    var extra = new FieldDefinition("Extra", AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes.Public,
                        _app.SystemTypes.SystemInt32Type.ToTypeSignature());
                    undo.Push(() => output.DeclaringType!.Fields.Remove(extra)); output.DeclaringType!.Fields.Add(extra); break;
                case "field-order":
                    var fields = output.DeclaringType!.Fields;
                    var left = fields[0]; var right = fields[1];
                    undo.Push(() => { fields.Clear(); foreach (var original in method.DeclaringType!.Fields)
                        fields.Add(original.GetExtraData<FieldDefinition>("AsmResolverField")!); });
                    fields.RemoveAt(1); fields.RemoveAt(0); fields.Insert(0, right); fields.Insert(1, left); break;
                case "other-field-type":
                    var other = method.DeclaringType!.Fields.First(candidate => !ReferenceEquals(candidate, proof.FirstField) && !ReferenceEquals(candidate, proof.SecondField));
                    var otherOutput = other.GetExtraData<FieldDefinition>("AsmResolverField")!;
                    Change(undo, () => otherOutput.Signature!.FieldType, value => otherOutput.Signature!.FieldType = value,
                        (ReferenceEquals(other.FieldType, _app.SystemTypes.SystemDoubleType) ?
                            _app.SystemTypes.SystemInt64Type : _app.SystemTypes.SystemDoubleType).ToTypeSignature()); break;
                case "base-type": Change(undo, () => output.DeclaringType!.BaseType,
                    value => output.DeclaringType!.BaseType = value, null); break;
                case "field-binding": Change(undo,
                    () => proof.FirstField!.GetExtraData<FieldDefinition>("AsmResolverField"),
                    value => proof.FirstField!.PutExtraData("AsmResolverField", value!),
                    proof.SecondField!.GetExtraData<FieldDefinition>("AsmResolverField")); break;
            }
            Assert.That(X64ScalarDoubleLeafProof.IsValidFor(method), Is.True,
                "This control changes only the output binding, not the authentic native recipe.");
            Reject(method);
            Assert.That(output.CilMethodBody, Is.SameAs(previousBody));
        }
        finally { Restore(undo); }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T value)
    {
        var previous = read(); undo.Push(() => write(previous)); write(value);
    }

    private void MutateNative(Stack<Action> undo, ulong address, byte mask)
    {
        var offset = checked((int)_app.Binary.MapVirtualAddressToRaw(address, false));
        WriteNative(undo, address, [(byte)(_app.Binary.GetRawBinaryContent()[offset] ^ mask)]);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] changed)
    {
        var pe = (PE)_app.Binary;
        Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>());
        var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
        var original = pe.GetRawBinaryContent().Slice(offset, changed.Length).ToArray();
        void Write(byte[] bytes)
        {
            var position = pe.BaseStream.Position;
            try { pe.BaseStream.Position = offset; pe.BaseStream.Write(bytes); }
            finally { pe.BaseStream.Position = position; }
        }
        undo.Push(() => Write(original)); Write(changed);
    }

    private static void Reject(MethodAnalysisContext method) =>
        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method,
            method.GetExtraData<MethodDefinition>("AsmResolverMethod")!));

    private static void Restore(Stack<Action> undo)
    {
        var failures = new List<Exception>();
        while (undo.Count != 0)
            try { undo.Pop()(); }
            catch (Exception failure) { failures.Add(failure); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}

public partial class IlGeneratorParameterTests
{
    [Test]
    public void Signed64FloatingConversionPreservesSignAndRoundsBinary64Midpoints()
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("IntegerToDouble", types.SystemDoubleType, [types.SystemInt64Type]);
        var result = new LocalVariable("result", new Register(940, "integerDouble"), types.SystemDoubleType);
        var graph = new ISILControlFlowGraph([new(0, OpCode.Int64ToDouble, result, parameters[0]), new(1, OpCode.Return, result)]);
        ConstantFolder.Run(graph); DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        var code = definition.CilMethodBody!.Instructions;
        var conversion = code.ToList().FindIndex(site => site.OpCode == CilOpCodes.Conv_I8);
        Assert.That(code[conversion + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R8));
        using var runtime = Load();
        var method = runtime.Type.GetMethod("IntegerToDouble")!;
        foreach (var (input, bits) in new (long, ulong)[]
        {
            (0, 0), (1, 0x3FF0000000000000), (-1, 0xBFF0000000000000),
            (9007199254740993, 0x4340000000000000), (9007199254740995, 0x4340000000000002),
            (-9007199254740993, 0xC340000000000000), (long.MaxValue, 0x43E0000000000000), (long.MinValue, 0xC3E0000000000000)
        })
            Assert.That(unchecked((ulong)BitConverter.DoubleToInt64Bits((double)method.Invoke(null, [input])!)), Is.EqualTo(bits));
    }

    [TestCase("unsigned-source")]
    [TestCase("narrow-source")]
    [TestCase("floating-source")]
    [TestCase("retagged-parameter")]
    [TestCase("narrow-result")]
    [TestCase("integer-width")]
    [TestCase("extra-operand")]
    public void Signed64ConversionRejectsUnprovedTypesStorageAndMetadata(string defect)
    {
        var types = _app.SystemTypes;
        var sourceType = defect switch
        {
            "unsigned-source" or "retagged-parameter" => types.SystemUInt64Type,
            "narrow-source" => types.SystemInt32Type,
            "floating-source" => types.SystemDoubleType,
            _ => types.SystemInt64Type
        };
        var (context, definition, parameters) = CreateMethod("InvalidIntegerDouble", types.SystemDoubleType, [sourceType]);
        if (defect == "retagged-parameter") parameters[0].Type = types.SystemInt64Type;
        var result = new LocalVariable("result", new Register(941, "integerDoubleResult"),
            defect == "narrow-result" ? types.SystemSingleType : types.SystemDoubleType);
        var conversion = new Instruction(0, OpCode.Int64ToDouble, result, parameters[0]);
        if (defect == "integer-width") conversion.IntegerBitWidth = 64;
        if (defect == "extra-operand") conversion.AddOperands([Imm(64)]);
        Assert.That(() => Emit(context, definition, [conversion, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Int64-to-Double"));
    }
}
