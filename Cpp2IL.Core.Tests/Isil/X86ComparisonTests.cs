using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ComparisonTests
{
    // Intel condition codes: unsigned above/below differ from signed greater/less at the sign boundary.
    private static readonly int[] Conditions = [2, 3, 6, 7, 12, 13, 14, 15, 4, 5, 8, 9];

    private static IEnumerable<TestCaseData> BoundaryCases()
    {
        foreach (var width in new[] { 32, 64 })
        foreach (var form in new[] { "branch", "set", "move" })
        foreach (var condition in Conditions)
            yield return new TestCaseData(width, form, condition).SetName($"NativeComparison_{width}_{form}_{condition:X}");
    }

    [TestCaseSource(nameof(BoundaryCases))]
    public void DecodedPredicatesMatchSignedAndUnsignedBoundaryValues(int width, string form, int condition)
    {
        var code = Lift(width == 32 ? [0x39, 0xD1] : [0x48, 0x39, 0xD1]); // cmp [r/e]cx, [r/e]dx
        code.AddRange(Lift(form switch
        {
            "branch" => [(byte)(0x70 + condition), 0],
            "set" => [0x0F, (byte)(0x90 + condition), 0xC0], // setcc al
            _ => [0x0F, (byte)(0x40 + condition), 0xC3], // cmovcc eax, ebx
        }));
        long minimum = width == 32 ? int.MinValue : long.MinValue;
        long maximum = width == 32 ? int.MaxValue : long.MaxValue;
        long[] values = [minimum, minimum + 1, -1, 0, 1, maximum - 1, maximum];
        foreach (var left in values)
        foreach (var right in values)
        {
            var expected = Predicate(width, condition, left, right);
            Assert.That(Evaluate(code, form, left, right), Is.EqualTo(expected), $"{left}, {right}");
        }
    }

    [TestCase(2, OpCode.CheckLessUnsigned)]
    [TestCase(3, OpCode.CheckGreaterOrEqualUnsigned)]
    [TestCase(6, OpCode.CheckLessOrEqualUnsigned)]
    [TestCase(7, OpCode.CheckGreaterUnsigned)]
    [TestCase(12, OpCode.CheckLess)]
    [TestCase(13, OpCode.CheckGreaterOrEqual)]
    [TestCase(14, OpCode.CheckLessOrEqual)]
    [TestCase(15, OpCode.CheckGreater)]
    public void RecoveryPreservesComparisonSignednessAndWidth(int condition, OpCode expected)
    {
        foreach (var width in new[] { 32, 64 })
        {
            var code = Lift(width == 32 ? [0x39, 0xD1] : [0x48, 0x39, 0xD1]);
            code.AddRange(Lift([0x0F, (byte)(0x90 + condition), 0xC0]));
            var result = ConvertToSingleAssignmentLocals(code);
            code.Add(new(code.Count, OpCode.Return, result));
            FlagConditionRecovery.Run(new ISILControlFlowGraph(code));
            var definition = code.Single(i => ReferenceEquals(i.Destination, result));
            Assert.That(definition.OpCode, Is.EqualTo(expected));
            Assert.That(definition.IntegerBitWidth, Is.EqualTo(width));
            Assert.That(definition.Operands[1].ToString(), Does.StartWith("rcx"));
            Assert.That(definition.Operands[2].ToString(), Does.StartWith("rdx"));
        }
    }

    [TestCase("38D1")] // cmp cl, dl
    [TestCase("6639D1")] // cmp cx, dx
    [TestCase("84C9")] // test cl, cl
    [TestCase("0F2FC1")] // comiss xmm0, xmm1
    [TestCase("0F2EC1")] // ucomiss xmm0, xmm1
    [TestCase("F30F5FC1")] // maxss xmm0, xmm1
    [TestCase("F30F5DC1")] // minss xmm0, xmm1
    [TestCase("0F4703")] // cmova eax, [rbx] reads memory even when not selected
    public void UnsupportedWidthOrFloatingSemanticsAreExplicit(string bytes)
    {
        Assert.That(Lift(Convert.FromHexString(bytes)).Single().OpCode, Is.EqualTo(OpCode.NotImplemented));
    }

    private static List<Instruction> Lift(byte[] bytes)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(bytes));
        decoder.Decode(out var native);
        return new X86InstructionSet().GetIsilFromInstruction(native);
    }

    [TestCase("3911")] // cmp [rcx], edx
    [TestCase("3B11")] // cmp edx, [rcx]
    [TestCase("8511")] // test [rcx], edx
    public void FlagExpansionReadsMemoryOperandOnce(string bytes)
    {
        var instructions = Lift(Convert.FromHexString(bytes));
        Assert.That(instructions.SelectMany(i => i.Operands).OfType<MemoryOperand>().Count(), Is.EqualTo(1));
        Assert.That(instructions[0].OpCode, Is.EqualTo(OpCode.Move));
        Assert.That(instructions[0].Operands[1], Is.TypeOf<MemoryOperand>());
    }

    // A small interpreter executes the lifted ISIL operations. Expected results above use ordinary
    // C# comparisons, independently of the lifter's CF/SF/OF formulas. Generated CIL is covered separately.
    private static bool Evaluate(List<Instruction> code, string form, long left, long right)
    {
        var values = new Dictionary<string, long> { ["rcx"] = left, ["rdx"] = right, ["rax"] = 7, ["rbx"] = 42 };
        long Read(IOperand operand) => operand switch
        {
            Immediate immediate => immediate.Value,
            Register register => values[register.Name],
            _ => throw new InvalidOperationException(operand.ToString()),
        };
        foreach (var instruction in code)
        {
            if (instruction.OpCode == OpCode.Nop)
                continue;
            if (instruction.OpCode == OpCode.ConditionalJump)
            {
                var taken = Read(instruction.Operands[1]) != 0;
                if (form == "branch")
                    return taken;
                if (taken)
                    return false; // cmov skip path leaves the original sentinel value.
                continue;
            }
            var a = Read(instruction.Operands[1]);
            var b = instruction.Operands.Count > 2 ? Read(instruction.Operands[2]) : 0;
            if (instruction.IntegerBitWidth == 32)
            {
                a = unchecked((int)a);
                b = unchecked((int)b);
            }
            var value = instruction.OpCode switch
            {
                OpCode.Move => a,
                OpCode.Subtract => unchecked(a - b),
                OpCode.Xor => a ^ b,
                OpCode.And => a & b,
                OpCode.Or => a | b,
                OpCode.CheckEqual => a == b ? 1 : 0,
                OpCode.CheckNotEqual => a != b ? 1 : 0,
                OpCode.CheckLess => a < b ? 1 : 0,
                OpCode.CheckLessUnsigned => instruction.IntegerBitWidth == 32
                    ? unchecked((uint)a) < unchecked((uint)b) ? 1 : 0
                    : unchecked((ulong)a) < unchecked((ulong)b) ? 1 : 0,
                _ => throw new InvalidOperationException(instruction.ToString()),
            };
            if (instruction.IntegerBitWidth == 32)
                value = unchecked((int)value);
            values[((Register)instruction.Destination!).Name] = value;
        }
        return values["rax"] == (form == "move" ? 42 : 1);
    }

    private static bool Predicate(int width, int condition, long left, long right)
    {
        var unsignedLeft = width == 32 ? unchecked((uint)left) : unchecked((ulong)left);
        var unsignedRight = width == 32 ? unchecked((uint)right) : unchecked((ulong)right);
        var difference = width == 32 ? unchecked((int)(left - right)) : unchecked(left - right);
        return condition switch
        {
            2 => unsignedLeft < unsignedRight,
            3 => unsignedLeft >= unsignedRight,
            6 => unsignedLeft <= unsignedRight,
            7 => unsignedLeft > unsignedRight,
            12 => left < right,
            13 => left >= right,
            14 => left <= right,
            15 => left > right,
            4 => left == right,
            5 => left != right,
            8 => difference < 0,
            9 => difference >= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
    }

    private static LocalVariable ConvertToSingleAssignmentLocals(List<Instruction> code)
    {
        var current = new Dictionary<string, LocalVariable>();
        LocalVariable Read(Register register)
        {
            if (!current.TryGetValue(register.Name, out var local))
                current[register.Name] = local = new(register.Name, register);
            return local;
        }
        for (var index = 0; index < code.Count; index++)
        {
            var instruction = code[index];
            instruction.Index = index;
            var destination = (Register)instruction.Destination!;
            for (var operand = 1; operand < instruction.Operands.Count; operand++)
                if (instruction.Operands[operand] is Register source)
                    instruction.SetOperand(operand, Read(source));
            var local = new LocalVariable(destination.Name + index, destination);
            current[destination.Name] = local;
            instruction.SetOperand(0, local);
        }
        return current["rax"];
    }
}
