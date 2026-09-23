using System;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("System.NullReferenceException", OpCode.CheckEqual, 0, 1)]
    [TestCase("System.IndexOutOfRangeException", OpCode.CheckNotEqual, 1, 0)]
    [TestCase("System.IndexOutOfRangeException", OpCode.CheckLess, -1, 0)]
    [TestCase("System.IndexOutOfRangeException", OpCode.CheckGreaterOrEqual, 0, -1)]
    public void ExceptionNameAndComparisonCannotEraseAnExplicitGuard(string exceptionName, OpCode comparison,
        int throwingInput, int returningInput)
    {
        var (context, definition, parameters) = CreateMethod("Guard", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type]);
        var condition = new LocalVariable("condition", new Register(540, "condition"), _app.SystemTypes.SystemBooleanType);
        context.Locals.Add(condition);
        var exception = _app.SystemTypes.SystemExceptionType.DeclaringAssembly.GetTypeByFullName(exceptionName)!;
        Assert.That(exception, Is.Not.Null);
        var thrown = new Instruction(3, OpCode.Throw, exception);
        var branch = new Instruction(1, OpCode.ConditionalJump, thrown, condition);
        context.ControlFlowGraph = new ISILControlFlowGraph([
            new(0, comparison, condition, parameters[0], Imm(0)), branch,
            new(2, OpCode.Return, Imm(42)), thrown,
        ]);

        // There is no managed dereference or array access which could preserve this throw.
        // The former exception-name matcher deleted the branch and returned42 for both paths.
        InjectedCheckRemover.Run(context);
        IlGenerator.GenerateIl(context, definition);
        using var runtime = Load();
        var method = runtime.Type.GetMethod("Guard")!;
        Assert.That(method.Invoke(null, [returningInput]), Is.EqualTo(42));
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [throwingInput]));
        Assert.That(error!.InnerException!.GetType().FullName, Is.EqualTo(exceptionName));
    }
}
