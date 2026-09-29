using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("single-zero")]
    [TestCase("double-zero")]
    [TestCase("integer")]
    [TestCase("string")]
    public void LiteralReturnsNeedNoLocalAndPreserveTheirEmittedValue(string kind)
    {
        var types = _app.SystemTypes;
        var (type, value, expected) = kind switch
        {
            "single-zero" => (types.SystemSingleType, (IOperand)new FloatLiteral(0f), (object)0f),
            "double-zero" => (types.SystemDoubleType, new DoubleLiteral(0d), (object)0d),
            "integer" => (types.SystemInt32Type, Imm(37), (object)37),
            _ => (types.SystemStringType, new StringLiteral("synthetic literal"), (object)"synthetic literal"),
        };
        var (context, definition, _) = CreateMethod("LiteralReturn", type, []);
        context.ControlFlowGraph = new ISILControlFlowGraph([new(0, OpCode.Return, value)]);
        StackAnalyzer.Analyze(context);
        context.DominatorInfo = new DominatorInfo(context.ControlFlowGraph);
        SsaForm.Build(context);
        LocalVariables.CreateAll(context);
        LocalVariables.ResolveTypesAndFields(context);
        Assert.That(context.Locals, Is.Empty);
        Assert.That(context.ParameterLocals, Is.Empty);
        Assert.That(context.AnalysisWarnings, Is.Empty);
        SsaForm.Remove(context);
        IlGenerator.GenerateIl(context, definition);

        using var runtime = Load();
        var actual = runtime.Type.GetMethod("LiteralReturn")!.Invoke(null, null);
        Assert.That(actual, Is.EqualTo(expected));
        if (actual is float single)
            Assert.That(BitConverter.SingleToUInt32Bits(single), Is.Zero);
        if (actual is double doublePrecision)
            Assert.That(BitConverter.DoubleToUInt64Bits(doublePrecision), Is.Zero);
    }
}
