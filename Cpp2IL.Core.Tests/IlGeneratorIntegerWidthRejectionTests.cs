using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, 64)]
    [TestCase(64, 32)]
    public void IntegerLocalReturnCannotImplicitlyChangeStackWidth(int sourceWidth, int returnWidth)
    {
        var sourceType = sourceWidth == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var returnType = returnWidth == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("MismatchedReturn", returnType, [sourceType]);
        var captured = new LocalVariable("captured", new Register(950, "captured"), sourceType);
        context.Locals.Add(captured);

        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.Move, captured, parameters[0]),
            new(1, OpCode.Return, captured),
        ]), Throws.TypeOf<DecompilerException>());
    }

    [TestCase(32, 64)]
    [TestCase(64, 32)]
    public void IntegerMoveCannotImplicitlyChangeStackWidth(int sourceWidth, int destinationWidth)
    {
        var sourceType = sourceWidth == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var destinationType = destinationWidth == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var (context, definition, parameters) = CreateMethod("MismatchedMove", destinationType, [sourceType]);
        var destination = new LocalVariable("destination", new Register(951, "destination"), destinationType);
        context.Locals.Add(destination);

        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.Move, destination, parameters[0]),
            new(1, OpCode.Return, destination),
        ]), Throws.TypeOf<DecompilerException>());
    }

    [TestCase(32)]
    [TestCase(64)]
    public void NativeSubtractRejectsDestinationWithTheWrongStackWidth(int nativeWidth)
    {
        var sourceType = nativeWidth == 32 ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemInt64Type;
        var destinationType = nativeWidth == 32 ? _app.SystemTypes.SystemInt64Type : _app.SystemTypes.SystemInt32Type;
        var (context, definition, parameters) = CreateMethod("MismatchedSubtract", destinationType, [sourceType]);
        var destination = new LocalVariable("destination", new Register(952, "destination"), destinationType);
        context.Locals.Add(destination);

        Assert.That(() => Emit(context, definition,
        [
            new Instruction(0, OpCode.Subtract, destination, parameters[0], Imm(1)) { IntegerBitWidth = nativeWidth },
            new(1, OpCode.Return, destination),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("Native Subtract destination width"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ForwardingNative32StepAcrossParentRegisterMoveCannotWidenReturn(bool increment)
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("ParentRegisterReturn", types.SystemInt64Type,
            [types.SystemInt32Type]);
        var stepped = new LocalVariable("stepped", new Register(953, "rcx"));
        var returned = new LocalVariable("returned", new Register(954, "rax"));
        context.Locals.AddRange([stepped, returned]);
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            LiftIntegerStep(increment, stepped, parameters[0]),
            new(1, OpCode.Move, returned, stepped),
            new(2, OpCode.Return, returned),
        ]);

        // Native INC/DEC ECX writes a zero-filled full RCX, but an ordinary managed
        // Int32 copy does not establish the corresponding Int64 stack value.
        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(stepped.Type, Is.SameAs(types.SystemInt32Type));
        Assert.That(returned.Type, Is.SameAs(types.SystemInt64Type));
        SsaSimplifier.Run(context);
        Assert.That(context.ControlFlowGraph.Instructions.Single(i => i.OpCode == OpCode.Return).Operands,
            Is.EqualTo(new IOperand[] { stepped }));

        Assert.That(() => IlGenerator.GenerateIl(context, definition), Throws.TypeOf<DecompilerException>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Native32StepWithInt32ResultPreservesWrappedBoundaries(bool increment)
    {
        var type = _app.SystemTypes.SystemInt32Type;
        var (context, definition, parameters) = CreateMethod("Int32Step", type, [type]);
        var stepped = new LocalVariable("stepped", new Register(955, "rcx"), type);
        context.Locals.Add(stepped);
        Emit(context, definition,
        [
            LiftIntegerStep(increment, stepped, parameters[0]),
            new(1, OpCode.Return, stepped),
        ]);

        using var runtime = Load();
        var recovered = runtime.Type.GetMethod("Int32Step")!;
        foreach (var input in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            Assert.That(recovered.Invoke(null, [input]),
                Is.EqualTo(increment ? unchecked(input + 1) : unchecked(input - 1)), $"input {input}");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExplicitUnsignedExtensionPreservesZeroFilledParentAfterNative32Step(bool increment)
    {
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("ZeroFilledStep", types.SystemUInt64Type,
            [types.SystemInt32Type]);
        var stepped = new LocalVariable("stepped", new Register(956, "rcx"), types.SystemInt32Type);
        var extended = new LocalVariable("extended", new Register(957, "rax"), types.SystemUInt64Type);
        context.Locals.AddRange([stepped, extended]);
        Emit(context, definition,
        [
            LiftIntegerStep(increment, stepped, parameters[0]),
            new(1, OpCode.IntegerExtend, extended, stepped, Imm(32), Imm(64), Imm(0)),
            new(2, OpCode.Return, extended),
        ]);

        using var runtime = Load();
        var recovered = runtime.Type.GetMethod("ZeroFilledStep")!;
        foreach (var input in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
        {
            var expected = (ulong)unchecked((uint)(increment ? unchecked(input + 1) : unchecked(input - 1)));
            Assert.That(recovered.Invoke(null, [input]), Is.EqualTo(expected), $"input {input}");
        }
    }

    private static Instruction LiftIntegerStep(bool increment, LocalVariable destination, LocalVariable source)
    {
        // Synthetic INC ECX / DEC ECX encodings; no player bytes or symbols.
        var decoder = Iced.Intel.Decoder.Create(64,
            new Iced.Intel.ByteArrayCodeReader(increment ? new byte[] { 0xFF, 0xC1 } : new byte[] { 0xFF, 0xC9 }));
        var step = new X86InstructionSet().GetIsilFromInstruction(decoder.Decode())
            .Single(i => i.OpCode is OpCode.Add or OpCode.Subtract);
        step.SetOperand(0, destination);
        step.SetOperand(1, source);
        return step;
    }
}
