using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, false)]
    [TestCase(64, false)]
    [TestCase(32, true)]
    [TestCase(64, true)]
    public void FloatingTruncationExecutesIndependentBinary64IntegerOracle(int width, bool copyToLocal)
    {
        var types = _app.SystemTypes;
        var resultType = width == 32 ? types.SystemInt32Type : types.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("Truncate", resultType, [types.SystemDoubleType]);
        var source = parameters[0];
        var result = new LocalVariable("result", new Register(850, "truncateResult"), resultType);
        var instructions = new List<Instruction>();
        if (copyToLocal)
        {
            source = new LocalVariable("snapshot", new Register(851, "truncateInput"), types.SystemDoubleType);
            instructions.Add(new(0, OpCode.Move, source, parameters[0]));
        }
        instructions.Add(new(instructions.Count, OpCode.FloatTruncateSigned, result, source, Imm(64), Imm(width)));
        instructions.Add(new(instructions.Count, OpCode.Return, result));
        var graph = new ISILControlFlowGraph(instructions);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Truncate")!;
        foreach (var bits in TruncationInputBits())
        {
            var value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
            var actual = method.Invoke(null, [value])!;
            var actualBits = width == 32 ? unchecked((uint)(int)actual) : unchecked((ulong)(long)actual);
            Assert.That(actualBits, Is.EqualTo(TruncationOracleBits(bits, width)),
                $"binary64 0x{bits:X16} -> signed {width}");
        }
    }

    [TestCase("integer-source")]
    [TestCase("single-source")]
    [TestCase("retagged-parameter")]
    [TestCase("retagged-byref-parameter")]
    [TestCase("unsigned-destination")]
    [TestCase("narrow-destination")]
    [TestCase("wrong-result-width")]
    [TestCase("source-width")]
    [TestCase("result-width")]
    [TestCase("width-annotation")]
    [TestCase("missing-metadata")]
    [TestCase("extra-metadata")]
    [TestCase("immediate")]
    [TestCase("literal")]
    [TestCase("register")]
    [TestCase("memory")]
    [TestCase("field")]
    [TestCase("store")]
    public void FloatingTruncationRejectsUnprovedRepresentation(string invalidity)
    {
        var types = _app.SystemTypes;
        var sourceType = invalidity is "integer-source" or "retagged-parameter" ? types.SystemInt32Type :
            invalidity == "single-source" ? types.SystemSingleType :
            invalidity == "retagged-byref-parameter" ? new ByRefTypeAnalysisContext(types.SystemDoubleType) : types.SystemDoubleType;
        var destinationType = invalidity == "unsigned-destination" ? types.SystemUInt32Type :
            invalidity == "narrow-destination" ? types.SystemInt16Type :
            invalidity == "wrong-result-width" ? types.SystemInt64Type : types.SystemInt32Type;
        var (context, definition, parameters) = CreateMethod("InvalidTruncation", destinationType, [sourceType]);
        if (invalidity is "retagged-parameter" or "retagged-byref-parameter")
            parameters[0].Type = types.SystemDoubleType;
        var result = new LocalVariable("result", new Register(852, "truncateResult"), destinationType);
        var conversion = new Instruction(0, OpCode.FloatTruncateSigned, result, parameters[0], Imm(64), Imm(32));
        switch (invalidity)
        {
            case "source-width": conversion.SetOperand(2, Imm(32)); break;
            case "result-width": conversion.SetOperand(3, Imm(16)); break;
            case "width-annotation": conversion.IntegerBitWidth = 64; break;
            case "missing-metadata": conversion.RemoveOperandAt(3); break;
            case "extra-metadata": conversion.AddOperands([Imm(1)]); break;
            case "immediate": conversion.SetOperand(1, Imm(3)); break;
            case "literal": conversion.SetOperand(1, new DoubleLiteral(3.5)); break;
            case "register": conversion.SetOperand(1, new Register(853, "untyped")); break;
            case "memory": conversion.SetOperand(1, new MemoryOperand(parameters[0])); break;
            case "field":
                var field = new InjectedFieldAnalysisContext("Value", types.SystemDoubleType,
                    System.Reflection.FieldAttributes.Public, _typeContext, 16);
                conversion.SetOperand(1, new FieldReference(field, parameters[0], 16));
                break;
            case "store": conversion.SetOperand(0, new MemoryOperand(parameters[0])); break;
        }
        Assert.That(() => Emit(context, definition, [conversion, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating truncation"));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void FloatingTruncationTypesOnlyItsResultAndMetadataIsNotAValueSource(int width)
    {
        var (context, _, parameters) = CreateMethod("InferTruncation", _app.SystemTypes.SystemVoidType, [_app.SystemTypes.SystemDoubleType]);
        var result = new LocalVariable("result", new Register(854, "truncateResult"), null);
        context.Locals.Add(result);
        var conversion = new Instruction(0, OpCode.FloatTruncateSigned, result, parameters[0], Imm(64), Imm(width));
        context.ControlFlowGraph = new([conversion, new(1, OpCode.Return)]);
        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(result.Type, Is.SameAs(width == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type));
        Assert.That(parameters[0].Type, Is.SameAs(_app.SystemTypes.SystemDoubleType));
        Assert.That(conversion.Destination, Is.SameAs(result));
        Assert.That(conversion.SourcesAndConstants, Is.EqualTo(new[] { parameters[0] }));
        Assert.That(OperandEffects.ReadLocals(conversion), Is.EqualTo(new[] { parameters[0] }));
    }

    [Test]
    public void UnusedMalformedFloatingTruncationCannotDisappearBeforeValidation()
    {
        var (context, definition, parameters) = CreateMethod("DeadInvalidTruncation", _app.SystemTypes.SystemInt32Type, [_app.SystemTypes.SystemDoubleType]);
        var result = new LocalVariable("unused", new Register(855, "unusedResult"), _app.SystemTypes.SystemInt32Type);
        var conversion = new Instruction(0, OpCode.FloatTruncateSigned, result, parameters[0], Imm(64), Imm(8));
        context.ControlFlowGraph = new([conversion, new(1, OpCode.Return, Imm(7))]);
        DeadCodeEliminator.Run(context);
        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.FloatTruncateSigned));
        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating truncation"));
    }

    private static IEnumerable<ulong> TruncationInputBits()
    {
        ulong[] magnitudes =
        [
            0x0000000000000000, 0x0000000000000001, 0x000FFFFFFFFFFFFF, 0x0010000000000000,
            0x3FDFFFFFFFFFFFFF, 0x3FE0000000000000, 0x3FEFFFFFFFFFFFFF, 0x3FF0000000000000,
            0x3FF0000000000001, 0x3FF8000000000000, 0x3FFFFFFFFFFFFFFF, 0x4004000000000000,
            0x41DFFFFFFFA00000, 0x41DFFFFFFFC00000, 0x41DFFFFFFFE00000, 0x41DFFFFFFFFFFFFF,
            0x41E0000000000000, 0x41E0000000000001, 0x41E0000000100000, 0x41E0000000200000,
            0x41E0000000200001, 0x43DFFFFFFFFFFFFE, 0x43DFFFFFFFFFFFFF, 0x43E0000000000000,
            0x43E0000000000001, 0x43E0000000000002, 0x433FFFFFFFFFFFFF, 0x4340000000000000,
            0x4340000000000001, 0x7FEFFFFFFFFFFFFF, 0x7FF0000000000000, 0x7FF8000000000001,
            0x7FFB123456789ABC,
        ];
        foreach (var bits in magnitudes)
        {
            yield return bits;
            yield return bits | (1UL << 63);
        }
        // Deterministic finite samples cover exponent ranges without using a host cast as oracle.
        ulong state = 0x5343414C41524650;
        for (var index = 0; index < 512; index++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            if ((state >> 52 & 0x7FF) != 0x7FF)
                yield return state;
        }
    }

    // Independent of the emitted comparisons/conversions: decode IEEE significand and exponent
    // into an arbitrary-precision integer, discard fractional bits and compare integer limits.
    private static ulong TruncationOracleBits(ulong bits, int width)
    {
        var exponent = (int)(bits >> 52 & 0x7FF);
        var minimum = -(BigInteger.One << (width - 1));
        var maximum = (BigInteger.One << (width - 1)) - 1;
        var result = minimum;
        if (exponent != 0x7FF)
        {
            var significand = new BigInteger(bits & 0x000FFFFFFFFFFFFFUL);
            var power = -1074;
            if (exponent != 0)
            {
                significand |= BigInteger.One << 52;
                power = exponent - 1075;
            }
            result = power >= 0 ? significand << power : significand >> -power;
            if ((bits & (1UL << 63)) != 0)
                result = -result;
            if (result < minimum || result > maximum)
                result = minimum;
        }
        return (ulong)(result & ((BigInteger.One << width) - 1));
    }
}
