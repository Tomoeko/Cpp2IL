using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using System.Reflection;

namespace Cpp2IL.Core.Tests.Analysis;

public class MetadataCallProvenanceTests
{
    private ApplicationAnalysisContext _app = null!;

    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LiteralSlotArgumentDoesNotProveTheCalleeIsALiteralAccessor(bool managed)
    {
        var index = _app.Metadata.metadataUsageDic![(uint)MetadataUsageType.StringLiteral].First().Key;
        var address = _app.Binary.GetRawMetadataUsage(index);
        Assert.That(_app.LibCpp2IlContext.GetLiteralByAddress(address), Is.Not.Null, "Public fixture must provide an actual literal slot.");
        var context = CreateMethod();
        var result = new LocalVariable("result", new Register(1, "result"), _app.SystemTypes.SystemStringType);
        IOperand callee = managed ? CreateMethod() : Str("UnknownNativeFunction");
        var call = new Instruction(0, OpCode.Call, callee, result, Imm(unchecked((long)address)));
        context.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return, result)]);
        context.Locals.Add(result);
        MetadataResolver.ResolveAll(context);
        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call), "A managed or unknown callee may have effects despite receiving a literal address.");
        Assert.That(call.Operands[0], Is.SameAs(callee));
    }

    [TestCase("il2cpp_codegen_initialize_method")]
    [TestCase("il2cpp_codegen_initialize_runtime_metadata")]
    [TestCase("il2cpp_codegen_initialize_runtime_metadata_inline")]
    public void MetadataHelperNameDoesNotProveAnUnguardedCallCanBeErased(string name)
    {
        var context = CreateMethod();
        var result = new LocalVariable("result", new Register(1, "result"), _app.SystemTypes.SystemStringType);
        var call = new Instruction(0, OpCode.Call, Str(name), result, Imm(0x2000));
        context.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return, result)]);
        MetadataInitGuardRemover.RewriteUnguardedInits(context);
        Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
    }

    private InjectedMethodAnalysisContext CreateMethod() => new(_app.SystemTypes.SystemObjectType,
        "SyntheticCall", _app.SystemTypes.SystemStringType, MethodAttributes.Public | MethodAttributes.Static,
        [_app.SystemTypes.SystemIntPtrType]) { Locals = [], ParameterLocals = [], ParameterOperands = [], AnalysisWarnings = [] };
}
