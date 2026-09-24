using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("83C007", 32)] // add eax, 7
    [TestCase("4883C007", 64)] // add rax, 7
    [TestCase("6683C007", 0)] // add ax, 7 remains outside the bounded rule
    public void NativeIntegerAddRecordsOnlySupportedResultWidths(string bytes, int width)
    {
        var decoder = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(bytes)));
        var native = decoder.Decode();
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native)
            .Single(instruction => instruction.OpCode == OpCode.Add);
        Assert.That(lifted.OpCode, Is.EqualTo(OpCode.Add));
        Assert.That(lifted.IntegerBitWidth, Is.EqualTo(width));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void MatchingSignedNativeAddsInferIntermediateTypeAndExecuteWrappedArithmetic(int width)
    {
        var integerType = width == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("WrappedAdd", integerType, [integerType]);
        var intermediate = new LocalVariable("intermediate", new Register(940, "intermediate"));
        var result = new LocalVariable("result", new Register(941, "result"), integerType);
        context.Locals.AddRange([intermediate, result]);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Add, intermediate, parameters[0], Imm(7)) { IntegerBitWidth = width },
            new Instruction(1, OpCode.Add, result, intermediate, parameters[0]) { IntegerBitWidth = width },
            new Instruction(2, OpCode.Return, result),
        ]);

        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(intermediate.Type, Is.SameAs(integerType));
        IlGenerator.GenerateIl(context, definition);

        using var runtime = Load();
        var recovered = runtime.Type.GetMethod("WrappedAdd")!;
        if (width == 32)
        {
            foreach (var input in new[] { 0, -7, int.MaxValue, int.MinValue })
                Assert.That(recovered.Invoke(null, [input]),
                    Is.EqualTo(unchecked(unchecked(input + 7) + input)));
        }
        else
        {
            foreach (var input in new[] { 0L, -7L, long.MaxValue, long.MinValue })
                Assert.That(recovered.Invoke(null, [input]),
                    Is.EqualTo(unchecked(unchecked(input + 7) + input)));
        }
    }

    [TestCase(0, "Int32", "literal", 7L)]
    [TestCase(64, "Int32", "literal", 7L)]
    [TestCase(32, "Int64", "literal", 7L)]
    [TestCase(32, "UInt32", "literal", 7L)]
    [TestCase(64, "UInt64", "literal", 7L)]
    [TestCase(32, "Single", "literal", 7L)]
    [TestCase(32, "Object", "literal", 7L)]
    [TestCase(32, "Int32", "literal", 2147483648L)]
    [TestCase(32, "Int32", "literal", -2147483649L)]
    [TestCase(32, "Int32", "mismatched-local", 0L)]
    [TestCase(64, "Int64", "mismatched-local", 0L)]
    public void IntegerAddDoesNotInferFromUnsupportedWidthSignOrOperand(
        int width, string sourceKind, string rightKind, long literal)
    {
        var types = _app.SystemTypes;
        var sourceType = sourceKind switch
        {
            "Int32" => types.SystemInt32Type,
            "Int64" => types.SystemInt64Type,
            "UInt32" => types.SystemUInt32Type,
            "UInt64" => types.SystemUInt64Type,
            "Single" => types.SystemSingleType,
            _ => types.SystemObjectType,
        };
        var otherType = ReferenceEquals(sourceType, types.SystemInt64Type)
            ? types.SystemInt32Type : types.SystemInt64Type;
        var (context, _, parameters) = CreateMethod("UnprovedAdd", types.SystemVoidType,
            rightKind == "literal" ? [sourceType] : [sourceType, otherType]);
        var result = new LocalVariable("unknown", new Register(942, "unknown"));
        context.Locals.Add(result);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new Instruction(0, OpCode.Add, result, parameters[0],
                rightKind == "literal" ? Imm(literal) : parameters[1]) { IntegerBitWidth = width },
            new Instruction(1, OpCode.Return),
        ]);

        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(result.Type, Is.Null);
    }

    [TestCase(32)]
    [TestCase(64)]
    public void NativeAddRejectsAlreadyTypedDestinationWithTheWrongWidth(int width)
    {
        var types = _app.SystemTypes;
        var sourceType = width == 32 ? types.SystemInt32Type : types.SystemInt64Type;
        var destinationType = width == 32 ? types.SystemInt64Type : types.SystemInt32Type;
        var (context, definition, parameters) = CreateMethod("MismatchedAdd", destinationType, [sourceType]);
        var result = new LocalVariable("result", new Register(943, "result"), destinationType);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition, [
                new Instruction(0, OpCode.Add, result, parameters[0], Imm(7)) { IntegerBitWidth = width },
                new Instruction(1, OpCode.Return, result),
            ]),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Native Add destination width"));
    }
}
