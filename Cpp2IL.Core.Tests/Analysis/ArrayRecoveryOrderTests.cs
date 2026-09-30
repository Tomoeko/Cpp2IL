using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class ArrayRecoveryOrderTests
{
    private ApplicationAnalysisContext _app = null!;

    [OneTimeSetUp]
    public void LoadTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
    }

    [OneTimeTearDown]
    public void ReleaseTypeModel() => Cpp2IlApi.ResetInternalState();

    [TestCase(OpCode.CallVoid)]
    [TestCase(OpCode.Move)]
    [TestCase(OpCode.Divide)]
    public void AllocationAndStoresStayBeforeInterveningOperations(OpCode operation)
    {
        var arrayType = new SzArrayTypeAnalysisContext(_app.SystemTypes.SystemInt32Type);
        var array = new LocalVariable("array", new Register(1, "array"), arrayType);
        var value = new LocalVariable("value", new Register(2, "value"), _app.SystemTypes.SystemInt32Type);
        var intervening = operation switch
        {
            OpCode.CallVoid => new Instruction(3, operation, Str("SideEffect")),
            OpCode.Move => new Instruction(3, operation, value, Imm(9)),
            _ => new Instruction(3, operation, value, Imm(1), Imm(0))
        };
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, value, Imm(3)),
            new(1, OpCode.NewArr, array, arrayType, Imm(2)),
            new(2, OpCode.Move, new ArrayAccess(array, Imm(0)), value),
            intervening,
            new(4, OpCode.Move, new ArrayAccess(array, Imm(1)), Imm(7)),
            new(5, OpCode.Return, array)
        ]);

        AssertOrderPreserved(graph);
    }

    [Test]
    public void AllocationAndStoresStayInTheirOriginalStraightLineBlocks()
    {
        var arrayType = new SzArrayTypeAnalysisContext(_app.SystemTypes.SystemInt32Type);
        var array = new LocalVariable("array", new Register(1, "array"), arrayType);
        var effect = new Instruction(3, OpCode.CallVoid, Str("SideEffect"));
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.NewArr, array, arrayType, Imm(2)),
            new(1, OpCode.Move, new ArrayAccess(array, Imm(0)), Imm(3)),
            new(2, OpCode.Jump, effect), effect,
            new(4, OpCode.Move, new ArrayAccess(array, Imm(1)), Imm(7)),
            new(5, OpCode.Return, array)
        ]);
        var blocks = graph.Blocks.Select(block => block.Instructions.ToArray()).ToArray();

        AssertOrderPreserved(graph);
        Assert.That(graph.Blocks.Select(block => block.Instructions.ToArray()), Is.EqualTo(blocks),
            "Allocation and stores must not cross a native control-flow boundary for source formatting.");
    }

    private void AssertOrderPreserved(ISILControlFlowGraph graph)
    {
        var original = graph.Instructions.ToArray();
        var method = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType, "ArrayOrder",
            _app.SystemTypes.SystemVoidType, MethodAttributes.Public | MethodAttributes.Static, [])
        {
            ControlFlowGraph = graph
        };

        ArrayRecovery.Run(method);

        Assert.That(graph.Instructions, Is.EqualTo(original),
            "Regrouping can change side effects, prior local values, and which exception occurs first.");
    }
}
