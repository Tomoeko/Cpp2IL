using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class InitializationGuardSafetyTests
{
    [TestCase("il2cpp_runtime_class_init_export")]
    [TestCase("il2cpp_runtime_class_init_actual")]
    [TestCase("il2cpp_codegen_runtime_class_init")]
    public void BareClassInitializationCannotBeDeleted(string helper)
    {
        var call = new Instruction(0, OpCode.CallVoid, Str(helper), Local("type"));
        var graph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);
        MetadataInitGuardRemover.Run(graph, 0x135);
        Assert.That(graph.Instructions.Contains(call), Is.True);
        Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid), "Initialization can execute a constructor or throw.");
    }

    [TestCase("il2cpp_runtime_class_init_actual")]
    [TestCase("ManagedSideEffect")]
    public void OffsetShapedConditionDoesNotAuthorizeDeletingCalls(string callee)
    {
        var flag = Local("flag");
        var result = Local("result");
        var merge = new Instruction(4, OpCode.Move, result, Imm(7));
        var call = new Instruction(2, OpCode.CallVoid, Str(callee), Local("type"));
        var branch = new Instruction(1, OpCode.ConditionalJump, merge, flag);
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.And, flag, new MemoryOperand(Local("unprovedReceiver"), addend: 0x135), Imm(1)),
            branch, call, new(3, OpCode.Jump, merge), merge, new(5, OpCode.Return, result)]);
        MetadataInitGuardRemover.Run(graph, 0x135);
        Assert.That(graph.Instructions.Contains(call), Is.True);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    [Test]
    public void MetadataHelperAndUnrelatedStoreDoNotProveACompilerGuard()
    {
        var result = Local("result");
        var merge = new Instruction(4, OpCode.Move, result, Imm(7));
        var call = new Instruction(2, OpCode.CallVoid, Str("il2cpp_codegen_initialize_runtime_metadata"), Imm(0x2000));
        var store = new Instruction(3, OpCode.Move, new MemoryOperand(addend: 0x3000), Imm(1));
        var branch = new Instruction(1, OpCode.ConditionalJump, merge, Local("applicationCondition"));
        var graph = new ISILControlFlowGraph([new(0, OpCode.Nop), branch, call, store, merge, new(5, OpCode.Return, result)]);
        MetadataInitGuardRemover.Run(graph, 0x135);
        Assert.That(graph.Instructions.Contains(call), Is.True);
        Assert.That(graph.Instructions.Contains(store), Is.True);
        Assert.That(branch.OpCode, Is.EqualTo(OpCode.ConditionalJump));
    }

    private static LocalVariable Local(string name) => new(name, new Register(null, name));
}
