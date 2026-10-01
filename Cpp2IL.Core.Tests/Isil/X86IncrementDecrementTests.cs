using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86IncrementDecrementTests
{
    private static readonly int[] Conditions = [2, 3, 4, 5, 6, 7, 8, 9, 12, 13, 14, 15];

    [TestCase("FFC1", 32, 1)]
    [TestCase("FFC9", 32, -1)]
    [TestCase("48FFC1", 64, 1)]
    [TestCase("48FFC9", 64, -1)]
    public void RegisterStepPreservesWidthAndDefinesOnlyChangedFlags(string bytes, int width, int step)
    {
        var instructions = Lift(bytes);
        var arithmetic = instructions.Single(i => i.OpCode is OpCode.Add or OpCode.Subtract);
        Assert.That(arithmetic.OpCode, Is.EqualTo(step == 1 ? OpCode.Add : OpCode.Subtract));
        Assert.That(arithmetic.IntegerBitWidth, Is.EqualTo(width));
        Assert.That(arithmetic.Operands[0], Is.TypeOf<Register>());
        Assert.That(((Register)arithmetic.Operands[0]).Name, Is.EqualTo("rcx"));
        Assert.That(arithmetic.Operands[1], Is.EqualTo(arithmetic.Operands[0]));
        Assert.That(arithmetic.Operands[2], Is.EqualTo(new Immediate(1)));

        foreach (var (flag, opcode) in new[]
                 {
                     ("ZF", OpCode.CheckEqual), ("SF", OpCode.CheckLess), ("OF", OpCode.CheckEqual),
                 })
        {
            var definition = instructions.Single(i => i.Destination is Register r && r.Name == flag);
            Assert.That(definition.OpCode, Is.EqualTo(opcode), flag);
            Assert.That(definition.IntegerBitWidth, Is.EqualTo(width), flag);
            Assert.That(definition.Operands[1], Is.EqualTo(arithmetic.Destination), flag);
        }
        Assert.That(instructions.Any(i => i.Destination is Register { Name: "CF" }), Is.False);
        Assert.That(instructions.Single(i => i.Destination is Register { Name: "PF" }).OpCode,
            Is.EqualTo(OpCode.UnresolvedValue));
        Assert.That(instructions.Single(i => i.Destination is Register { Name: "AF" }).OpCode,
            Is.EqualTo(OpCode.UnresolvedValue));
    }

    [TestCase("FFC1", 32, 1, "branch")]
    [TestCase("FFC9", 32, -1, "branch")]
    [TestCase("48FFC1", 64, 1, "branch")]
    [TestCase("48FFC9", 64, -1, "branch")]
    [TestCase("FFC1", 32, 1, "set")]
    [TestCase("FFC9", 32, -1, "set")]
    [TestCase("48FFC1", 64, 1, "set")]
    [TestCase("48FFC9", 64, -1, "set")]
    public void NativeConditionsUseWrappedResultAndPreservedCarry(string bytes, int width, int step, string form)
    {
        long minimum = width == 32 ? int.MinValue : long.MinValue;
        long maximum = width == 32 ? int.MaxValue : long.MaxValue;
        // The wider inputs also ensure a 32-bit operation ignores preexisting parent-register bits.
        long[] inputs = [minimum, minimum + 1, -2, -1, 0, 1, maximum - 1, maximum,
            0x100000000, 0x180000000, long.MinValue, long.MaxValue];
        foreach (var condition in Conditions)
        {
            var instructions = Lift(bytes);
            instructions.AddRange(Lift(form == "branch"
                ? Convert.ToHexString(new byte[] { (byte)(0x70 + condition), 0 })
                : Convert.ToHexString(new byte[] { 0x0F, (byte)(0x90 + condition), 0xC0 })));
            foreach (var input in inputs.Distinct())
            foreach (var carry in new[] { false, true })
            {
                var before = width == 32 ? unchecked((int)input) : input;
                var after = width == 32 ? unchecked((int)(before + step)) : unchecked(before + step);
                var overflow = step == 1 ? before == maximum : before == minimum;
                var expected = Predicate(condition, after == 0, after < 0, overflow, carry);
                Assert.That(Evaluate(instructions, input, carry, form), Is.EqualTo(expected),
                    $"{width}-bit step {step}, input {input:X16}, condition {condition:X}, carry {carry}");
            }
        }
    }

    [TestCase("FFC1", 32, 1)]
    [TestCase("FFC9", 32, -1)]
    [TestCase("48FFC1", 64, 1)]
    [TestCase("48FFC9", 64, -1)]
    public void OverflowOccursOnlyAtTheSignedInputBoundary(string bytes, int width, int step)
    {
        long minimum = width == 32 ? int.MinValue : long.MinValue;
        long maximum = width == 32 ? int.MaxValue : long.MaxValue;
        var instructions = Lift(bytes);
        instructions.Add(new(instructions.Count, OpCode.Move, new Register(null, "rax"), new Register(null, "OF")));
        foreach (var input in new[] { minimum, minimum + 1, -1, 0, 1, maximum - 1, maximum,
                     0x100000000, 0x180000000, long.MinValue, long.MaxValue }.Distinct())
        {
            var before = width == 32 ? unchecked((int)input) : input;
            Assert.That(Evaluate(instructions, input, false, "set"),
                Is.EqualTo(step == 1 ? before == maximum : before == minimum), $"{input:X16}");
        }
    }

    [TestCase("FFC1")]
    [TestCase("FFC9")]
    [TestCase("48FFC1")]
    [TestCase("48FFC9")]
    public void ConsumedParityCannotReuseAnEarlierProvedValue(string bytes)
    {
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Move, new Register(null, "PF"), new Immediate(1)),
        };
        instructions.AddRange(Lift(bytes));
        var graph = Analyze(instructions, "PF");
        Assert.That(graph.Instructions.Count(i => i.OpCode == OpCode.UnresolvedValue &&
            i.Destination is LocalVariable { Register.Name: "PF" }), Is.EqualTo(1));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Move &&
            i.Destination is LocalVariable { Register.Name: "PF" }), Is.False,
            "The parity consumer must depend on the new unresolved definition, not the earlier constant.");
    }

    [TestCase("FFC1")]
    [TestCase("FFC9")]
    [TestCase("48FFC1")]
    [TestCase("48FFC9")]
    public void NativeParityBranchCannotReadAnEarlierFlag(string bytes)
    {
        foreach (var branch in new[] { "7A00", "7B00" }) // jp and jnp
        {
            var instructions = Lift(bytes);
            instructions.AddRange(Lift(branch));
            // Evaluate seeds PF=1, but the step must invalidate it before either native branch reads it.
            Assert.Throws<KeyNotFoundException>(() => Evaluate(instructions, 0, false, "branch"));
        }
    }

    [TestCase("FFC1")]
    [TestCase("FFC9")]
    [TestCase("48FFC1")]
    [TestCase("48FFC9")]
    public void UnsupportedParitySetRemainsRejectedAfterFlagRemoval(string bytes)
    {
        foreach (var set in new[] { "0F9AC0", "0F9BC0" }) // setp and setnp
        {
            var instructions = new List<Instruction>
            {
                new(0, OpCode.Move, new Register(null, "PF"), new Immediate(1)),
            };
            instructions.AddRange(Lift(bytes));
            var rejection = Lift(set).Single();
            Assert.That(rejection.OpCode, Is.EqualTo(OpCode.NotImplemented));
            instructions.Add(rejection);
            var graph = Analyze(instructions, "rax");
            Assert.That(graph.Instructions, Does.Contain(rejection),
                "Removing the unused flag must not turn an unsupported parity consumer into recovered behavior.");
            Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.UnresolvedValue &&
                i.Destination is LocalVariable { Register.Name: "PF" }), Is.False);
            Assert.That(graph.Instructions.Last().Operands[0], Is.TypeOf<LocalVariable>());
        }
    }

    [TestCase("FFC1")]
    [TestCase("FFC9")]
    [TestCase("48FFC1")]
    [TestCase("48FFC9")]
    public void UnusedUnresolvedFlagsDoNotRejectRegisterResult(string bytes)
    {
        var graph = Analyze(Lift(bytes), "rcx");
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.UnresolvedValue), Is.False);
        Assert.That(graph.Instructions.Count(i => i.OpCode is OpCode.Add or OpCode.Subtract), Is.EqualTo(1));
    }

    [TestCase("FEC1", 0)] // inc cl
    [TestCase("FEC9", 0)] // dec cl
    [TestCase("66FFC1", 0)] // inc cx
    [TestCase("66FFC9", 0)] // dec cx
    [TestCase("FF01", 32)] // inc dword ptr [rcx]
    [TestCase("FF09", 32)] // dec dword ptr [rcx]
    [TestCase("48FF01", 64)] // inc qword ptr [rcx]
    [TestCase("48FF09", 64)] // dec qword ptr [rcx]
    public void NarrowAndMemoryStepsKeepUnprovedFlagsAndPreservedCarry(string bytes, int width)
    {
        var instructions = Lift(bytes);
        Assert.That(instructions.Single(i => i.OpCode is OpCode.Add or OpCode.Subtract).IntegerBitWidth, Is.EqualTo(width));
        foreach (var flag in new[] { "ZF", "SF", "OF", "PF" })
            Assert.That(instructions.Single(i => i.Destination is Register r && r.Name == flag).OpCode,
                Is.EqualTo(OpCode.UnresolvedValue), flag);
        Assert.That(instructions.Any(i => i.Destination is Register { Name: "CF" }), Is.False);
    }

    private static List<Instruction> Lift(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(bytes));
        var native = decoder.Decode();
        Assert.That(decoder.IP, Is.EqualTo((ulong)bytes.Length));
        return new X86InstructionSet().GetIsilFromInstruction(native);
    }

    // This executes decoded ISIL against an independent boundary oracle. Initial status flags
    // deliberately conflict with some expected results, exposing a stale flag definition.
    private static bool Evaluate(List<Instruction> instructions, long input, bool carry, string form)
    {
        var values = new Dictionary<string, long>
        {
            ["rcx"] = input, ["rax"] = 7, ["CF"] = carry ? 1 : 0, ["ZF"] = 1,
            ["SF"] = 1, ["OF"] = 1, ["PF"] = 1, ["AF"] = 1,
        };
        long Read(IOperand operand) => operand switch
        {
            Register register => values[register.Name],
            Immediate immediate => immediate.Value,
            _ => throw new InvalidOperationException(operand.ToString()),
        };
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCode.UnresolvedValue)
            {
                values.Remove(((Register)instruction.Destination!).Name);
                continue;
            }
            if (instruction.OpCode == OpCode.ConditionalJump)
                return Read(instruction.Operands[1]) != 0;
            var left = Read(instruction.Operands[1]);
            var right = instruction.Operands.Count > 2 ? Read(instruction.Operands[2]) : 0;
            if (instruction.IntegerBitWidth == 32)
            {
                left = unchecked((int)left);
                right = unchecked((int)right);
            }
            var result = instruction.OpCode switch
            {
                OpCode.Add => unchecked(left + right),
                OpCode.Subtract => unchecked(left - right),
                OpCode.Move => left,
                OpCode.Xor => left ^ right,
                OpCode.And => left & right,
                OpCode.Or => left | right,
                OpCode.CheckEqual => left == right ? 1L : 0L,
                OpCode.CheckNotEqual => left != right ? 1L : 0L,
                OpCode.CheckLess => left < right ? 1L : 0L,
                _ => throw new InvalidOperationException(instruction.ToString()),
            };
            values[((Register)instruction.Destination!).Name] = instruction.IntegerBitWidth == 32
                ? unchecked((int)result) : result;
        }
        Assert.That(form, Is.EqualTo("set"));
        return values["rax"] == 1;
    }

    private static bool Predicate(int condition, bool zero, bool sign, bool overflow, bool carry) => condition switch
    {
        0 => overflow,
        1 => !overflow,
        2 => carry,
        3 => !carry,
        4 => zero,
        5 => !zero,
        6 => carry || zero,
        7 => !carry && !zero,
        8 => sign,
        9 => !sign,
        12 => sign != overflow,
        13 => sign == overflow,
        14 => zero || sign != overflow,
        15 => !zero && sign == overflow,
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    private static ISILControlFlowGraph Analyze(List<Instruction> instructions, string resultRegister)
    {
        instructions.Add(new(instructions.Count, OpCode.Return, new Register(null, resultRegister)));
        for (var index = 0; index < instructions.Count; index++)
            instructions[index].Index = index;
        var graph = new ISILControlFlowGraph(instructions);
        SsaForm.Build(graph, new DominatorInfo(graph));
        var locals = new Dictionary<Register, LocalVariable>();
        foreach (var instruction in graph.Instructions)
        for (var index = 0; index < instruction.Operands.Count; index++)
        {
            if (instruction.Operands[index] is not Register register)
                continue;
            if (!locals.TryGetValue(register, out var local))
                locals.Add(register, local = new LocalVariable(register.ToString(), register));
            instruction.SetOperand(index, local);
        }
        FlagConditionRecovery.Run(graph);
        DeadCodeEliminator.Run(graph);
        SsaSimplifier.Run(graph, []);
        while (ConstantFolder.Run(graph))
            SsaSimplifier.Run(graph, []);
        DeadCodeEliminator.Run(graph);
        return graph;
    }
}
