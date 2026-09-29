using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    public void UnusedIncomingParametersKeepLaterTypesAndManagedArgumentIndices(
        bool instance, bool useFirstParameter, bool useStackParameter)
    {
        var types = _app.SystemTypes;
        var parameterTypes = useStackParameter
            ? new[] { types.SystemInt32Type, types.SystemByteType, types.SystemSingleType,
                types.SystemInt64Type, types.SystemDoubleType }
            : new[] { types.SystemInt32Type, types.SystemByteType, types.SystemDoubleType };
        var (context, definition, _) = CreateMethod("ReadLast", types.SystemDoubleType,
            parameterTypes, instance);
        context.ParameterOperands = new X64CallingConventionResolver()
            .ResolveForParameters(context).ToList();
        var offset = instance ? 1 : 0;
        var last = offset + parameterTypes.Length - 1;
        Assert.That(context.ParameterOperands[last] is StackOffset, Is.EqualTo(useStackParameter));
        var instructions = new List<Instruction>();
        if (instance)
            instructions.Add(new(instructions.Count, OpCode.Move,
                new Register(920, "receiverCopy"), context.ParameterOperands[0]));
        if (useFirstParameter)
            instructions.Add(new(instructions.Count, OpCode.Move,
                new Register(921, "firstCopy"), context.ParameterOperands[offset]));
        var result = new Register(922, "result");
        instructions.Add(new(instructions.Count, OpCode.Move, result, context.ParameterOperands[last]));
        instructions.Add(new(instructions.Count, OpCode.Return, result));
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        StackAnalyzer.Analyze(context);
        context.DominatorInfo = new DominatorInfo(context.ControlFlowGraph);
        SsaForm.Build(context);
        LocalVariables.CreateAll(context);
        Assert.That(context.ParameterLocals.Count, Is.EqualTo(
            1 + (instance ? 1 : 0) + (useFirstParameter ? 1 : 0)));
        LocalVariables.ResolveTypesAndFields(context);
        var lastLocal = context.ParameterLocals.Single(local =>
            LocalVariables.GetIncomingParameterIndex(context, local) == parameterTypes.Length - 1);
        Assert.That(lastLocal.Type, Is.SameAs(types.SystemDoubleType));
        SsaForm.Remove(context);
        IlGenerator.GenerateIl(context, definition);
        if (instance)
            AddDefaultConstructor();

        using var runtime = Load();
        var receiver = instance ? Activator.CreateInstance(runtime.Type) : null;
        object[] arguments = useStackParameter
            ? [73, (byte)189, -3.5f, long.MinValue, 9.25d]
            : [73, (byte)189, 9.25d];
        Assert.That(runtime.Type.GetMethod("ReadLast")!.Invoke(receiver, arguments), Is.EqualTo(9.25d));
    }

    [TestCase("duplicate-slot")]
    [TestCase("versioned-local")]
    [TestCase("method-info")]
    [TestCase("receiver")]
    [TestCase("missing-slot")]
    [TestCase("unconverted-stack")]
    [TestCase("undeclared-trailing-slot")]
    public void ParameterIndexRequiresAUniqueDeclaredIncomingSlot(string defect)
    {
        var types = _app.SystemTypes;
        var (context, _, parameters) = CreateMethod("UnprovedParameter", types.SystemVoidType,
            [types.SystemInt32Type, types.SystemDoubleType]);
        var local = parameters[1];
        switch (defect)
        {
            case "duplicate-slot": context.ParameterOperands[0] = local.Register; break;
            case "versioned-local": local.Register = local.Register.Copy(1); break;
            case "method-info": local.IsMethodInfo = true; break;
            case "receiver": local.IsThis = true; break;
            case "missing-slot": context.ParameterOperands.RemoveAt(1); break;
            case "unconverted-stack": context.ParameterOperands[1] = new StackOffset(40); break;
            case "undeclared-trailing-slot":
                local = new LocalVariable("trailing", new Register(923, "hiddenArgument"));
                context.ParameterOperands.Add(local.Register);
                break;
        }
        Assert.That(LocalVariables.GetIncomingParameterIndex(context, local), Is.Null);
    }
}
