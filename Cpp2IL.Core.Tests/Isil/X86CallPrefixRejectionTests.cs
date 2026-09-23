using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86CallPrefixRejectionTests
{
    [TestCase("F2E800000000")] // BND-prefixed direct call
    [TestCase("F3E800000000")]
    [TestCase("64E800000000")]
    [TestCase("2EE800000000")]
    [TestCase("F2FFD0")] // BND-prefixed register call
    [TestCase("F3FF10")] // REP-prefixed memory call
    [TestCase("64FF10")] // FS-relative indirect target
    [TestCase("36FFD0")]
    public void UnprovedCallerPrefixCannotDisappearIntoCalleeRecovery(string bytes)
    {
        var native = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes))).Decode();
        Assert.That(native.Mnemonic, Is.EqualTo(Mnemonic.Call));
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native);
        var rejected = lifted.Single();
        Assert.That(rejected.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(rejected.Operands.Single().ToString(), Does.Contain("prefix").IgnoreCase);

        // No callee context is supplied: rejection must precede both helper proofs and
        // ordinary call resolution, and remain visible when no return value is consumed.
        lifted.Add(new(lifted.Count, OpCode.Return));
        var graph = new ISILControlFlowGraph(lifted);
        DeadCodeEliminator.Run(graph);
        Assert.That(graph.Instructions, Does.Contain(rejected));
    }
}
