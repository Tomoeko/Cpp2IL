using System;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void ReferenceNullShapeExcludesNonzeroPointerAndOrderingComparisons()
    {
        var value = new LocalVariable("value", new Register(1700, "value"),
            _app.SystemTypes.SystemObjectType);
        var result = new LocalVariable("result", new Register(1701, "result"),
            _app.SystemTypes.SystemBooleanType);
        var zero = new Instruction(0, OpCode.CheckEqual, result, value, new Immediate(0))
            { IntegerBitWidth = 64 };
        Assert.That(IlGenerator.IsReferenceNullComparisonShape(zero,
            _app.SystemTypes.SystemBooleanType, out var proved), Is.True);
        Assert.That(proved, Is.SameAs(value));

        Assert.That(IlGenerator.IsReferenceNullComparisonShape(
            new Instruction(0, OpCode.CheckEqual, result, value, new Immediate(1))
                { IntegerBitWidth = 64 }, _app.SystemTypes.SystemBooleanType, out _), Is.False);
        Assert.That(IlGenerator.IsReferenceNullComparisonShape(
            new Instruction(0, OpCode.CheckLessUnsigned, result, value, new Immediate(0))
                { IntegerBitWidth = 64 }, _app.SystemTypes.SystemBooleanType, out _), Is.False);
        Assert.That(IlGenerator.IsReferenceNullComparisonShape(
            new Instruction(0, OpCode.CheckEqual, result, value, new Immediate(0))
                { IntegerBitWidth = 32 }, _app.SystemTypes.SystemBooleanType, out _), Is.False);
        var pointer = new LocalVariable("pointer", new Register(1702, "pointer"),
            new PointerTypeAnalysisContext(_app.SystemTypes.SystemByteType));
        Assert.That(IlGenerator.IsReferenceNullComparisonShape(
            new Instruction(0, OpCode.CheckEqual, result, pointer, new Immediate(0))
                { IntegerBitWidth = 64 }, _app.SystemTypes.SystemBooleanType, out _), Is.False);
    }

    [Test]
    public void UnprovedZeroComparisonRetainsGeneralFlagCalculation()
    {
        var decoder = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString("4885C9")));
        var test = new X86InstructionSet().GetIsilFromInstruction(decoder.Decode());
        Assert.That(test.Any(instruction => instruction.OpCode == OpCode.Subtract), Is.True,
            "Only the proved complete reference-return body may skip integer flag arithmetic.");

        decoder = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString("4883F901")));
        var nonzero = new X86InstructionSet().GetIsilFromInstruction(decoder.Decode());
        Assert.That(nonzero.Any(instruction => instruction.OpCode == OpCode.Subtract), Is.True,
            "A nonzero comparison must retain its ordinary integer flag calculation.");
    }

    [Test]
    public void ParameterStorageRejectsAddressTakingAndDefinitions()
    {
        var parameter = new LocalVariable("parameter", new Register(1720, "parameter"),
            _app.SystemTypes.SystemObjectType);
        var result = new LocalVariable("result", new Register(1721, "result"),
            _app.SystemTypes.SystemBooleanType);
        var compare = new Instruction(1, OpCode.CheckEqual, result, parameter, new Immediate(0))
            { IntegerBitWidth = 64 };
        var tail = new Instruction(2, OpCode.Return, result);
        Assert.That(IlGenerator.HasUnchangedParameterStorage(
            new ISILControlFlowGraph([compare, tail]), parameter), Is.True);

        var address = new LocalVariable("address", new Register(1722, "address"),
            _app.SystemTypes.SystemIntPtrType);
        Assert.That(IlGenerator.HasUnchangedParameterStorage(new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, address, new AddressOf(parameter)), compare, tail
        ]), parameter), Is.False);
        Assert.That(IlGenerator.HasUnchangedParameterStorage(new ISILControlFlowGraph([
            new Instruction(0, OpCode.Move, parameter, new Immediate(0)), compare, tail
        ]), parameter), Is.False);
    }

    [Test]
    public void SharedNativeReturnRequiresTheEntireReferenceZeroTestBody()
    {
        const ulong start = 0x1000;
        Iced.Intel.Instruction[] Decode(string hex) =>
            X86Utils.Disassemble(Convert.FromHexString(hex), start, false).ToArray();

        Assert.That(X86ReferenceNullReturnProof.MatchesShape(
            Decode("4885C90F95C0C3"), start, 7), Is.True);
        Assert.That(X86ReferenceNullReturnProof.MatchesShape(
            Decode("4885D20F95C0C3"), start, 7), Is.False,
            "Testing a different argument register does not prove this parameter.");
        Assert.That(X86ReferenceNullReturnProof.MatchesShape(
            Decode("4885C90F97C0C3"), start, 7), Is.False,
            "An alternate flag consumer is outside the bounded proof.");
        Assert.That(X86ReferenceNullReturnProof.MatchesShape(
            Decode("4885C90F95C090C3"), start, 8), Is.False,
            "No additional native instruction may be present.");
    }
}
