using System;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using FieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(0)]
    [TestCase(1)]
    public void GuardedBooleanLiteralStoreRetainsProducerEffectsAndNullFailure(int value)
    {
        var fixture = CreateBooleanLiteralNullGuard(value, capturedResult: true);
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.EqualTo(1));
        Assert.That(fixture.Context.NullCheckedFieldAccesses.Single().IsValidFor(fixture.Context), Is.True);
        SsaForm.Remove(fixture.Context);
        CopyCoalescer.Run(fixture.Context);
        Simplifier.Simplify(fixture.Context);
        CallArgumentTrimmer.Run(fixture.Context);
        DeadCodeEliminator.Run(fixture.Context);
        LocalVariables.RemoveUnused(fixture.Context);
        IlGenerator.GenerateIl(fixture.Context, fixture.Definition);
        Assert.That(fixture.Definition.CilMethodBody!.Instructions.Count(instruction =>
            instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
        AddDefaultConstructor();

        using var runtime = Load();
        var method = runtime.Type.GetMethod("StoreBoolean")!;
        var counter = runtime.Type.GetField("ProducerCount")!;
        Assert.That(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [null]))!
            .InnerException, Is.TypeOf<NullReferenceException>());
        Assert.That(counter.GetValue(null), Is.EqualTo(1));
        var instance = Activator.CreateInstance(runtime.Type)!;
        runtime.Type.GetField("State")!.SetValue(instance, value == 0);
        method.Invoke(null, [instance]);
        Assert.That(runtime.Type.GetField("State")!.GetValue(instance), Is.EqualTo(value == 1));
        Assert.That(counter.GetValue(null), Is.EqualTo(2));
    }

    [TestCase("literal-two")]
    [TestCase("literal-negative")]
    [TestCase("byte-field")]
    [TestCase("sbyte-field")]
    [TestCase("enum-field")]
    [TestCase("different-result")]
    [TestCase("prior-store")]
    [TestCase("prior-call")]
    [TestCase("cross-block-load")]
    public void BooleanStoreGuardRejectsChangedIdentityOrAnEarlierEffect(string mutation)
    {
        var fixture = CreateBooleanLiteralNullGuard(1, capturedResult: true);
        var graph = fixture.Context.ControlFlowGraph!;
        var storeBlock = graph.FindBlockByInstruction(fixture.Store)!;
        var access = (FieldReference)fixture.Store.Operands[0];
        switch (mutation)
        {
            case "literal-two": fixture.Store.SetOperand(1, Imm(2)); break;
            case "literal-negative": fixture.Store.SetOperand(1, Imm(-1)); break;
            case "byte-field": access.Field.OverrideFieldType = _app.SystemTypes.SystemByteType; break;
            case "sbyte-field": access.Field.OverrideFieldType = _app.SystemTypes.SystemSByteType; break;
            case "enum-field":
                var enumType = new InjectedTypeAnalysisContext(_typeContext.DeclaringAssembly,
                    "Synthetic", "ByteFlag", _app.SystemTypes.EnumType,
                    System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed);
                enumType.Fields.Add(new InjectedFieldAnalysisContext("value__", _app.SystemTypes.SystemByteType,
                    System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.SpecialName |
                    System.Reflection.FieldAttributes.RTSpecialName, enumType, 16));
                Assert.That(enumType.IsEnumType, Is.True);
                access.Field.OverrideFieldType = enumType;
                break;
            case "different-result":
                access.Local = new LocalVariable("otherResult", fixture.Receiver.Register.Copy(2), _typeContext);
                break;
            case "prior-store":
                storeBlock.Instructions.Insert(0, new(-1, OpCode.Move,
                    new FieldReference(access.Field, fixture.Context.ParameterLocals[0], 16), Imm(0)));
                break;
            case "prior-call":
                var (effect, _, _) = CreateMethod("OtherEffect", _app.SystemTypes.SystemVoidType, []);
                storeBlock.Instructions.Insert(0, new(-1, OpCode.CallVoid, effect));
                break;
            case "cross-block-load":
                var scratch = NullGuardLocal("scratch", 1310, _app.SystemTypes.SystemBooleanType);
                fixture.Context.Locals.Add(scratch);
                var load = new Instruction(2, OpCode.Move, scratch,
                    new FieldReference(access.Field, fixture.Context.ParameterLocals[0], 16));
                var instructions = graph.Instructions.OrderBy(instruction => instruction.Index).ToList();
                var branch = instructions.Single(instruction => instruction.OpCode == OpCode.ConditionalJump);
                branch.SetOperand(0, instructions.Single(instruction => instruction.OpCode == OpCode.RuntimeNullThrow));
                instructions.Insert(instructions.IndexOf(fixture.Store), load);
                instructions.Insert(instructions.IndexOf(fixture.Store),
                    new(2, OpCode.Jump, fixture.Store));
                fixture.Context.ControlFlowGraph = new(instructions);
                break;
        }
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.Zero);
        Assert.That(fixture.Context.ControlFlowGraph!.Instructions.Any(instruction =>
            instruction.OpCode == OpCode.RuntimeNullThrow), Is.True);
    }

    [TestCase("literal")]
    [TestCase("receiver")]
    [TestCase("field-type")]
    [TestCase("offset")]
    [TestCase("removed-store")]
    public void BooleanStoreEvidenceMustRemainValidUntilEmission(string mutation)
    {
        var fixture = CreateBooleanLiteralNullGuard(1);
        Assert.That(RuntimeNullGuardCoalescer.Run(fixture.Context, SyntheticNullThrow), Is.EqualTo(1));
        var access = (FieldReference)fixture.Store.Operands[0];
        switch (mutation)
        {
            case "literal": fixture.Store.SetOperand(1, Imm(2)); break;
            case "receiver": access.Local = new("other", fixture.Receiver.Register.Copy(2), _typeContext); break;
            case "field-type": access.Field.OverrideFieldType = _app.SystemTypes.SystemByteType; break;
            case "offset": access.Offset++; break;
            case "removed-store":
                fixture.Context.ControlFlowGraph!.FindBlockByInstruction(fixture.Store)!
                    .Instructions.Remove(fixture.Store);
                break;
        }
        Assert.That(() => IlGenerator.GenerateIl(fixture.Context, fixture.Definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-checked field access marker"));
    }

    private (MethodAnalysisContext Context, MethodDefinition Definition,
        LocalVariable Receiver, Instruction Store) CreateBooleanLiteralNullGuard(int value,
        bool capturedResult = false)
    {
        var fieldDefinition = new FieldDefinition("State", FieldAttributes.Public,
            _module.CorLibTypeFactory.Boolean);
        _type.Fields.Add(fieldDefinition);
        var field = new InjectedFieldAnalysisContext("State", _app.SystemTypes.SystemBooleanType,
            System.Reflection.FieldAttributes.Public, _typeContext, 16);
        field.PutExtraData("AsmResolverField", fieldDefinition);
        _typeContext.Fields.Add(field);
        var (context, definition, parameters) = CreateMethod("StoreBoolean",
            _app.SystemTypes.SystemVoidType, [_typeContext]);
        var receiver = parameters[0];
        var instructions = new System.Collections.Generic.List<Instruction>();
        if (capturedResult)
        {
            var counter = new FieldDefinition("ProducerCount", FieldAttributes.Public | FieldAttributes.Static,
                _module.CorLibTypeFactory.Int32);
            _type.Fields.Add(counter);
            var (producer, producerDefinition, _) = CreateMethod("Produce", _typeContext, [_typeContext]);
            producerDefinition.CilMethodBody = new CilMethodBody();
            var il = producerDefinition.CilMethodBody.Instructions;
            il.Add(CilOpCodes.Ldsfld, counter);
            il.Add(CilOpCodes.Ldc_I4_1);
            il.Add(CilOpCodes.Add);
            il.Add(CilOpCodes.Stsfld, counter);
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ret);
            receiver = NullGuardLocal("result", 1300, _typeContext);
            context.Locals.Add(receiver);
            instructions.Add(new(-1, OpCode.Call, producer, receiver, parameters[0]));
        }
        var condition = NullGuardLocal("null", 1301, _app.SystemTypes.SystemBooleanType);
        var throwing = new Instruction(5, OpCode.RuntimeNullThrow, new StringLiteral("synthetic-proof"));
        var store = new Instruction(3, OpCode.Move, new FieldReference(field, receiver, 16), Imm(value));
        instructions.AddRange([
            new(0, OpCode.CheckEqual, condition, receiver, Imm(0)) { IntegerBitWidth = 64 },
            new(1, OpCode.ConditionalJump, throwing, condition),
            store,
            new(4, OpCode.Return),
            throwing,
        ]);
        context.ControlFlowGraph = new(instructions);
        context.Locals.Add(condition);
        return (context, definition, receiver, store);
    }
}
