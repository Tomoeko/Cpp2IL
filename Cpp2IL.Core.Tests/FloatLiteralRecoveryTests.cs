using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void SubnormalTypeHintsRequireNativeStoreEvidenceAndDoNotReinterpretCallArguments(int width, bool argument)
    {
        var type = width == 32 ? _app.SystemTypes.SystemSingleType : _app.SystemTypes.SystemDoubleType;
        var (context, _, _) = CreateMethod("UnprovedSubnormal", _app.SystemTypes.SystemVoidType, [], instance: true);
        IOperand raw = new Immediate(1);
        Instruction operation;
        if (argument)
        {
            var (target, _, _) = CreateMethod("FloatingTarget", _app.SystemTypes.SystemVoidType, [type]);
            operation = new(0, OpCode.CallVoid, target, raw);
        }
        else
        {
            var field = new InjectedFieldAnalysisContext("Marker", type, System.Reflection.FieldAttributes.Public,
                _typeContext, 16);
            operation = new(0, OpCode.Move, new FieldReference(field, context.ParameterLocals[0], 16), raw);
        }
        context.ControlFlowGraph = new ISILControlFlowGraph([operation, new(1, OpCode.Return)]);
        FloatLiteralRecovery.Run(context);
        Assert.That(operation.Operands[1], Is.SameAs(raw));

        // An admission marker without the sites that captured the native
        // effects must never clear this unresolved bit interpretation.
        NativeRecoveryProofTracker.Mark(context, X64NativeNullCheckedInvocationProof.EvidenceKey);
        FloatLiteralRecovery.Run(context);
        Assert.That(operation.Operands[1], Is.SameAs(raw));
        Assert.That(X64NativeNullCheckedInvocationProof.TryNormalizeSubnormalFloatingStores(context), Is.False);
    }

    [Test]
    public void SubnormalNormalizationRequiresAnAnalysisGraph()
    {
        Assert.That(X64NativeNullCheckedInvocationProof.TryNormalizeSubnormalFloatingStores(null), Is.False);
        var (context, _, _) = CreateMethod("UnanalyzedSubnormal", _app.SystemTypes.SystemVoidType, []);
        Assert.That(X64NativeNullCheckedInvocationProof.TryNormalizeSubnormalFloatingStores(context), Is.False);
    }
}
