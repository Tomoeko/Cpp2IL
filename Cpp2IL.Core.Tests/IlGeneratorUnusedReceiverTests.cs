using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void InstanceMethodWithoutAReceiverReadExecutesWithoutAnInventedReceiverLocal(bool overwriteReceiver)
    {
        var (context, definition, _) = CreateMethod("ConstantInstance", _app.SystemTypes.SystemInt32Type, [], instance: true);
        var receiver = new Register(null, "rcx");
        var result = new Register(null, "rax");
        context.ParameterOperands = [receiver];
        context.RawBytes = new BinarySlice(Convert.FromHexString(overwriteReceiver ? "B91100000089C8C3" : "B82A000000C3"));
        var instructions = overwriteReceiver
            ? new List<Instruction> { new(0, OpCode.Move, receiver, Imm(17)), new(1, OpCode.Move, result, receiver), new(2, OpCode.Return, result) }
            : [new(0, OpCode.Move, result, Imm(42)), new(1, OpCode.Return, result)];
        PrepareReceiverMapping(context, instructions);
        Assert.That(context.AnalysisWarnings, Is.Empty);
        Assert.That(context.ParameterLocals.Any(local => local.IsThis), Is.False);
        LocalVariables.ResolveTypesAndFields(context);
        SsaForm.Remove(context);
        IlGenerator.GenerateIl(context, definition);
        AddDefaultConstructor();

        using var runtime = Load();
        var instance = Activator.CreateInstance(runtime.Type);
        Assert.That(runtime.Type.GetMethod("ConstantInstance")!.Invoke(instance, null), Is.EqualTo(overwriteReceiver ? 17 : 42));
    }

    [TestCase("")]
    [TestCase("4889C8C3")] // receiver read is missing from the supplied graph
    [TestCase("B1074889C8C3")] // a partial write must not hide a later full receiver read
    [TestCase("E800000000B82A000000C3")]
    public void MissingReceiverMappingStillWarnsWithoutIndependentNativeProof(string nativeBytes)
    {
        var (context, _, _) = CreateMethod("UnprovedInstance", _app.SystemTypes.SystemInt32Type, [], instance: true);
        context.ParameterOperands = [new Register(null, "rcx")];
        context.RawBytes = new BinarySlice(Convert.FromHexString(nativeBytes));
        var result = new Register(null, "rax");
        PrepareReceiverMapping(context, [new(0, OpCode.Move, result, Imm(42)), new(1, OpCode.Return, result)]);
        Assert.That(context.AnalysisWarnings, Has.Count.EqualTo(1));
        Assert.That(context.AnalysisWarnings.Single(), Does.Contain("'this' local not found"));
    }

    [Test]
    public void ReceiverReadIsStillBoundToTheManagedThisParameter()
    {
        var (context, definition, _) = CreateMethod("MappedSelf", _app.SystemTypes.SystemObjectType, [], instance: true);
        var receiver = new Register(null, "rcx");
        var result = new Register(null, "rax");
        context.ParameterOperands = [receiver];
        context.RawBytes = new BinarySlice(Convert.FromHexString("4889C8C3"));
        PrepareReceiverMapping(context, [new(0, OpCode.Move, result, receiver), new(1, OpCode.Return, result)]);
        Assert.That(context.ParameterLocals.Single().IsThis, Is.True);
        Assert.That(context.AnalysisWarnings, Is.Empty);
        LocalVariables.ResolveTypesAndFields(context);
        SsaForm.Remove(context);
        IlGenerator.GenerateIl(context, definition);
        AddDefaultConstructor();

        using var runtime = Load();
        var instance = Activator.CreateInstance(runtime.Type);
        Assert.That(runtime.Type.GetMethod("MappedSelf")!.Invoke(instance, null), Is.SameAs(instance));
    }

    private static void PrepareReceiverMapping(MethodAnalysisContext context, List<Instruction> instructions)
    {
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        context.DominatorInfo = new DominatorInfo(context.ControlFlowGraph);
        SsaForm.Build(context);
        LocalVariables.CreateAll(context);
    }
}
