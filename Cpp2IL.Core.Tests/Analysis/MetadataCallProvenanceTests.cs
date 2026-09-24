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

    [TestCase(false)]
    [TestCase(true)]
    public void SameSignatureAliasesNeedCallsiteIdentity(bool isStatic)
    {
        const ulong target = 0x7f00_1234_5678_9000;
        var owner = _app.SystemTypes.SystemStringType;
        var attributes = MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0);
        var first = new InjectedMethodAnalysisContext(owner, "First", owner, attributes, [owner]);
        var second = new InjectedMethodAnalysisContext(owner, "Second", owner, attributes, [owner]);
        _app.MethodsByAddress[target] = [first, second];

        var context = CreateMethod();
        var result = new LocalVariable("result", new Register(1, "result"), owner);
        var receiverOrArgument = new LocalVariable("argument", new Register(2, "argument"), owner);
        var call = new Instruction(0, OpCode.Call, Imm(target), result,
            receiverOrArgument, Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return, result)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(context), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>(),
            "A shared body and signature do not identify the original managed member.");
    }

    [Test]
    public void ExplicitMethodInfoSelectsSharedAlias()
    {
        const ulong target = 0x7f00_1234_5678_9001;
        var owner = _app.SystemTypes.SystemStringType;
        var attributes = MethodAttributes.Public | MethodAttributes.Static;
        var first = new InjectedMethodAnalysisContext(owner, "First", owner, attributes, [owner]);
        var second = new InjectedMethodAnalysisContext(owner, "Second", owner, attributes, [owner]);
        _app.MethodsByAddress[target] = [first, second];

        var context = CreateMethod();
        var result = new LocalVariable("result", new Register(1, "result"), owner);
        var argument = new LocalVariable("argument", new Register(2, "argument"), owner);
        var methodInfo = new RuntimeMethodInfoAnalysisContext(second, owner.DeclaringAssembly);
        var call = new Instruction(0, OpCode.Call, Imm(target), result, argument, methodInfo);
        context.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return, result)]);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(context), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(second));
    }

    [Test]
    public void AllocatedOwnerDoesNotIdentifyFoldedConstructorOverload()
    {
        const ulong target = 0x7f00_1234_5678_9002;
        var owner = _app.SystemTypes.SystemStringType;
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var empty = new InjectedMethodAnalysisContext(owner, ".ctor", _app.SystemTypes.SystemVoidType,
            attributes, []);
        var withValue = new InjectedMethodAnalysisContext(owner, ".ctor", _app.SystemTypes.SystemVoidType,
            attributes, [_app.SystemTypes.SystemInt32Type]);
        _app.MethodsByAddress[target] = [empty, withValue];

        var context = CreateMethod();
        var instance = new LocalVariable("instance", new Register(1, "instance"), owner);
        var allocation = new Instruction(0, OpCode.Newobj, instance);
        var call = new Instruction(1, OpCode.CallVoid, Imm(target), instance, Imm(7), Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([allocation, call,
            new(2, OpCode.Return, instance)]);

        Assert.That(MetadataResolver.ResolveConstructorCalls(context), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    [Test]
    public void AllocatedOwnerIdentifiesUniqueConstructorAcrossOwners()
    {
        const ulong target = 0x7f00_1234_5678_9003;
        var owner = _app.SystemTypes.SystemStringType;
        var other = _app.SystemTypes.SystemObjectType;
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var constructor = new InjectedMethodAnalysisContext(owner, ".ctor", _app.SystemTypes.SystemVoidType,
            attributes, []);
        var unrelated = new InjectedMethodAnalysisContext(other, ".ctor", _app.SystemTypes.SystemVoidType,
            attributes, []);
        _app.MethodsByAddress[target] = [unrelated, constructor];

        var context = CreateMethod();
        var instance = new LocalVariable("instance", new Register(1, "instance"), owner);
        var allocation = new Instruction(0, OpCode.Newobj, instance);
        var call = new Instruction(1, OpCode.CallVoid, Imm(target), instance, Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([allocation, call,
            new(2, OpCode.Return, instance)]);

        Assert.That(MetadataResolver.ResolveConstructorCalls(context), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(constructor));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ConstructorReceiverCannotChooseDerivedAliasOfBaseConstructor(bool callerIsConstructor)
    {
        const ulong target = 0x7f00_1234_5678_9004;
        var derived = _app.SystemTypes.SystemStringType;
        var baseType = _app.SystemTypes.SystemObjectType;
        var baseConstructor = new InjectedMethodAnalysisContext(baseType, ".ctor",
            _app.SystemTypes.SystemVoidType, MethodAttributes.Public, []);
        var derivedMethod = new InjectedMethodAnalysisContext(derived, "Dispose",
            _app.SystemTypes.SystemVoidType, MethodAttributes.Public, []);
        _app.MethodsByAddress[target] = [derivedMethod, baseConstructor];

        var caller = new InjectedMethodAnalysisContext(derived,
            callerIsConstructor ? ".ctor" : "OrdinaryMethod", _app.SystemTypes.SystemVoidType,
            MethodAttributes.Public, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), derived) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.EqualTo(!callerIsConstructor));
        Assert.That(call.Operands[0], callerIsConstructor ? Is.TypeOf<Immediate>() : Is.SameAs(derivedMethod));
    }

    private InjectedMethodAnalysisContext CreateMethod() => new(_app.SystemTypes.SystemObjectType,
        "SyntheticCall", _app.SystemTypes.SystemStringType, MethodAttributes.Public | MethodAttributes.Static,
        [_app.SystemTypes.SystemIntPtrType]) { Locals = [], ParameterLocals = [], ParameterOperands = [], AnalysisWarnings = [] };
}
