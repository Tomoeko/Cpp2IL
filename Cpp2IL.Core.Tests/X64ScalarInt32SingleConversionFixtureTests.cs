using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;
using NativeCode = Iced.Intel.Code;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarInt32SingleConversionFixtureTests
{
    private Dictionary<string, MethodAnalysisContext> _methods = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_INT32_SINGLE_CONVERSION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_INT32_SINGLE_CONVERSION_FIXTURE_INPUT to the neutral exact player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("ScalarInt32SingleConversionFixture")!.Types.Single(type => type.Name == "ConversionHolder")
            .Methods.Where(method => method.Name != ".ctor").ToDictionary(method => method.Name);
        foreach (var method in _methods.Values) method.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("StoreFirst")]
    [TestCase("StoreSecond")]
    [TestCase("Convert")]
    [TestCase("ConvertStatic")]
    [TestCase("Ratio")]
    [TestCase("ScaledChoice")]
    public void CompleteNativeConversionKeepsSignedValuesSeparateRoundingAndDeclaredFields(string name)
    {
        var method = _methods[name];
        Accept(method);
        var conversions = method.ControlFlowGraph!.Instructions.Where(instruction => instruction.OpCode == OpCode.Int32ToSingle).ToArray();
        Assert.That(conversions.Length, Is.EqualTo(name == "Ratio" ? 2 : 1));
        foreach (var conversion in conversions)
        {
            Assert.That(conversion.Operands[0], Is.TypeOf<LocalVariable>());
            Assert.That(((LocalVariable)conversion.Operands[1]).Type, Is.SameAs(method.AppContext.SystemTypes.SystemInt32Type));
        }
        var code = Definition(method).CilMethodBody!.Instructions;
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I4), Is.GreaterThanOrEqualTo(conversions.Length));
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Conv_R4), Is.GreaterThanOrEqualTo(conversions.Length));
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(name.StartsWith("Store", StringComparison.Ordinal) ? 1 : 0));
        if (name is "Ratio" or "ScaledChoice")
        {
            var arithmetic = code.ToList().FindIndex(instruction => instruction.OpCode == (name == "Ratio" ? CilOpCodes.Div : CilOpCodes.Mul));
            Assert.That(arithmetic, Is.GreaterThanOrEqualTo(0));
            Assert.That(code[arithmetic + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R4));
        }
        if (name == "ScaledChoice")
        {
            Assert.That(method.ControlFlowGraph.Instructions.Single(instruction => instruction.OpCode == OpCode.Subtract).IntegerBitWidth, Is.EqualTo(32));
            Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Sub), Is.EqualTo(1));
            Assert.That(code.Any(instruction => instruction.OpCode == CilOpCodes.Sub_Ovf), Is.False);
        }
    }

    [Test]
    public void ReadOnlyScaleBytesMustStillMatchTheAdmittedNativeConstant()
    {
        var method = _methods["ScaledChoice"];
        Accept(method);
        var proof = method.GetExtraData<X64ScalarInt32ToSingleProof.Proof>(X64ScalarInt32ToSingleProof.EvidenceKey)!;
        var address = proof.Native.Arithmetic!.Value.IPRelativeMemoryAddress;
        var offset = X64UnwindProof.ForApplication(method.AppContext)!.MapReadOnlyData(address, 4);
        Assert.That(offset, Is.GreaterThanOrEqualTo(0));
        var pe = (PE)method.AppContext.Binary;
        var original = pe.GetRawBinaryContent()[offset];
        var originalPosition = pe.BaseStream.Position;
        try
        {
            // The native body and managed literal are unchanged. Mutating only
            // the file-backed coefficient must invalidate the retained proof.
            pe.BaseStream.Position = offset;
            pe.BaseStream.WriteByte((byte)(original ^ 1));
            Assert.That(pe.GetRawBinaryContent()[offset], Is.EqualTo((byte)(original ^ 1)));
            Assert.That(X64ScalarInt32ToSingleProof.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally
        {
            pe.BaseStream.Position = offset;
            pe.BaseStream.WriteByte(original);
            pe.BaseStream.Position = originalPosition;
        }
        Accept(method);
    }

    [Test]
    public void PackedConversionCannotReadAnUninitializedDifferentRegister()
    {
        var method = _methods["Ratio"];
        Accept(method);
        var proof = method.GetExtraData<X64ScalarInt32ToSingleProof.Proof>(X64ScalarInt32ToSingleProof.EvidenceKey)!;
        var body = proof.Body.ToArray();
        Assert.That(X64ScalarInt32ToSingleProof.TryProveShape(body), Is.EqualTo(proof.Native));
        var position = Array.FindIndex(body, instruction => instruction.Code == NativeCode.Cvtdq2ps_xmm_xmmm128);
        var conversion = body[position];
        conversion.Op1Register = NativeRegister.XMM2;
        body[position] = conversion;
        Assert.That(X64ScalarInt32ToSingleProof.TryProveShape(body), Is.Null);
        Assert.That(X64ScalarInt32ToSingleProof.IsValidFor(method), Is.True);
    }

    [Test]
    public void NumericConversionCannotAcquireInstanceCallSemantics()
    {
        var method = _methods["StoreFirst"];
        Accept(method);
        var conversion = method.ControlFlowGraph!.Instructions.First(instruction => instruction.OpCode == OpCode.Int32ToSingle);
        Assert.Throws<InvalidOperationException>(() => conversion.CallSemantics = CallSemantics.NullCheckedInstance);
        Assert.That(conversion.CallSemantics, Is.EqualTo(CallSemantics.Direct));
        Accept(method);
    }

    [TestCase("StoreFirst", "conversion-source")]
    [TestCase("StoreFirst", "conversion-result-type")]
    [TestCase("StoreFirst", "conversion-address")]
    [TestCase("StoreFirst", "integer-width")]
    [TestCase("StoreFirst", "store-field")]
    [TestCase("StoreFirst", "store-owner")]
    [TestCase("StoreFirst", "field-layout")]
    [TestCase("StoreFirst", "field-type")]
    [TestCase("StoreFirst", "raw-field-identity")]
    [TestCase("StoreFirst", "owner-raw-flags")]
    [TestCase("StoreFirst", "native-bytes")]
    [TestCase("StoreFirst", "missing-evidence")]
    [TestCase("StoreFirst", "extra-effect")]
    [TestCase("StoreFirst", "entry-effect")]
    [TestCase("StoreFirst", "exit-effect")]
    [TestCase("StoreFirst", "body-successor")]
    [TestCase("ConvertStatic", "parameter-type")]
    [TestCase("Convert", "return-value")]
    [TestCase("Ratio", "division-order")]
    [TestCase("Ratio", "division-precision")]
    [TestCase("Ratio", "second-conversion-source")]
    [TestCase("ScaledChoice", "decrement-opcode")]
    [TestCase("ScaledChoice", "decrement-value")]
    [TestCase("ScaledChoice", "scale-literal")]
    [TestCase("ScaledChoice", "enum-underlying")]
    [TestCase("ScaledChoice", "enum-raw-flags")]
    public void FinalProofRejectsChangedNativeBitsSignednessPrecisionEffectsAndGraph(string name, string mutation)
    {
        var method = _methods[name];
        Accept(method);
        var graph = method.ControlFlowGraph!;
        var operations = graph.Instructions;
        var conversion = operations.First(instruction => instruction.OpCode == OpCode.Int32ToSingle);
        var body = graph.FindBlockByInstruction(conversion)!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "conversion-source": Replace(conversion, 1, conversion.Operands[0]); break;
                case "conversion-result-type": Retype((LocalVariable)conversion.Destination!, method.AppContext.SystemTypes.SystemDoubleType); break;
                case "conversion-address":
                    var address = conversion.NativeAddress;
                    undo.Add(() => conversion.NativeAddress = address);
                    conversion.NativeAddress++;
                    break;
                case "integer-width": undo.Add(() => conversion.IntegerBitWidth = 0); conversion.IntegerBitWidth = 32; break;
                case "store-field":
                    var store = Store();
                    var destination = (FieldReference)store.Operands[0];
                    var other = method.DeclaringType!.Fields.Single(field => field.Name == "Second");
                    Replace(store, 0, new FieldReference(other, destination.Local, other.Offset));
                    break;
                case "store-owner":
                    var access = (FieldReference)Store().Operands[0];
                    var owner = access.Local;
                    undo.Add(() => access.Local = owner);
                    access.Local = new LocalVariable("other", new Register(null, "r8"), owner.Type);
                    break;
                case "field-layout":
                    var field = ((FieldReference)Store().Operands[0]).Field;
                    var offset = field.OverrideOffset;
                    undo.Add(() => field.OverrideOffset = offset);
                    field.OverrideOffset = field.DefaultOffset + 4;
                    break;
                case "field-type":
                    var typedField = ((FieldReference)Store().Operands[0]).Field;
                    var type = typedField.OverrideFieldType;
                    undo.Add(() => typedField.OverrideFieldType = type);
                    typedField.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type;
                    break;
                case "raw-field-identity":
                    var raw = ((FieldReference)Store().Operands[0]).Field.BackingData!.Field;
                    var identity = raw.nameIndex;
                    undo.Add(() => raw.nameIndex = identity);
                    raw.nameIndex++;
                    break;
                case "owner-raw-flags": ChangeFlags(method.DeclaringType!); break;
                case "native-bytes":
                    var bytes = method.RawBytes;
                    undo.Add(() => method.RawBytes = bytes);
                    var changed = bytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "missing-evidence":
                    var evidence = method.GetExtraData<object>(X64ScalarInt32ToSingleProof.EvidenceKey)!;
                    undo.Add(() => method.PutExtraData(X64ScalarInt32ToSingleProof.EvidenceKey, evidence));
                    method.PutExtraData<object>(X64ScalarInt32ToSingleProof.EvidenceKey, null!);
                    Assert.That(X64ScalarInt32ToSingleProof.HasEvidence(method), Is.True);
                    break;
                case "extra-effect":
                    var extra = new Instruction(-1, OpCode.Move, Store().Operands[0], conversion.Destination!);
                    body.Instructions.Insert(body.Instructions.Count - 1, extra);
                    undo.Add(() => body.Instructions.Remove(extra));
                    break;
                case "entry-effect":
                case "exit-effect":
                    var index = body.Instructions.IndexOf(conversion);
                    var sentinel = mutation == "entry-effect" ? graph.EntryBlock : graph.ExitBlock;
                    body.Instructions.Remove(conversion);
                    sentinel.Instructions.Add(conversion);
                    undo.Add(() => { sentinel.Instructions.Remove(conversion); body.Instructions.Insert(index, conversion); });
                    break;
                case "body-successor":
                    var successor = body.Successors[0];
                    undo.Add(() => body.Successors[0] = successor);
                    body.Successors[0] = graph.EntryBlock;
                    break;
                case "parameter-type":
                    var parameter = method.Parameters[0];
                    var parameterType = parameter.OverrideParameterType;
                    undo.Add(() => parameter.OverrideParameterType = parameterType);
                    parameter.OverrideParameterType = method.AppContext.SystemTypes.SystemUInt32Type;
                    break;
                case "return-value": Replace(operations.Single(instruction => instruction.OpCode == OpCode.Return), 0, conversion.Operands[1]); break;
                case "division-order":
                    var division = operations.Single(instruction => instruction.OpCode == OpCode.FloatDivide);
                    var left = division.Operands[1];
                    var right = division.Operands[2];
                    Replace(division, 1, right); Replace(division, 2, left);
                    break;
                case "division-precision": Replace(operations.Single(instruction => instruction.OpCode == OpCode.FloatDivide), 3, new Immediate(64)); break;
                case "second-conversion-source": Replace(operations.Last(instruction => instruction.OpCode == OpCode.Int32ToSingle), 1, conversion.Operands[1]); break;
                case "decrement-opcode":
                    var decrement = operations.Single(instruction => instruction.OpCode == OpCode.Subtract);
                    undo.Add(() => decrement.OpCode = OpCode.Subtract);
                    decrement.OpCode = OpCode.Add;
                    break;
                case "decrement-value": Replace(operations.Single(instruction => instruction.OpCode == OpCode.Subtract), 2, new Immediate(2)); break;
                case "scale-literal":
                    var product = operations.Single(instruction => instruction.OpCode == OpCode.FloatMultiply);
                    if (product.Operands[2] is FloatLiteral) Replace(product, 2, new FloatLiteral(2f));
                    else Replace(operations.Single(instruction => instruction is { OpCode: OpCode.Move, Operands: [_, FloatLiteral] }), 1, new FloatLiteral(2f));
                    break;
                case "enum-underlying":
                    var enumType = method.DeclaringType!.Fields.Single(field => field.Name == "Choice").FieldType;
                    var underlying = enumType.OverrideEnumUnderlyingType;
                    undo.Add(() => enumType.OverrideEnumUnderlyingType = underlying);
                    enumType.OverrideEnumUnderlyingType = method.AppContext.SystemTypes.SystemUInt32Type;
                    break;
                case "enum-raw-flags": ChangeFlags(method.DeclaringType!.Fields.Single(field => field.Name == "Choice").FieldType); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64ScalarInt32ToSingleProof.IsValidFor(method), Is.False, mutation);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)), mutation);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept(method);
        return;

        Instruction Store() => operations.Single(instruction => instruction is { OpCode: OpCode.Move, Operands: [FieldReference, _] });
        void Replace(Instruction instruction, int index, IOperand value)
        {
            var previous = instruction.Operands[index];
            undo.Add(() => instruction.SetOperand(index, previous));
            instruction.SetOperand(index, value);
        }
        void Retype(LocalVariable local, TypeAnalysisContext type)
        {
            var previous = local.Type;
            undo.Add(() => local.Type = previous);
            local.Type = type;
        }
        void ChangeFlags(TypeAnalysisContext type)
        {
            var previous = type.Definition!.Flags;
            undo.Add(() => type.Definition.Flags = previous);
            type.Definition.Flags ^= (uint)TypeAttributes.Sealed;
        }
    }

    private static MethodDefinition Definition(MethodAnalysisContext method) => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
    private static void Accept(MethodAnalysisContext method)
    {
        Assert.That(X64ScalarInt32ToSingleProof.HasEvidence(method), Is.True, method.Name);
        Assert.That(X64ScalarInt32ToSingleProof.IsValidFor(method), Is.True, method.Name);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, Definition(method)), method.Name);
    }
}
