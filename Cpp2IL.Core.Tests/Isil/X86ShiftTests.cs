using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ShiftTests
{
    [TestCase("D3E8", OpCode.ShiftRightUnsigned, 32)]
    [TestCase("48D3E8", OpCode.ShiftRightUnsigned, 64)]
    [TestCase("D3F8", OpCode.ShiftRight, 32)]
    [TestCase("48D3F8", OpCode.ShiftRight, 64)]
    [TestCase("D3E0", OpCode.ShiftLeft, 32)]
    [TestCase("48D3E0", OpCode.ShiftLeft, 64)]
    [TestCase("C1E820", OpCode.ShiftRightUnsigned, 32)]
    [TestCase("48C1F840", OpCode.ShiftRight, 64)]
    [TestCase("D129", OpCode.ShiftRightUnsigned, 32)]
    [TestCase("48D139", OpCode.ShiftRight, 64)]
    public void NativeShiftPreservesOperationAndOperandWidth(string bytes, OpCode opcode, int width)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(bytes)));
        decoder.Decode(out var native);
        var shift = new X86InstructionSet().GetIsilFromInstruction(native).First();
        Assert.That(shift.OpCode, Is.EqualTo(opcode));
        Assert.That(shift.IntegerBitWidth, Is.EqualTo(width));
    }

    [TestCase("D2E8")]
    [TestCase("66D3F8")]
    [TestCase("D220")]
    [TestCase("66D329")]
    public void NarrowRegisterAndMemoryShiftsAreExplicitlyUnsupported(string bytes)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(Convert.FromHexString(bytes)));
        decoder.Decode(out var native);
        Assert.That(new X86InstructionSet().GetIsilFromInstruction(native).Single().OpCode, Is.EqualTo(OpCode.NotImplemented));
    }

    [TestCase(OpCode.ShiftRight)]
    [TestCase(OpCode.ShiftRightUnsigned)]
    [TestCase(OpCode.ShiftLeft)]
    public void ConstantShiftsCannotEraseUnknownOrNativeWidth(OpCode opcode)
    {
        foreach (var width in new[] { 0, 32, 64 })
        foreach (var count in new[] { 0, 32, 64 })
        {
            var result = new LocalVariable("result", new Register(1, "result"));
            var shift = new Instruction(0, opcode, result, Imm(-1), Imm(count)) { IntegerBitWidth = width };
            var graph = new ISILControlFlowGraph([shift, new(1, OpCode.Return, result)]);
            Assert.That(ConstantFolder.Run(graph), Is.False);
            Assert.That(shift.OpCode, Is.EqualTo(opcode));
            Assert.That(shift.IntegerBitWidth, Is.EqualTo(width));
        }
    }
}
