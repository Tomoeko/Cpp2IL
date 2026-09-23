using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void CoalescedGuardRetainsNullFailureAndExecutesNonvirtualTargetOnce(bool notEqual, bool isVoid)
    {
        var fixture = CreateNullGuard(notEqual, isVoid);
        var originalCall = fixture.Call;
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.EqualTo(1));
        Assert.That(fixture.Call, Is.SameAs(originalCall));
        Assert.That(fixture.Context.ControlFlowGraph!.Instructions.Any(i => i.OpCode == OpCode.RuntimeNullThrow), Is.False);
        Assert.That(fixture.Call.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        SsaForm.Remove(fixture.Context);
        CopyCoalescer.Run(fixture.Context);
        Simplifier.Simplify(fixture.Context);
        CallArgumentTrimmer.Run(fixture.Context);
        DeadCodeEliminator.Run(fixture.Context);
        LocalVariables.RemoveUnused(fixture.Context);
        IlGenerator.GenerateIl(fixture.Context, fixture.Definition);
        Assert.That(fixture.Definition.CilMethodBody!.Instructions.Count(i => i.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
        AddDefaultConstructor();
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Guarded")!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, 11]));
        Assert.That(exception!.InnerException, Is.TypeOf<NullReferenceException>());
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(0),
            "The target deliberately does not dereference this; ordinary call would wrongly enter it for null.");
        var instance = Activator.CreateInstance(runtime.Type);
        Assert.That(method.Invoke(null, [instance, 11]), Is.EqualTo(isVoid ? 91 : 18));
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(1));
    }

    [Test]
    public void CoalescedLoopRetainsZeroIterationNullBypassAndLoopCarriedArgument()
    {
        var target = AddReceiverIndependentTarget(false);
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("Loop", types.SystemInt32Type, [_typeContext, types.SystemInt32Type]);
        var receiver = parameters[0];
        var count = parameters[1];
        var initialCondition = NullGuardLocal("initial", 1200, types.SystemBooleanType);
        var index = NullGuardLocal("index", 1201, types.SystemInt32Type);
        var next = NullGuardLocal("next", 1202, types.SystemInt32Type);
        var nullCondition = NullGuardLocal("null", 1203, types.SystemBooleanType);
        var keepGoing = NullGuardLocal("keepGoing", 1204, types.SystemBooleanType);
        var result = NullGuardLocal("result", 1205, types.SystemInt32Type);
        var phi = new Instruction(2, OpCode.Phi, index);
        var nullThrow = new Instruction(12, OpCode.RuntimeNullThrow, new StringLiteral("synthetic-proof"));
        var zeroReturn = new Instruction(11, OpCode.Return, Imm(0));
        var call = new Instruction(5, OpCode.Call, target, result, receiver, index, Imm(0));
        context.ControlFlowGraph = new([
            new(0, OpCode.CheckLessOrEqual, initialCondition, count, Imm(0)),
            new(1, OpCode.ConditionalJump, zeroReturn, initialCondition),
            phi,
            new(3, OpCode.CheckEqual, nullCondition, receiver, Imm(0)) { IntegerBitWidth = 64 },
            new(4, OpCode.ConditionalJump, nullThrow, nullCondition),
            call,
            new(6, OpCode.Add, next, index, Imm(1)),
            new(7, OpCode.CheckLess, keepGoing, next, count),
            new(8, OpCode.ConditionalJump, phi, keepGoing),
            new(9, OpCode.Return, next),
            zeroReturn,
            nullThrow,
        ]);
        var header = context.ControlFlowGraph.FindBlockByInstruction(phi)!;
        foreach (var predecessor in header.Predecessors)
            phi.AddOperands([predecessor.Instructions.Any(i => ReferenceEquals(i.Destination, next)) ? next : Imm(0)]);
        context.Locals.AddRange([initialCondition, index, next, nullCondition, keepGoing, result]);

        Assert.That(RuntimeNullGuardCoalescer.Run(context, SyntheticNullThrow), Is.EqualTo(1));
        Assert.That(call.CallSemantics, Is.EqualTo(CallSemantics.NullCheckedInstance));
        SsaForm.Remove(context);
        CopyCoalescer.Run(context);
        Simplifier.Simplify(context);
        CallArgumentTrimmer.Run(context);
        DeadCodeEliminator.Run(context);
        LocalVariables.RemoveUnused(context);
        IlGenerator.GenerateIl(context, definition);
        AddDefaultConstructor();
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Loop")!;
        Assert.That(method.Invoke(null, [null, 0]), Is.EqualTo(0));
        Assert.That(method.Invoke(null, [null, -3]), Is.EqualTo(0));
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(0));
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, 1]));
        Assert.That(exception!.InnerException, Is.TypeOf<NullReferenceException>());
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(0));
        Assert.That(method.Invoke(null, [Activator.CreateInstance(runtime.Type), 5]), Is.EqualTo(5));
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(5));
    }

    [TestCase("wrong-width")]
    [TestCase("unknown-width")]
    [TestCase("wrong-zero")]
    [TestCase("different-receiver")]
    [TestCase("escaped-receiver")]
    [TestCase("memory-argument")]
    [TestCase("extra-argument")]
    [TestCase("nonzero-method-info")]
    [TestCase("virtual-target")]
    [TestCase("constructor-target")]
    [TestCase("retagged-parameter")]
    [TestCase("unknown-helper")]
    [TestCase("null-arm-effect")]
    [TestCase("call-arm-effect")]
    [TestCase("call-arm-load")]
    [TestCase("call-arm-string")]
    [TestCase("call-arm-address")]
    [TestCase("call-arm-width")]
    [TestCase("null-arm-entry")]
    [TestCase("call-arm-entry")]
    [TestCase("receiver-overwrite")]
    [TestCase("receiver-forward-reference")]
    public void NullGuardCoalescingRejectsAmbiguousIdentityEffectsOrControlFlow(string invalidity)
    {
        var fixture = CreateNullGuard(false, false);
        var graph = fixture.Context.ControlFlowGraph!;
        var nullBlock = graph.FindBlockByInstruction(fixture.NullThrow)!;
        var callBlock = graph.FindBlockByInstruction(fixture.Call)!;
        var receiver = fixture.Parameters[0];
        var argument = fixture.Parameters[1];
        var scratch = NullGuardLocal("scratch", 1230, _app.SystemTypes.SystemInt32Type);
        fixture.Context.Locals.Add(scratch);
        switch (invalidity)
        {
            case "wrong-width": fixture.Comparison.IntegerBitWidth = 32; break;
            case "unknown-width": fixture.Comparison.IntegerBitWidth = 0; break;
            case "wrong-zero": fixture.Comparison.SetOperand(2, Imm(1)); break;
            case "different-receiver":
                fixture.Call.SetOperand(2, new LocalVariable("same-name", receiver.Register.Copy(2), _typeContext)); break;
            case "escaped-receiver":
                callBlock.Instructions.Insert(0, new(-1, OpCode.Move, scratch, new AddressOf(receiver))); break;
            case "memory-argument": fixture.Call.SetOperand(3, new MemoryOperand(argument)); break;
            case "extra-argument": fixture.Call.AddOperands([Imm(0)]); break;
            case "nonzero-method-info": fixture.Call.SetOperand(4, Imm(1)); break;
            case "virtual-target": fixture.Target.Attributes |= System.Reflection.MethodAttributes.Virtual; break;
            case "constructor-target": fixture.Target.Name = ".ctor"; break;
            case "retagged-parameter": fixture.Target.Parameters[0].ParameterType = _app.SystemTypes.SystemInt64Type; break;
            case "unknown-helper": fixture.NullThrow.SetOperands(new StringLiteral("unproved")); break;
            case "null-arm-effect": nullBlock.Instructions.Insert(0, new(-1, OpCode.CallVoid, fixture.Target, receiver, argument)); break;
            case "call-arm-effect":
                var (effect, _, _) = CreateMethod("Effect", _app.SystemTypes.SystemVoidType, []);
                callBlock.Instructions.Insert(0, new(-1, OpCode.CallVoid, effect));
                break;
            case "call-arm-load": callBlock.Instructions.Insert(0, new(-1, OpCode.Move, scratch, new MemoryOperand(argument))); break;
            case "call-arm-string": callBlock.Instructions.Insert(0, new(-1, OpCode.Move, scratch, new StringLiteral("may-allocate"))); break;
            case "call-arm-address": callBlock.Instructions.Insert(0, new(-1, OpCode.Move, scratch, new AddressOf(argument))); break;
            case "call-arm-width": callBlock.Instructions.Insert(0, new(-1, OpCode.Move, scratch, argument) { IntegerBitWidth = 32 }); break;
            case "null-arm-entry": nullBlock.Predecessors.Add(graph.EntryBlock); graph.EntryBlock.Successors.Add(nullBlock); break;
            case "call-arm-entry": callBlock.Predecessors.Add(graph.EntryBlock); graph.EntryBlock.Successors.Add(callBlock); break;
            case "receiver-overwrite": callBlock.Instructions.Insert(0, new(-1, OpCode.Move, receiver, Imm(0))); break;
            case "receiver-forward-reference":
                var lateReceiver = NullGuardLocal("late", 1231, _typeContext);
                fixture.Context.Locals.Add(lateReceiver);
                fixture.Comparison.SetOperand(1, lateReceiver);
                fixture.Call.SetOperand(2, lateReceiver);
                var guard = graph.FindBlockByInstruction(fixture.Comparison)!;
                guard.Instructions.Insert(guard.Instructions.IndexOf(fixture.Comparison) + 1,
                    new(-1, OpCode.Move, lateReceiver, receiver));
                break;
        }
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.Zero);
        Assert.That(fixture.Call.CallSemantics, Is.EqualTo(CallSemantics.Direct));
        Assert.That(graph.Instructions, Does.Contain(fixture.NullThrow));
    }

    [Test]
    public void UncoalescedIntrinsicFailsBeforeMissingLocalTypesAndNeverAllocatesException()
    {
        var fixture = CreateNullGuard(false, false);
        fixture.Parameters[0].Type = null;
        Assert.That(() => IlGenerator.GenerateIl(fixture.Context, fixture.Definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Uncoalesced target runtime null guard"));
        Assert.That(fixture.Definition.CilMethodBody, Is.Null);
    }

    [Test]
    public void EffectsBeforeTheGuardStillRunBeforeTheNullFailure()
    {
        var fixture = CreateNullGuard(false, false);
        var before = new FieldDefinition("BeforeCount", FieldAttributes.Public | FieldAttributes.Static, _module.CorLibTypeFactory.Int32);
        _type.Fields.Add(before);
        var (effect, effectDefinition, _) = CreateMethod("Before", _app.SystemTypes.SystemVoidType, []);
        effectDefinition.CilMethodBody = new CilMethodBody();
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_1);
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Stsfld, before);
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var block = fixture.Context.ControlFlowGraph!.FindBlockByInstruction(fixture.Comparison)!;
        var firstEffect = new Instruction(-1, OpCode.CallVoid, effect);
        block.Instructions.Insert(0, firstEffect);
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.EqualTo(1));
        Assert.That(block.Instructions[0], Is.SameAs(firstEffect));
        CallArgumentTrimmer.Run(fixture.Context);
        LocalVariables.RemoveUnused(fixture.Context);
        IlGenerator.GenerateIl(fixture.Context, fixture.Definition);
        using var runtime = Load();
        var exception = Assert.Throws<TargetInvocationException>(() => runtime.Type.GetMethod("Guarded")!.Invoke(null, [null, 3]));
        Assert.That(exception!.InnerException, Is.TypeOf<NullReferenceException>());
        Assert.That(runtime.Type.GetField("BeforeCount")!.GetValue(null), Is.EqualTo(1));
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(0));
    }

    [TestCase("virtual")]
    [TestCase("static")]
    [TestCase("constructor")]
    [TestCase("receiver-type")]
    [TestCase("retagged-caller-parameter")]
    [TestCase("field-argument")]
    public void NullCheckedEmissionRevalidatesMutableCallContext(string invalidity)
    {
        var fixture = CreateNullGuard(false, false);
        fixture.Call.CallSemantics = CallSemantics.NullCheckedInstance;
        fixture.Context.ControlFlowGraph = new([fixture.Call, new(20, OpCode.Return, fixture.Call.Operands[1])]);
        switch (invalidity)
        {
            case "virtual": fixture.Target.Attributes |= System.Reflection.MethodAttributes.Virtual; break;
            case "static": fixture.Target.Attributes |= System.Reflection.MethodAttributes.Static; break;
            case "constructor": fixture.Target.Name = ".ctor"; break;
            case "receiver-type": fixture.Parameters[0].Type = _app.SystemTypes.SystemObjectType; break;
            case "retagged-caller-parameter": fixture.Context.Parameters[0].ParameterType = _app.SystemTypes.SystemObjectType; break;
            case "field-argument":
                fixture.Call.SetOperand(3, new FieldReference(new InjectedFieldAnalysisContext("Value", _app.SystemTypes.SystemInt32Type,
                    System.Reflection.FieldAttributes.Public, _typeContext, 16), fixture.Parameters[0], 16));
                break;
        }
        Assert.That(() => IlGenerator.GenerateIl(fixture.Context, fixture.Definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-checked invocation"));
    }

    [Test]
    public void NullCheckedVoidCallCanDiscardANonvoidReturnWithoutLosingTheCheck()
    {
        var fixture = CreateNullGuard(false, false);
        fixture.Call.OpCode = OpCode.CallVoid;
        fixture.Call.RemoveOperandAt(1);
        fixture.Call.CallSemantics = CallSemantics.NullCheckedInstance;
        fixture.Context.ControlFlowGraph = new([fixture.Call, new(20, OpCode.Return, Imm(73))]);
        LocalVariables.RemoveUnused(fixture.Context);
        CallArgumentTrimmer.Run(fixture.Context);
        IlGenerator.GenerateIl(fixture.Context, fixture.Definition);
        AddDefaultConstructor();
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Guarded")!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null, 5]));
        Assert.That(exception!.InnerException, Is.TypeOf<NullReferenceException>());
        Assert.That(method.Invoke(null, [Activator.CreateInstance(runtime.Type), 5]), Is.EqualTo(73));
        Assert.That(runtime.Type.GetField("InvocationCount")!.GetValue(null), Is.EqualTo(1));
    }

    [Test]
    public void NullCheckedCallMarkerParticipatesInIdentityAndCannotMigrateToPureOperations()
    {
        var fixture = CreateNullGuard(false, false);
        var copy = new Instruction(fixture.Call.Index, fixture.Call.OpCode, fixture.Call.Operands.ToList());
        Assert.That(copy.IsStructurallyEqualTo(fixture.Call), Is.True);
        fixture.Call.CallSemantics = CallSemantics.NullCheckedInstance;
        Assert.That(copy.IsStructurallyEqualTo(fixture.Call), Is.False);
        Assert.That(() => fixture.Call.OpCode = OpCode.Move, Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => new Instruction(0, OpCode.Move, fixture.Parameters[1], Imm(0))
            { CallSemantics = CallSemantics.NullCheckedInstance }, Throws.TypeOf<InvalidOperationException>());
    }

    private (InjectedMethodAnalysisContext Context, MethodDefinition Definition, LocalVariable[] Parameters,
        InjectedMethodAnalysisContext Target, Instruction Call, Instruction Comparison, Instruction NullThrow)
        CreateNullGuard(bool notEqual, bool isVoid)
    {
        var target = AddReceiverIndependentTarget(isVoid);
        var (context, definition, parameters) = CreateMethod("Guarded", _app.SystemTypes.SystemInt32Type,
            [_typeContext, _app.SystemTypes.SystemInt32Type]);
        var condition = NullGuardLocal("condition", 1210, _app.SystemTypes.SystemBooleanType);
        var result = NullGuardLocal("result", 1211, _app.SystemTypes.SystemInt32Type);
        var comparison = new Instruction(0, notEqual ? OpCode.CheckNotEqual : OpCode.CheckEqual, condition, parameters[0], Imm(0))
            { IntegerBitWidth = 64 };
        var call = isVoid ? new Instruction(3, OpCode.CallVoid, target, parameters[0], parameters[1], Imm(0))
            : new Instruction(3, OpCode.Call, target, result, parameters[0], parameters[1], Imm(0));
        var throwing = new Instruction(5, OpCode.RuntimeNullThrow, new StringLiteral("synthetic-proof"));
        var returned = new Instruction(4, OpCode.Return, isVoid ? Imm(91) : result);
        var branch = new Instruction(1, OpCode.ConditionalJump, notEqual ? call : throwing, condition);
        context.ControlFlowGraph = new(notEqual
            ? [comparison, branch, throwing, call, returned]
            : [comparison, branch, call, returned, throwing]);
        context.Locals.AddRange([condition, result]);
        return (context, definition, parameters, target, call, comparison, throwing);
    }

    private InjectedMethodAnalysisContext AddReceiverIndependentTarget(bool isVoid)
    {
        var calls = new FieldDefinition("InvocationCount", FieldAttributes.Public | FieldAttributes.Static, _module.CorLibTypeFactory.Int32);
        _type.Fields.Add(calls);
        var (target, definition, _) = CreateMethod("Touch", isVoid ? _app.SystemTypes.SystemVoidType : _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type], instance: true);
        definition.CilMethodBody = new CilMethodBody();
        var il = definition.CilMethodBody.Instructions;
        il.Add(CilOpCodes.Ldsfld, calls);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stsfld, calls);
        if (!isVoid)
        {
            il.Add(CilOpCodes.Ldarg_1);
            il.Add(CilOpCodes.Ldc_I4_7);
            il.Add(CilOpCodes.Add);
        }
        il.Add(CilOpCodes.Ret);
        return target;
    }

    private static bool SyntheticNullThrow(Instruction instruction) => instruction is
        { OpCode: OpCode.RuntimeNullThrow, Operands: [StringLiteral { Value: "synthetic-proof" }] };

    private static LocalVariable NullGuardLocal(string name, int number, TypeAnalysisContext type) =>
        new(name, new Register(number, name, 1), type);
}
