using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Isil;

public class X86FlagClobberTests
{
    [TestCase("83C101")] // add ecx, 1
    [TestCase("83E901")] // sub ecx, 1
    [TestCase("21D1")] // and ecx, edx
    [TestCase("09D1")] // or ecx, edx
    [TestCase("31D1")] // xor ecx, edx
    [TestCase("D1E9")] // shr ecx, 1
    [TestCase("D1F9")] // sar ecx, 1
    [TestCase("FFC1")] // inc ecx
    [TestCase("FFC9")] // dec ecx
    public void OverwrittenZeroFlagCannotReuseEarlierComparison(string operation)
    {
        var graph = Analyze("83F800" + operation + "0F94C0"); // cmp eax,0; operation; setz al
        Assert.That(UnresolvedFlags(graph), Does.Contain("ZF"));
    }

    [Test]
    public void BranchRetainsUnresolvedFlagDefinition()
    {
        var graph = Analyze("83F80083C10174019090"); // cmp eax,0; add ecx,1; jz final-nop; nop; nop
        Assert.That(UnresolvedFlags(graph), Does.Contain("ZF"));
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.ConditionalJump), Is.True);
    }

    [TestCase("FFC1")] // inc ecx preserves carry
    [TestCase("FFC9")] // dec ecx preserves carry
    [TestCase("89D1")] // mov ecx, edx preserves flags
    [TestCase("C1E900")] // shr ecx,0 preserves flags
    [TestCase("C1E920")] // shr ecx,32 has an effective count of zero
    [TestCase("48C1E940")] // shr rcx,64 has an effective count of zero
    public void UnmodifiedCarryFlagKeepsItsProvedComparison(string operation)
    {
        var graph = Analyze("39D1" + operation + "0F92C0"); // cmp ecx,edx; operation; setb al
        Assert.That(UnresolvedFlags(graph), Is.Empty);
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.CheckLessUnsigned), Is.True);
    }

    [Test]
    public void LogicalOperationHasKnownClearedCarry()
    {
        var graph = Analyze("39D121D10F92C0"); // cmp ecx,edx; and ecx,edx; setb al
        Assert.That(UnresolvedFlags(graph), Is.Empty);
        Assert.That(graph.Instructions.Last().Operands[0], Is.TypeOf<Immediate>());
        Assert.That(((Immediate)graph.Instructions.Last().Operands[0]).Value, Is.Zero);
    }

    [Test]
    public void VariableShiftDoesNotInventConstantFlagsWhenCountMayBeZero()
    {
        var graph = Analyze("39D1D3F8", "OF"); // cmp ecx,edx; sar eax,cl; observe its overflow flag
        Assert.That(UnresolvedFlags(graph), Does.Contain("OF"));
    }

    [Test]
    public void UnusedFlagDefinitionsDoNotRejectOrdinaryArithmetic()
    {
        var graph = Analyze("83C101", "rcx"); // add ecx,1; return ecx
        Assert.That(UnresolvedFlags(graph), Is.Empty);
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.Add), Is.True);
    }

    [Test]
    public void LaterComparisonReplacesUnresolvedFlag()
    {
        var graph = Analyze("83C10139D10F94C0"); // add ecx,1; cmp ecx,edx; setz al
        Assert.That(UnresolvedFlags(graph), Is.Empty);
        Assert.That(graph.Instructions.Any(i => i.OpCode == OpCode.CheckEqual), Is.True);
    }

    [Test]
    public void UnrecoveredParityCannotUseAnEarlierFlagValue()
    {
        var graph = Analyze("39D1", "PF"); // cmp ecx,edx; observe its unrecovered parity flag
        Assert.That(UnresolvedFlags(graph), Does.Contain("PF"));
    }

    [TestCase("E800000000")] // direct call
    [TestCase("FFD3")] // call rbx
    [TestCase("FF13")] // call [rbx]
    public void OpaqueCallsInvalidateStatusFlags(string call)
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var context = app.InjectAssembly("FlagCallsFixture").InjectType("Fixture", "Calls",
            app.SystemTypes.SystemObjectType, System.Reflection.TypeAttributes.Public)
            .InjectMethodContext("Method", app.SystemTypes.SystemVoidType,
                System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
        var graph = Analyze("39D1" + call + "0F94C0", context: context);
        Assert.That(UnresolvedFlags(graph), Does.Contain("ZF"));
    }

    private static string[] UnresolvedFlags(ISILControlFlowGraph graph) => graph.Instructions
        .Where(i => i.OpCode == OpCode.UnresolvedValue)
        .Select(i => ((LocalVariable)i.Destination!).Register.Name).ToArray();

    private static ISILControlFlowGraph Analyze(string hex, string resultRegister = "rax", MethodAnalysisContext? context = null)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(bytes));
        var instructions = new List<Instruction>();
        var targets = new Dictionary<ulong, Instruction>();
        var lifter = new X86InstructionSet();
        while (decoder.IP < (ulong)bytes.Length)
        {
            var native = decoder.Decode();
            var lifted = lifter.GetIsilFromInstruction(native, context);
            targets[native.IP] = lifted[0];
            instructions.AddRange(lifted);
        }
        instructions.Add(new(instructions.Count, OpCode.Return, new Register(null, resultRegister)));
        for (var index = 0; index < instructions.Count; index++)
        {
            instructions[index].Index = index;
            if (instructions[index].OpCode is OpCode.ConditionalJump or OpCode.Jump)
                instructions[index].SetOperand(0, targets[((Immediate)instructions[index].Operands[0]).UnsignedValue]);
        }

        var graph = new ISILControlFlowGraph(instructions);
        SsaForm.Build(graph, new DominatorInfo(graph));
        var locals = new Dictionary<Register, LocalVariable>();
        IOperand Replace(IOperand operand)
        {
            if (operand is Register register)
            {
                if (!locals.TryGetValue(register, out var local))
                    locals[register] = local = new(register.ToString(), register);
                return local;
            }
            if (operand is MemoryOperand memory)
            {
                if (memory.Base != null)
                    memory.Base = Replace(memory.Base);
                if (memory.Index != null)
                    memory.Index = Replace(memory.Index);
                return memory;
            }
            return operand;
        }
        foreach (var instruction in graph.Instructions)
            for (var index = 0; index < instruction.Operands.Count; index++)
                instruction.SetOperand(index, Replace(instruction.Operands[index]));
        FlagConditionRecovery.Run(graph);
        DeadCodeEliminator.Run(graph);
        SsaSimplifier.Run(graph, []);
        while (ConstantFolder.Run(graph))
            SsaSimplifier.Run(graph, []);
        DeadCodeEliminator.Run(graph);
        return graph;
    }
}
