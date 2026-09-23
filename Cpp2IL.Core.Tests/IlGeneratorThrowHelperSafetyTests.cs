using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("unused-result")]
    [TestCase("void-call")]
    [TestCase("returned-result")]
    [TestCase("addressed-result")]
    [TestCase("raiser-with-extra-arguments")]
    [TestCase("raiser-with-live-result")]
    public void ThrowHelperHintsCannotReplaceUnresolvedCallsOrDiscardTheirOperands(string shape)
    {
        const ulong helperAddress = 0x7FFF_FFFF_FFFF_FF00;
        const ulong consumerAddress = 0x7FFF_FFFF_FFFF_FF10;
        var raiserHint = shape.StartsWith("raiser-");
        var returnsResult = shape is "returned-result" or "raiser-with-live-result";
        var voidCall = shape is "void-call" or "raiser-with-extra-arguments";
        var types = _app.SystemTypes;
        var (context, definition, parameters) = CreateMethod("UnresolvedHelper",
            returnsResult ? types.SystemObjectType : types.SystemVoidType,
            [new ByRefTypeAnalysisContext(types.SystemInt32Type), types.SystemObjectType]);
        var keyFunctions = _app.GetOrCreateKeyFunctionAddresses();
        Assert.That(_app.MethodsByAddress.ContainsKey(helperAddress), Is.False);
        Assert.That(keyFunctions.IsKeyFunctionAddress(helperAddress), Is.False);
        if (raiserHint)
            _app.ExceptionRaisersByAddress[helperAddress] = true;
        else
            _app.ThrowHelperNamesByAddress[helperAddress] = "InvalidOperationException";

        var produced = new LocalVariable("produced", new Register(720, "produced"), types.SystemObjectType);
        List<IOperand> operands = [new Immediate((long)helperAddress)];
        if (!voidCall)
            operands.Add(produced);
        // The native helper could choose another exception, preserve this message/inner object,
        // mutate the referenced state, and return conditionally. A hint proves none of those choices.
        operands.Add(parameters[1]);
        operands.Add(new StringLiteral("authored message"));
        operands.Add(parameters[0]);
        operands.Add(new Immediate(29));
        var call = new Instruction(1, voidCall ? OpCode.CallVoid : OpCode.Call, operands);
        var expectedOperands = call.Operands.ToArray();
        var before = new Instruction(0, OpCode.Move, new MemoryOperand(parameters[0]), new Immediate(17));
        List<Instruction> code = [before, call];
        if (shape == "addressed-result")
            code.Add(new(code.Count, OpCode.CallVoid, new Immediate((long)consumerAddress), new AddressOf(produced)));
        var after = new Instruction(code.Count, OpCode.Move, new MemoryOperand(parameters[0]), new Immediate(23));
        code.Add(after);
        code.Add(returnsResult ? new(code.Count, OpCode.Return, produced) : new(code.Count, OpCode.Return));
        context.ControlFlowGraph = new ISILControlFlowGraph(code);

        MetadataResolver.ResolveAll(context);

        Assert.That(call.OpCode, Is.EqualTo(voidCall ? OpCode.CallVoid : OpCode.Call));
        Assert.That(call.Operands.ToArray(), Is.EqualTo(expectedOperands));
        Assert.That(context.ControlFlowGraph.Instructions.IndexOf(before), Is.LessThan(context.ControlFlowGraph.Instructions.IndexOf(call)));
        Assert.That(context.ControlFlowGraph.Instructions.IndexOf(after), Is.GreaterThan(context.ControlFlowGraph.Instructions.IndexOf(call)));
        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Call target is unresolved"));
    }

    [Test]
    public void RetiredThrowHelperContractsIgnoreLegacyPositiveHints()
    {
        const ulong address = 0x7FFF_FFFF_FFFF_FF20;
        _app.ThrowHelperNamesByAddress[address] = "InvalidOperationException";
        _app.ExceptionRaisersByAddress[address] = true;
        Assert.That(ThrowHelperRecovery.GetThrownException(_app, address), Is.Null);
        Assert.That(ThrowHelperRecovery.IsExceptionRaiser(_app, address), Is.False);
    }
}
