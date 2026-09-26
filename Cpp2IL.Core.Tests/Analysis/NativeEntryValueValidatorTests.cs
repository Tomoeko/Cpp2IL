using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Analysis;

public class NativeEntryValueValidatorTests
{
    [Test]
    public void TypedIncomingStackValueStillNeedsAParameterOrigin()
    {
        var parameter = new LocalVariable("parameter", new Register(1, "stack_28"));
        var unknown = new LocalVariable("unknown", new Register(2, "stack_20"));
        var copied = new LocalVariable("copied", new Register(3, "rax", 0));
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, copied, unknown), new(1, OpCode.Return, copied, parameter)]);
        Assert.That(NativeEntryValueValidator.FindUnprovedValues(graph, [parameter]),
            Is.EqualTo(new[] { unknown }));
    }

    [Test]
    public void ExplicitDefinitionsAndAddressedStorageHaveSeparateOrigins()
    {
        var value = new LocalVariable("value", new Register(1, "rax", 0));
        var storage = new LocalVariable("storage", new Register(2, "stack_-20"));
        var graph = new ISILControlFlowGraph([
            new(0, OpCode.Move, value, Imm(17)),
            new(1, OpCode.CallVoid, Str("storage-initializer"), new AddressOf(storage)),
            new(2, OpCode.Return, value)]);
        Assert.That(NativeEntryValueValidator.FindUnprovedValues(graph, []), Is.Empty);
    }
}
