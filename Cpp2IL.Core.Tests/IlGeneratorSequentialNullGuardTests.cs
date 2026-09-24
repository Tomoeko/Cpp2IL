using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void SequentialFieldGuardsShareOneThrowAndPreserveTheRightReadMarker()
    {
        var pair = CreateSequentialFieldGuards();

        Assert.That(RuntimeNullGuardCoalescer.Run(pair.Context, SyntheticNullThrow), Is.EqualTo(2));
        Assert.That(pair.Context.ControlFlowGraph!.Instructions.Any(instruction =>
            instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
        Assert.That(pair.Context.NullCheckedFieldAccesses, Has.Count.EqualTo(1));
        Assert.That(pair.Context.NullCheckedFieldAccesses[0].Operation, Is.SameAs(pair.RightRead));
        Assert.That(pair.Context.NullCheckedFieldAccesses[0].Field, Is.SameAs(pair.Field));
        Assert.That(pair.Context.NullArmFieldProbes, Has.Count.EqualTo(1));
        Assert.That(pair.Context.NullArmFieldProbes[0].NativeRead, Is.SameAs(pair.LeftRead));
        Assert.That(pair.Context.NullArmFieldProbes[0].EarlierRead.Operation, Is.SameAs(pair.RightRead));
        Assert.That(pair.Context.NullArmFieldProbes[0].IsValidFor(pair.Context), Is.True);

        SsaForm.Remove(pair.Context);
        CopyCoalescer.Run(pair.Context);
        Simplifier.Simplify(pair.Context);
        DeadCodeEliminator.Run(pair.Context);
        LocalVariables.RemoveUnused(pair.Context);
        var probeStillValid = pair.Context.NullArmFieldProbes[0].IsValidFor(pair.Context);
        Assert.That(probeStillValid, Is.True, "the emitted null-arm probe must survive graph transforms");
        IlGenerator.GenerateIl(pair.Context, pair.Definition);
        Assert.That(pair.Definition.CilMethodBody, Is.Not.Null);

        AddDefaultConstructor();
        using var runtime = Load();
        var method = runtime.Type.GetMethod("ReadPair")!;
        var left = Activator.CreateInstance(runtime.Type)!;
        var right = Activator.CreateInstance(runtime.Type)!;
        runtime.Type.GetField("Value")!.SetValue(left, 17);
        runtime.Type.GetField("Value")!.SetValue(right, -7);
        Assert.That(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, right]))!
            .InnerException, Is.TypeOf<NullReferenceException>(), "left receiver is null");
        Assert.That(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [left, null]))!
            .InnerException, Is.TypeOf<NullReferenceException>(), "right receiver is null");
        Assert.That(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, null]))!
            .InnerException, Is.TypeOf<NullReferenceException>(), "both receivers are null");
        Assert.That(method.Invoke(null, [left, right]), Is.EqualTo(10));
    }

    [TestCase("field-offset")]
    [TestCase("probe-receiver")]
    [TestCase("comparison-width")]
    public void ChangedNullArmProbeEvidenceCannotReachEmission(string mutation)
    {
        var pair = CreateSequentialFieldGuards();
        Assert.That(RuntimeNullGuardCoalescer.Run(pair.Context, SyntheticNullThrow), Is.EqualTo(2));
        var probe = pair.Context.NullArmFieldProbes.Single();
        switch (mutation)
        {
            case "field-offset":
                ((FieldReference)pair.LeftRead.Operands[1]).Offset++;
                break;
            case "probe-receiver":
                probe.Probe.SetOperand(1, new FieldReference(pair.Field, pair.Right, 16));
                break;
            case "comparison-width":
                pair.LeftComparison.IntegerBitWidth = 32;
                break;
        }

        Assert.That(probe.IsValidFor(pair.Context), Is.False, mutation);
        Assert.That(() => IlGenerator.GenerateIl(pair.Context, pair.Definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-arm field probe"), mutation);
    }

    [TestCase("effect-between-guards")]
    [TestCase("missing-left-read")]
    [TestCase("wrong-left-receiver")]
    [TestCase("wrong-left-offset")]
    [TestCase("wrong-left-width")]
    [TestCase("reversed-left-branch")]
    [TestCase("escaped-left-receiver")]
    public void SequentialFieldGuardKeepsTheFirstNullFailureWhenItsLaterReadIsUnproved(string mutation)
    {
        var pair = CreateSequentialFieldGuards();
        var graph = pair.Context.ControlFlowGraph!;
        switch (mutation)
        {
            case "effect-between-guards":
                var (effect, _, _) = CreateMethod("BeforeRightGuard", _app.SystemTypes.SystemVoidType, []);
                graph.FindBlockByInstruction(pair.RightComparison)!.Instructions.Insert(0,
                    new(-1, OpCode.CallVoid, effect));
                break;
            case "missing-left-read":
                graph.FindBlockByInstruction(pair.LeftRead)!.Instructions.Remove(pair.LeftRead);
                break;
            case "wrong-left-receiver":
                pair.LeftRead.SetOperand(1, new FieldReference(pair.Field, pair.Right, 16));
                break;
            case "wrong-left-offset":
                ((FieldReference)pair.LeftRead.Operands[1]).Offset++;
                break;
            case "wrong-left-width":
                pair.LeftComparison.IntegerBitWidth = 32;
                break;
            case "reversed-left-branch":
                pair.LeftComparison.OpCode = OpCode.CheckNotEqual;
                break;
            case "escaped-left-receiver":
                var (mutate, _, _) = CreateMethod("MutateLeft", _app.SystemTypes.SystemVoidType,
                    [new ByRefTypeAnalysisContext(_typeContext)]);
                var readBlock = graph.FindBlockByInstruction(pair.LeftRead)!;
                readBlock.Instructions.Insert(readBlock.Instructions.IndexOf(pair.LeftRead) + 1,
                    new(-1, OpCode.CallVoid, mutate, new AddressOf(pair.Left)));
                break;
        }

        Assert.That(RuntimeNullGuardCoalescer.Run(pair.Context, SyntheticNullThrow), Is.EqualTo(1), mutation);
        Assert.That(pair.Context.NullCheckedFieldAccesses, Has.Count.EqualTo(1), mutation);
        Assert.That(pair.Context.NullCheckedFieldAccesses[0].Operation, Is.SameAs(pair.RightRead), mutation);
        Assert.That(pair.Context.NullArmFieldProbes, Is.Empty, mutation);
        Assert.That(pair.Context.ControlFlowGraph!.Instructions, Does.Contain(pair.NullThrow), mutation);
    }

    [Test]
    public void NonGuardEntryToSharedThrowPreventsBothFieldRewrites()
    {
        var pair = CreateSequentialFieldGuards(extraThrowEntry: true);
        var graph = pair.Context.ControlFlowGraph!;
        var throwBlock = graph.FindBlockByInstruction(pair.NullThrow)!;
        Assert.That(throwBlock.Predecessors, Has.Count.EqualTo(3));
        Assert.That(throwBlock.Predecessors.Any(predecessor => predecessor.Instructions.LastOrDefault()?.OpCode ==
            OpCode.Jump), Is.True);

        Assert.That(RuntimeNullGuardCoalescer.Run(pair.Context, SyntheticNullThrow), Is.Zero);
        Assert.That(pair.Context.NullCheckedFieldAccesses, Is.Empty);
        Assert.That(pair.Context.NullArmFieldProbes, Is.Empty);
        Assert.That(graph.Instructions, Does.Contain(pair.NullThrow));
    }

    private sealed record SequentialFieldPair(InjectedMethodAnalysisContext Context,
        MethodDefinition Definition, LocalVariable Left,
        LocalVariable Right, InjectedFieldAnalysisContext Field, Instruction LeftComparison,
        Instruction RightComparison, Instruction LeftRead, Instruction RightRead,
        Instruction NullThrow);

    private SequentialFieldPair CreateSequentialFieldGuards(bool extraThrowEntry = false)
    {
        var types = _app.SystemTypes;
        var parameters = extraThrowEntry
            ? new[] { _typeContext, _typeContext, types.SystemInt32Type }
            : new[] { _typeContext, _typeContext };
        var (context, definition, arguments) = CreateMethod("ReadPair", types.SystemInt32Type, parameters);
        var field = AddSequentialField("Value", 16);
        var leftCondition = NullGuardLocal("leftNull", 1380, types.SystemBooleanType);
        var rightCondition = NullGuardLocal("rightNull", 1381, types.SystemBooleanType);
        var leftValue = NullGuardLocal("leftValue", 1382, types.SystemInt32Type);
        var rightValue = NullGuardLocal("rightValue", 1383, types.SystemInt32Type);
        var sum = NullGuardLocal("sum", 1384, types.SystemInt32Type);
        context.Locals.AddRange([leftCondition, rightCondition, leftValue, rightValue, sum]);

        var nextIndex = 0;
        var prelude = extraThrowEntry ? NullGuardLocal("otherPath", 1385, types.SystemBooleanType) : null;
        var instructions = new List<Instruction>();
        if (prelude != null)
        {
            context.Locals.Add(prelude);
            instructions.Add(new(nextIndex++, OpCode.CheckEqual, prelude, arguments[2], Imm(0))
                { IntegerBitWidth = 32 });
            // Set this target after the normal path has been built.
            instructions.Add(new(nextIndex++, OpCode.ConditionalJump, prelude, prelude));
        }

        var leftComparison = new Instruction(nextIndex++, OpCode.CheckEqual, leftCondition,
            arguments[0], Imm(0)) { IntegerBitWidth = 64 };
        instructions.Add(leftComparison);
        var leftBranch = new Instruction(nextIndex++, OpCode.ConditionalJump, leftCondition,
            leftCondition);
        instructions.Add(leftBranch);
        var rightComparison = new Instruction(nextIndex++, OpCode.CheckEqual, rightCondition,
            arguments[1], Imm(0)) { IntegerBitWidth = 64 };
        instructions.Add(rightComparison);
        var rightBranch = new Instruction(nextIndex++, OpCode.ConditionalJump, rightCondition,
            rightCondition);
        instructions.Add(rightBranch);
        var rightRead = new Instruction(nextIndex++, OpCode.Move, rightValue,
            new FieldReference(field, arguments[1], 16));
        var leftRead = new Instruction(nextIndex++, OpCode.Move, leftValue,
            new FieldReference(field, arguments[0], 16));
        instructions.Add(rightRead);
        instructions.Add(leftRead);
        instructions.Add(new Instruction(nextIndex++, OpCode.Add, sum, rightValue, leftValue)
            { IntegerBitWidth = 32 });
        instructions.Add(new(nextIndex++, OpCode.Return, sum));
        Instruction? otherThrowEntry = null;
        if (prelude != null)
        {
            otherThrowEntry = new(nextIndex++, OpCode.Jump, prelude);
            instructions.Add(otherThrowEntry);
            instructions[1].SetOperand(0, otherThrowEntry);
        }
        var nullThrow = new Instruction(nextIndex, OpCode.RuntimeNullThrow,
            new StringLiteral("synthetic-proof"));
        instructions.Add(nullThrow);
        leftBranch.SetOperand(0, nullThrow);
        rightBranch.SetOperand(0, nullThrow);
        otherThrowEntry?.SetOperand(0, nullThrow);
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        return new(context, definition, arguments[0], arguments[1], field,
            leftComparison, rightComparison, leftRead, rightRead, nullThrow);
    }

    private InjectedFieldAnalysisContext AddSequentialField(string name, int offset)
    {
        var definition = new FieldDefinition(name, FieldAttributes.Public, _module.CorLibTypeFactory.Int32);
        _type.Fields.Add(definition);
        var field = new InjectedFieldAnalysisContext(name, _app.SystemTypes.SystemInt32Type,
            System.Reflection.FieldAttributes.Public, _typeContext, offset);
        field.PutExtraData("AsmResolverField", definition);
        _typeContext.Fields.Add(field);
        return field;
    }
}
