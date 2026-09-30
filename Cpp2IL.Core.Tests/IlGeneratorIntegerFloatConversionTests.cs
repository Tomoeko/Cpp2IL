using System;
using System.Linq;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void SignedIntegerFloatingConversionPreservesSignAndRoundsTiesToBinary32()
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("IntegerToSingle", types.SystemSingleType, [types.SystemInt32Type]);
        var result = new LocalVariable("result", new Register(920, "integerSingle"), types.SystemSingleType);
        var graph = new ISILControlFlowGraph([new(0, OpCode.Int32ToSingle, result, parameters[0]), new(1, OpCode.Return, result)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        var code = definition.CilMethodBody!.Instructions;
        var conversion = code.ToList().FindIndex(instruction => instruction.OpCode == CilOpCodes.Conv_I4);
        Assert.That(conversion, Is.GreaterThanOrEqualTo(0));
        Assert.That(code[conversion + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R4));
        using var runtime = Load();
        var method = runtime.Type.GetMethod("IntegerToSingle")!;
        foreach (var (input, bits) in new (int, uint)[]
                 { (0, 0), (1, 0x3F800000), (-1, 0xBF800000), (16777217, 0x4B800000),
                   (16777219, 0x4B800002), (-16777217, 0xCB800000), (int.MaxValue, 0x4F000000), (int.MinValue, 0xCF000000) })
            Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits((float)method.Invoke(null, [input])!)), Is.EqualTo(bits));
    }

    [TestCase("unsigned-source")]
    [TestCase("wide-source")]
    [TestCase("floating-source")]
    [TestCase("retagged-parameter")]
    [TestCase("wide-result")]
    [TestCase("integer-width")]
    [TestCase("extra-operand")]
    public void IntegerFloatingConversionRejectsUnprovedSignednessStorageAndOperationMetadata(string mutation)
    {
        var types = _app.SystemTypes;
        var sourceType = mutation switch
        {
            "unsigned-source" or "retagged-parameter" => types.SystemUInt32Type,
            "wide-source" => types.SystemInt64Type,
            "floating-source" => types.SystemSingleType,
            _ => types.SystemInt32Type,
        };
        var (context, definition, parameters) = CreateMethod("InvalidIntegerSingle", types.SystemSingleType, [sourceType]);
        if (mutation == "retagged-parameter") parameters[0].Type = types.SystemInt32Type;
        var result = new LocalVariable("result", new Register(921, "integerSingleResult"),
            mutation == "wide-result" ? types.SystemDoubleType : types.SystemSingleType);
        var conversion = new Instruction(0, OpCode.Int32ToSingle, result, parameters[0]);
        if (mutation == "integer-width") conversion.IntegerBitWidth = 32;
        if (mutation == "extra-operand") conversion.AddOperands([Imm(32)]);
        Assert.That(() => Emit(context, definition, [conversion, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Int32-to-Single"));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void FloatingMultiplicationRoundsAtItsDeclaredPrecisionAndRetainsZeroSign(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("ScalarProduct", type, [type, type]);
        var result = new LocalVariable("result", new Register(922, "scalarProduct"), type);
        var graph = new ISILControlFlowGraph([new(0, OpCode.FloatMultiply, result, parameters[0], parameters[1], Imm(width)),
            new(1, OpCode.Return, result)]);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        var code = definition.CilMethodBody!.Instructions;
        var multiply = code.ToList().FindIndex(instruction => instruction.OpCode == CilOpCodes.Mul);
        Assert.That(code[multiply + 1].OpCode, Is.EqualTo(width == 32 ? CilOpCodes.Conv_R4 : CilOpCodes.Conv_R8));
        using var runtime = Load();
        var method = runtime.Type.GetMethod("ScalarProduct")!;
        var cases = width == 32 ? new (ulong Left, ulong Right, ulong Expected)[]
            { (0x80000000, 0x3F000000, 0x80000000), (0x3FC00000, 0x3F000000, 0x3F400000),
              (0x7F7FFFFF, 0x40000000, 0x7F800000), (3, 0x3F000000, 2) }
            : [(0x8000000000000000, 0x3FE0000000000000, 0x8000000000000000),
               (0x3FF8000000000000, 0x3FE0000000000000, 0x3FE8000000000000),
               (0x7FEFFFFFFFFFFFFF, 0x4000000000000000, 0x7FF0000000000000), (3, 0x3FE0000000000000, 2)];
        foreach (var (left, right, expected) in cases)
        {
            object first = width == 32 ? BitConverter.Int32BitsToSingle(unchecked((int)left)) :
                (object)BitConverter.Int64BitsToDouble(unchecked((long)left));
            object second = width == 32 ? BitConverter.Int32BitsToSingle(unchecked((int)right)) :
                (object)BitConverter.Int64BitsToDouble(unchecked((long)right));
            var actual = method.Invoke(null, [first, second])!;
            Assert.That(width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)actual)) :
                unchecked((ulong)BitConverter.DoubleToInt64Bits((double)actual)), Is.EqualTo(expected));
        }
    }

    [TestCase("source-type")]
    [TestCase("retagged-parameter")]
    [TestCase("unsupported-width")]
    [TestCase("integer-width")]
    [TestCase("literal-width")]
    [TestCase("integer-literal")]
    public void FloatingMultiplicationRejectsUnprovedTypesAndPrecision(string mutation)
    {
        var types = _app.SystemTypes;
        var sourceType = mutation is "source-type" or "retagged-parameter" ? types.SystemInt32Type : types.SystemSingleType;
        var (context, definition, parameters) = CreateMethod("InvalidProduct", types.SystemSingleType, [types.SystemSingleType, sourceType]);
        if (mutation == "retagged-parameter") parameters[1].Type = types.SystemSingleType;
        var result = new LocalVariable("result", new Register(923, "productResult"), types.SystemSingleType);
        var multiplication = new Instruction(0, OpCode.FloatMultiply, result, parameters[0], parameters[1], Imm(32));
        if (mutation == "unsupported-width") multiplication.SetOperand(3, Imm(16));
        if (mutation == "integer-width") multiplication.IntegerBitWidth = 32;
        if (mutation == "literal-width") multiplication.SetOperand(2, new DoubleLiteral(0.5));
        if (mutation == "integer-literal") multiplication.SetOperand(2, Imm(1));
        Assert.That(() => Emit(context, definition, [multiplication, new(1, OpCode.Return, result)]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating"));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void FloatingMultiplicationRetainsExactTypedLiteralsExposedByConstantPropagation(int width)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, definition, parameters) = CreateMethod("LiteralProduct", type, [type]);
        var result = new LocalVariable("result", new Register(924, "literalProduct"), type);
        IOperand coefficient = width == 32 ? new FloatLiteral(0.5f) : new DoubleLiteral(0.5);
        var graph = new ISILControlFlowGraph([new(0, OpCode.FloatMultiply, result, parameters[0], coefficient, Imm(width)),
            new(1, OpCode.Return, result)]);
        ConstantFolder.Run(graph);
        DeadCodeEliminator.Run(graph);
        Emit(context, definition, graph.Instructions);
        using var runtime = Load();
        object input = width == 32 ? 1.5f : (object)1.5;
        var value = runtime.Type.GetMethod("LiteralProduct")!.Invoke(null, [input])!;
        Assert.That(width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits((float)value)) :
            unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value)),
            Is.EqualTo(width == 32 ? 0x3F400000UL : 0x3FE8000000000000UL));
    }
}
