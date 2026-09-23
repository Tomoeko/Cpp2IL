using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("SByte", false)]
    [TestCase("Byte", false)]
    [TestCase("Int16", false)]
    [TestCase("UInt16", false)]
    [TestCase("Int32", false)]
    [TestCase("UInt32", false)]
    [TestCase("Int64", false)]
    [TestCase("UInt64", false)]
    [TestCase("SByte", true)]
    [TestCase("Byte", true)]
    [TestCase("Int16", true)]
    [TestCase("UInt16", true)]
    [TestCase("Int32", true)]
    [TestCase("UInt32", true)]
    [TestCase("Int64", true)]
    [TestCase("UInt64", true)]
    public void IntegerExtensionExecutesLowBitTruncationAndSignOrZeroFill(string inputKind, bool useLocal)
    {
        var types = _app.SystemTypes;
        var (inputType, inputBits) = ExtensionInputType(inputKind);
        var cases = new List<(string Name, int SourceBits, int ResultBits, bool Signed)>();
        foreach (var sourceBits in new[] { 8, 16, 32 }.Where(width => width <= inputBits))
        foreach (var resultBits in new[] { 32, 64 })
        foreach (var signed in new[] { false, true })
        foreach (var unsignedDestination in new[] { false, true })
        {
            var resultType = (resultBits, unsignedDestination) switch
            {
                (32, false) => types.SystemInt32Type,
                (32, true) => types.SystemUInt32Type,
                (64, false) => types.SystemInt64Type,
                _ => types.SystemUInt64Type,
            };
            var name = $"Extend{sourceBits}To{resultBits}Sign{signed}UnsignedResult{unsignedDestination}";
            var (context, definition, parameters) = CreateMethod(name, resultType, [inputType]);
            var source = parameters[0];
            var instructions = new List<Instruction>();
            if (useLocal)
            {
                source = new LocalVariable("copy", new Register(810, "copy"), inputType);
                instructions.Add(new(0, OpCode.Move, source, parameters[0]));
            }
            var result = new LocalVariable("extended", new Register(811, "extended"), resultType);
            instructions.Add(new(instructions.Count, OpCode.IntegerExtend, result, source,
                Imm(sourceBits), Imm(resultBits), Imm(signed ? 1 : 0)));
            instructions.Add(new(instructions.Count, OpCode.Return, result));
            var graph = new ISILControlFlowGraph(instructions);
            FlagConditionRecovery.Run(graph);
            ConstantFolder.Run(graph);
            DeadCodeEliminator.Run(graph);
            Emit(context, definition, graph.Instructions);
            cases.Add((name, sourceBits, resultBits, signed));
        }

        using var runtime = Load();
        foreach (var input in ExtensionInputs(inputKind).Distinct())
        foreach (var test in cases)
        {
            var actual = runtime.Type.GetMethod(test.Name)!.Invoke(null, [input])!;
            var sourceMask = (1UL << test.SourceBits) - 1;
            var expected = ExtensionValueBits(input) & sourceMask;
            if (test.Signed && (expected & (1UL << (test.SourceBits - 1))) != 0)
                expected |= ~sourceMask;
            if (test.ResultBits == 32)
                expected &= uint.MaxValue;
            var actualBits = ExtensionValueBits(actual);
            if (test.ResultBits == 32)
                actualBits &= uint.MaxValue;
            Assert.That(actualBits, Is.EqualTo(expected), $"{inputKind} {input}: {test.Name}");
        }
    }

    [Test]
    public void IntegerExtensionSeparatesSignedByteResultFromSubsequentZeroUpper32Write()
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("TwoExtensions", types.SystemUInt64Type, [types.SystemInt64Type]);
        var narrow = new LocalVariable("signed32", new Register(812, "signed32"), types.SystemInt32Type);
        var result = new LocalVariable("zeroFilled64", new Register(813, "zeroFilled64"), types.SystemUInt64Type);
        Emit(context, definition,
        [
            new(0, OpCode.IntegerExtend, narrow, parameters[0], Imm(8), Imm(32), Imm(1)),
            new(1, OpCode.IntegerExtend, result, narrow, Imm(32), Imm(64), Imm(0)),
            new(2, OpCode.Return, result),
        ]);
        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("TwoExtensions")!.Invoke(null, [0x1234567800000080L]), Is.EqualTo(0xFFFFFF80UL));
    }

    [TestCase("boolean-source")]
    [TestCase("char-source")]
    [TestCase("float-source")]
    [TestCase("native-int-source")]
    [TestCase("reference-source")]
    [TestCase("byref-source")]
    [TestCase("retagged-parameter")]
    [TestCase("retagged-byref-parameter")]
    [TestCase("narrow-source")]
    [TestCase("narrow-destination")]
    [TestCase("result-type-width")]
    [TestCase("source-width")]
    [TestCase("result-width")]
    [TestCase("negative-sign")]
    [TestCase("invalid-sign")]
    [TestCase("width-annotation")]
    [TestCase("missing-metadata")]
    [TestCase("extra-metadata")]
    [TestCase("immediate")]
    [TestCase("register")]
    [TestCase("memory")]
    [TestCase("field")]
    [TestCase("store")]
    public void IntegerExtensionRejectsUnprovedRepresentation(string defect)
    {
        var types = _app.SystemTypes;
        var sourceType = defect switch
        {
            "boolean-source" => types.SystemBooleanType,
            "char-source" => types.SystemCharType,
            "float-source" => types.SystemSingleType,
            "native-int-source" => types.SystemIntPtrType,
            "reference-source" => types.SystemObjectType,
            "byref-source" or "retagged-byref-parameter" => new ByRefTypeAnalysisContext(types.SystemInt32Type),
            "narrow-source" => types.SystemUInt16Type,
            _ => types.SystemUInt64Type,
        };
        var destinationType = defect == "narrow-destination" ? types.SystemByteType :
            defect == "result-type-width" ? types.SystemInt32Type : types.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("InvalidExtension", destinationType, [sourceType]);
        var result = new LocalVariable("result", new Register(814, "result"), destinationType);
        var extension = new Instruction(0, OpCode.IntegerExtend, result, parameters[0], Imm(32), Imm(64), Imm(0));
        switch (defect)
        {
            case "retagged-parameter": parameters[0].Type = types.SystemInt64Type; break;
            case "retagged-byref-parameter": parameters[0].Type = types.SystemInt64Type; break;
            case "source-width": extension.SetOperand(2, Imm(64)); break;
            case "result-width": extension.SetOperand(3, Imm(16)); break;
            case "negative-sign": extension.SetOperand(4, Imm(-1)); break;
            case "invalid-sign": extension.SetOperand(4, Imm(2)); break;
            case "width-annotation": extension.IntegerBitWidth = 32; break;
            case "missing-metadata": extension.RemoveOperandAt(4); break;
            case "extra-metadata": extension.AddOperands([Imm(1)]); break;
            case "immediate": extension.SetOperand(1, Imm(255)); break;
            case "register": extension.SetOperand(1, new Register(815, "untyped")); break;
            case "memory": extension.SetOperand(1, new MemoryOperand(parameters[0])); break;
            case "field":
                var field = new InjectedFieldAnalysisContext("Value", types.SystemInt32Type,
                    System.Reflection.FieldAttributes.Public, _typeContext, 16);
                extension.SetOperand(1, new FieldReference(field, parameters[0], 16));
                break;
            case "store": extension.SetOperand(0, new MemoryOperand(parameters[0])); break;
        }
        Assert.That(() => Emit(context, definition, [extension, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Integer extension"));
    }

    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void IntegerExtensionTypeInferenceUsesResultMetadataWithoutRetypingSource(int bits, bool signed)
    {
        var (context, _, parameters) = CreateMethod("InferExtension", _app.SystemTypes.SystemVoidType, [_app.SystemTypes.SystemByteType]);
        var result = new LocalVariable("result", new Register(816, "result"), null);
        context.Locals.Add(result);
        context.ControlFlowGraph = new([
            new(0, OpCode.IntegerExtend, result, parameters[0], Imm(8), Imm(bits), Imm(signed ? 1 : 0)),
            new(1, OpCode.Return),
        ]);
        LocalVariables.ResolveTypesAndFields(context);
        var expected = (bits, signed) switch
        {
            (32, false) => _app.SystemTypes.SystemUInt32Type,
            (32, true) => _app.SystemTypes.SystemInt32Type,
            (64, false) => _app.SystemTypes.SystemUInt64Type,
            _ => _app.SystemTypes.SystemInt64Type,
        };
        Assert.That(result.Type, Is.SameAs(expected));
        Assert.That(parameters[0].Type, Is.SameAs(_app.SystemTypes.SystemByteType));
    }

    [Test]
    public void IntegerExtensionMetadataSurvivesCopiesAndDistinguishesOperations()
    {
        var type = _app.SystemTypes.SystemInt64Type;
        var (context, _, parameters) = CreateMethod("CopyExtension", type, [type]);
        var source = new LocalVariable("copy", new Register(817, "copy"), type);
        var result = new LocalVariable("result", new Register(818, "result"), type);
        var extension = new Instruction(1, OpCode.IntegerExtend, result, source, Imm(8), Imm(64), Imm(1));
        var graph = new ISILControlFlowGraph([new(0, OpCode.Move, source, parameters[0]), extension, new(2, OpCode.Return, result)]);
        SsaSimplifier.Run(graph, context.ParameterLocals);
        var expected = new Instruction(1, OpCode.IntegerExtend, result, parameters[0], Imm(8), Imm(64), Imm(1));
        Assert.That(extension.IsStructurallyEqualTo(expected), Is.True);
        Assert.That(extension.Sources.ToArray(), Is.EqualTo(new IOperand[] { parameters[0] }));
        Assert.That(extension.SourcesAndConstants.ToArray(), Is.EqualTo(new IOperand[] { parameters[0] }));
        Assert.That(extension.OpCode.IsComparison(), Is.False);
        foreach (var (index, replacement) in new[] { (2, 16), (3, 32), (4, 0) })
        {
            var altered = new Instruction(1, OpCode.IntegerExtend, result, parameters[0], Imm(8), Imm(64), Imm(1));
            altered.SetOperand(index, Imm(replacement));
            Assert.That(extension.IsStructurallyEqualTo(altered), Is.False);
        }
    }

    [TestCase("valid", true)]
    [TestCase("memory", false)]
    [TestCase("field", false)]
    [TestCase("source-width", false)]
    [TestCase("result-width", false)]
    [TestCase("sign", false)]
    [TestCase("width-annotation", false)]
    [TestCase("missing-metadata", false)]
    [TestCase("source-type", false)]
    [TestCase("source-too-small", false)]
    [TestCase("destination-type", false)]
    [TestCase("unknown-type", false)]
    [TestCase("same-name-type", false)]
    public void DeadIntegerExtensionRequiresPureTypedInputsAndValidMetadata(string defect, bool removed)
    {
        var types = _app.SystemTypes;
        var (_, _, parameters) = CreateMethod("UnusedExtension", types.SystemVoidType, [types.SystemInt64Type]);
        var result = new LocalVariable("unused", new Register(819, "unused"), types.SystemInt64Type);
        var extension = new Instruction(0, OpCode.IntegerExtend, result, parameters[0], Imm(32), Imm(64), Imm(0));
        switch (defect)
        {
            case "memory": extension.SetOperand(1, new MemoryOperand(parameters[0])); break;
            case "field":
                var field = new InjectedFieldAnalysisContext("Value", types.SystemInt32Type,
                    System.Reflection.FieldAttributes.Public, _typeContext, 16);
                extension.SetOperand(1, new FieldReference(field, parameters[0], 16));
                break;
            case "source-width": extension.SetOperand(2, Imm(64)); break;
            case "result-width": extension.SetOperand(3, Imm(8)); break;
            case "sign": extension.SetOperand(4, Imm(2)); break;
            case "width-annotation": extension.IntegerBitWidth = 32; break;
            case "missing-metadata": extension.RemoveOperandAt(4); break;
            case "source-type": parameters[0].Type = types.SystemDoubleType; break;
            case "source-too-small": parameters[0].Type = types.SystemByteType; break;
            case "destination-type": result.Type = types.SystemBooleanType; break;
            case "unknown-type": parameters[0].Type = null; break;
            case "same-name-type":
                parameters[0].Type = new InjectedTypeAnalysisContext(_typeContext.DeclaringAssembly, "System", "Int64",
                    types.SystemValueTypeType, System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed);
                break;
        }
        DeadCodeEliminator.Run(new ISILControlFlowGraph([extension, new(1, OpCode.Return)]));
        Assert.That(extension.OpCode == OpCode.Nop, Is.EqualTo(removed));
    }

    private (TypeAnalysisContext Type, int Bits) ExtensionInputType(string kind) => kind switch
    {
        "SByte" => (_app.SystemTypes.SystemSByteType, 8),
        "Byte" => (_app.SystemTypes.SystemByteType, 8),
        "Int16" => (_app.SystemTypes.SystemInt16Type, 16),
        "UInt16" => (_app.SystemTypes.SystemUInt16Type, 16),
        "Int32" => (_app.SystemTypes.SystemInt32Type, 32),
        "UInt32" => (_app.SystemTypes.SystemUInt32Type, 32),
        "Int64" => (_app.SystemTypes.SystemInt64Type, 64),
        _ => (_app.SystemTypes.SystemUInt64Type, 64),
    };

    private static IEnumerable<object> ExtensionInputs(string kind)
    {
        ulong[] values = [0, 1, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000,
            0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x100000000, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, ulong.MaxValue];
        foreach (var value in values)
            yield return kind switch
            {
                "SByte" => (object)unchecked((sbyte)value),
                "Byte" => unchecked((byte)value),
                "Int16" => unchecked((short)value),
                "UInt16" => unchecked((ushort)value),
                "Int32" => unchecked((int)value),
                "UInt32" => unchecked((uint)value),
                "Int64" => unchecked((long)value),
                _ => value,
            };
    }

    private static ulong ExtensionValueBits(object value) => value switch
    {
        sbyte v => unchecked((ulong)v),
        byte v => v,
        short v => unchecked((ulong)v),
        ushort v => v,
        int v => unchecked((ulong)v),
        uint v => v,
        long v => unchecked((ulong)v),
        ulong v => v,
        _ => throw new ArgumentException("Expected a canonical integer value", nameof(value)),
    };
}
