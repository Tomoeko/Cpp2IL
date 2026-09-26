using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class MetadataCallProvenanceTests
{
    private ApplicationAnalysisContext _app = null!;
    private ulong? _boundAddress;

    [OneTimeSetUp]
    public void LoadPublicTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
    }

    [TearDown]
    public void RemoveSyntheticCallBinding()
    {
        if (_boundAddress is not { } address)
            return;

        _app.MethodsByAddress.Remove(address);
        _boundAddress = null;
    }

    [OneTimeTearDown]
    public void ReleasePublicTypeModel() => Cpp2IlApi.ResetInternalState();

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
        BindMethods(target, [first, second]);

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
        BindMethods(target, [first, second]);

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
        BindMethods(target, [empty, withValue]);

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
        BindMethods(target, [unrelated, constructor]);

        var context = CreateMethod();
        var instance = new LocalVariable("instance", new Register(1, "instance"), owner);
        var allocation = new Instruction(0, OpCode.Newobj, instance);
        var call = new Instruction(1, OpCode.CallVoid, Imm(target), instance, Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([allocation, call,
            new(2, OpCode.Return, instance)]);

        Assert.That(MetadataResolver.ResolveConstructorCalls(context), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(constructor));
    }

    [Test]
    public void AllocatedOwnerCannotSelectConstructorOutsideTargetBindings()
    {
        const ulong target = 0x7f00_1234_5678_9005;
        var owner = new InjectedTypeAnalysisContext(_app.SystemTypes.SystemObjectType.DeclaringAssembly,
            "Synthetic", "AllocatedOwner", _app.SystemTypes.SystemObjectType,
            TypeAttributes.Public | TypeAttributes.Class);
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var unboundConstructor = owner.InjectMethodContext(".ctor", _app.SystemTypes.SystemVoidType,
            attributes);
        var boundConstructor = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType,
            ".ctor", _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, [boundConstructor]);

        var context = CreateMethod();
        var instance = new LocalVariable("instance", new Register(1, "instance"), owner);
        var allocation = new Instruction(0, OpCode.Newobj, instance);
        var call = new Instruction(1, OpCode.CallVoid, Imm(target), instance, Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([allocation, call,
            new(2, OpCode.Return, instance)]);

        Assert.That(owner.Methods, Does.Contain(unboundConstructor));
        Assert.That(MetadataResolver.ResolveConstructorCalls(context), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    [Test]
    public void AllocatedGenericOwnerSpecializesAddressBoundConstructor()
    {
        const ulong target = 0x7f00_1234_5678_9007;
        var genericType = _app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!;
        var owner = genericType.MakeGenericInstanceType([_app.SystemTypes.SystemInt32Type]);
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var boundConstructor = new InjectedMethodAnalysisContext(genericType, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, [boundConstructor]);

        var context = CreateMethod();
        var instance = new LocalVariable("instance", new Register(1, "instance"), owner);
        var allocation = new Instruction(0, OpCode.Newobj, instance);
        var call = new Instruction(1, OpCode.CallVoid, Imm(target), instance, Imm(0));
        context.ControlFlowGraph = new ISILControlFlowGraph([allocation, call,
            new(2, OpCode.Return, instance)]);

        Assert.That(MetadataResolver.ResolveConstructorCalls(context), Is.True);
        Assert.That(call.Operands[0], Is.TypeOf<ConcreteGenericMethodAnalysisContext>());
        var resolved = (ConcreteGenericMethodAnalysisContext)call.Operands[0];
        Assert.That(resolved.BaseMethodContext, Is.SameAs(boundConstructor));
        Assert.That(resolved.TypeGenericParameters, Is.EquivalentTo(owner.GenericArguments));
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
        BindMethods(target, [derivedMethod, baseConstructor]);

        var caller = new InjectedMethodAnalysisContext(derived,
            callerIsConstructor ? ".ctor" : "OrdinaryMethod", _app.SystemTypes.SystemVoidType,
            MethodAttributes.Public, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), derived) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.EqualTo(!callerIsConstructor));
        Assert.That(call.Operands[0], callerIsConstructor ? Is.TypeOf<Immediate>() : Is.SameAs(derivedMethod));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConstructorReceiverCannotChooseBetweenDerivedAndBaseConstructors(bool reverseAliases)
    {
        const ulong target = 0x7f00_1234_5678_9006;
        var derived = _app.SystemTypes.SystemStringType;
        var baseType = _app.SystemTypes.SystemObjectType;
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var derivedConstructor = new InjectedMethodAnalysisContext(derived, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var baseConstructor = new InjectedMethodAnalysisContext(baseType, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, reverseAliases
            ? [baseConstructor, derivedConstructor]
            : [derivedConstructor, baseConstructor]);

        var caller = new InjectedMethodAnalysisContext(derived, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), derived) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    [Test]
    public void ConstructorReceiverSelectsImmediateBaseAmongBaseChainAliases()
    {
        const ulong target = 0x7f00_1234_5678_9008;
        var assembly = _app.SystemTypes.SystemObjectType.DeclaringAssembly;
        var root = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Root",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var middle = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Middle",
            root, TypeAttributes.Public | TypeAttributes.Class);
        var derived = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Derived",
            middle, TypeAttributes.Public | TypeAttributes.Class);
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var rootConstructor = new InjectedMethodAnalysisContext(root, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var immediateBaseConstructor = new InjectedMethodAnalysisContext(middle, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, [rootConstructor, immediateBaseConstructor]);

        var caller = new InjectedMethodAnalysisContext(derived, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), derived) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.True);
        Assert.That(call.Operands[0], Is.SameAs(immediateBaseConstructor));
    }

    [Test]
    public void ConstructorReceiverCannotSelectGrandparentWhenImmediateBaseIsUnbound()
    {
        const ulong target = 0x7f00_1234_5678_9010;
        var assembly = _app.SystemTypes.SystemObjectType.DeclaringAssembly;
        var root = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Root",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var middle = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Middle",
            root, TypeAttributes.Public | TypeAttributes.Class);
        var derived = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Derived",
            middle, TypeAttributes.Public | TypeAttributes.Class);
        var unrelated = new InjectedTypeAnalysisContext(assembly, "Synthetic", "Unrelated",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var rootConstructor = new InjectedMethodAnalysisContext(root, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var unrelatedConstructor = new InjectedMethodAnalysisContext(unrelated, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, [rootConstructor, unrelatedConstructor]);

        var caller = new InjectedMethodAnalysisContext(derived, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), derived) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    public void GenericConstructorReceiverNeedsUnambiguousOpenSelfBaseCall(bool openSelf,
        bool includeSelfAlias)
    {
        const ulong target = 0x7f00_1234_5678_9009;
        var assembly = _app.SystemTypes.SystemObjectType.DeclaringAssembly;
        var root = new InjectedTypeAnalysisContext(assembly, "Synthetic", "GenericRoot",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var middle = new InjectedTypeAnalysisContext(assembly, "Synthetic", "GenericMiddle",
            root, TypeAttributes.Public | TypeAttributes.Class);
        var owner = new InjectedTypeAnalysisContext(assembly, "Synthetic", "GenericOwner`1",
            middle, TypeAttributes.Public | TypeAttributes.Class);
        var genericParameter = _app.GetAssemblyByName("mscorlib")!
            .GetTypeByFullName("System.Collections.Generic.List`1")!.GenericParameters.Single();
        owner.GenericParameters.Add(genericParameter);
        var receiverType = owner.MakeGenericInstanceType([
            openSelf ? genericParameter : _app.SystemTypes.SystemInt32Type]);
        var attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var rootConstructor = new InjectedMethodAnalysisContext(root, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var immediateBaseConstructor = new InjectedMethodAnalysisContext(middle, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        BindMethods(target, [rootConstructor, immediateBaseConstructor]);
        if (includeSelfAlias)
            _app.MethodsByAddress[target].Add(new InjectedMethodAnalysisContext(owner, ".ctor",
                _app.SystemTypes.SystemVoidType, attributes, []));

        var caller = new InjectedMethodAnalysisContext(owner, ".ctor",
            _app.SystemTypes.SystemVoidType, attributes, []);
        var receiver = new LocalVariable("this", new Register(1, "this"), receiverType) { IsThis = true };
        var call = new Instruction(0, OpCode.CallVoid, Imm(target), receiver, Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call, new(1, OpCode.Return)]);

        var resolved = openSelf && !includeSelfAlias;
        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.EqualTo(resolved));
        Assert.That(call.Operands[0], resolved
            ? Is.SameAs(immediateBaseConstructor) : Is.TypeOf<Immediate>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SharedCallRetainsBothArgumentStreamsAndSelectsTheProvedReturn(bool reverseAliases)
    {
        const ulong target = 0x7f00_1234_5678_9010;
        var owner = _app.SystemTypes.SystemObjectType;
        var attributes = MethodAttributes.Public | MethodAttributes.Static;
        var floating = new InjectedMethodAnalysisContext(owner, "Floating", _app.SystemTypes.SystemSingleType,
            attributes, [_app.SystemTypes.SystemSingleType]);
        var integer = new InjectedMethodAnalysisContext(owner, "Integer", _app.SystemTypes.SystemInt32Type,
            attributes, [_app.SystemTypes.SystemInt32Type]);
        BindMethods(target, reverseAliases ? [integer, floating] : [floating, integer]);
        var caller = CreateMethod();
        var native = Iced.Intel.Instruction.CreateBranch(Iced.Intel.Code.Call_rel32_64, target);
        native.IP = target - 16;
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native, caller);
        var call = lifted.Single(instruction => instruction.IsCall);
        Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid));
        Assert.That(call.Operands.Skip(1).Select(operand => operand.ToString()), Is.EqualTo(
            new[] { "rcx", "rdx", "r8", "r9", "xmm0", "xmm1", "xmm2", "xmm3" }));
        Assert.That(call.DeferredCallReturns, Has.Length.EqualTo(2));
        var result = new LocalVariable("floatingResult", new Register(1, "xmm0"));
        call.DeferredCallReturns![1].SetOperand(0, result);
        var info = new RuntimeMethodInfoAnalysisContext(floating, owner.DeclaringAssembly);
        call.SetOperand(2, info); // static float argument in XMM0, hidden MethodInfo in RDX
        caller.ControlFlowGraph = new ISILControlFlowGraph(lifted);

        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(call.Operands[0], Is.SameAs(floating));
            Assert.That(call.OpCode, Is.EqualTo(OpCode.Call));
            Assert.That(call.Operands[1], Is.SameAs(result));
            Assert.That(call.Operands[2].ToString(), Is.EqualTo("xmm0"));
            Assert.That(call.Operands[3], Is.SameAs(info));
            Assert.That(lifted[2].OpCode, Is.EqualTo(OpCode.Nop));
            Assert.That(lifted[1].OpCode, Is.EqualTo(OpCode.UnresolvedValue),
                "The other register remains a clobber, not a guessed managed value.");
        });
    }

    [Test]
    public void MethodInfoInAnOrdinaryArgumentCannotSelectAnAlias()
    {
        const ulong target = 0x7f00_1234_5678_9011;
        var owner = _app.SystemTypes.SystemObjectType;
        var attributes = MethodAttributes.Public | MethodAttributes.Static;
        var first = new InjectedMethodAnalysisContext(owner, "First", owner, attributes, [owner]);
        var second = new InjectedMethodAnalysisContext(owner, "Second", owner, attributes, [owner]);
        BindMethods(target, [first, second]);
        var caller = CreateMethod();
        var call = new Instruction(0, OpCode.Call, Imm(target), new LocalVariable("result", new Register(1, "rax")),
            new RuntimeMethodInfoAnalysisContext(second, owner.DeclaringAssembly), Imm(0));
        caller.ControlFlowGraph = new ISILControlFlowGraph([call]);
        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());
    }

    [Test]
    public void ReturnBufferCandidatesCannotUseTheFirstRegisterAsAnUnprovedReceiver()
    {
        const ulong target = 0x7f00_1234_5678_9012;
        var owner = _app.SystemTypes.SystemStringType;
        var aggregate = _app.GetAssemblyByName("mscorlib")!.GetTypeByFullName("System.Decimal")!;
        var buffered = new InjectedMethodAnalysisContext(owner, "Buffered", aggregate,
            MethodAttributes.Public, []);
        var scalar = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType, "Scalar", owner,
            MethodAttributes.Public, []);
        BindMethods(target, [buffered, scalar]);
        var caller = CreateMethod();
        var convention = new X64CallingConventionResolver();
        Assert.That(convention.ReturnsViaHiddenBuffer(buffered), Is.True);
        var call = new Instruction(0, OpCode.CallVoid, Imm(target));
        call.AddOperands(convention.ResolveForUnmanaged(_app, target));
        call.SetOperand(1, new LocalVariable("possibleBuffer", new Register(1, "rcx"), owner));
        call.RawCallStackArgumentCount = 0;
        call.DeferredCallReturns = [];
        caller.ControlFlowGraph = new ISILControlFlowGraph([call]);
        Assert.That(MetadataResolver.ResolveAmbiguousCalls(caller), Is.False);
        Assert.That(call.Operands[0], Is.TypeOf<Immediate>());

        call.SetOperand(3, new RuntimeMethodInfoAnalysisContext(buffered, owner.DeclaringAssembly));
        Assert.That(MetadataResolver.ResolveCallsViaMethodInfo(caller), Is.True,
            "The hidden MethodInfo in R8 identifies the buffer-returning instance target.");
        Assert.That(call.OpCode, Is.EqualTo(OpCode.NotImplemented),
            "Identity does not establish transfer of a hidden return-buffer result.");
    }

    [Test]
    public void WindowsStackArgumentsFollowShadowSpaceAndIncomingReturnAddress()
    {
        var type = _app.SystemTypes.SystemInt32Type;
        var method = new InjectedMethodAnalysisContext(_app.SystemTypes.SystemObjectType, "SixArguments", type,
            MethodAttributes.Public | MethodAttributes.Static, Enumerable.Repeat(type, 6).ToArray());
        var convention = new X64CallingConventionResolver();
        Assert.That(convention.ResolveForManaged(method).OfType<StackOffset>().Select(slot => slot.Offset),
            Is.EqualTo(new[] { 0x20, 0x28, 0x30 }));
        Assert.That(convention.ResolveForParameters(method).OfType<StackOffset>().Select(slot => slot.Offset),
            Is.EqualTo(new[] { 0x28, 0x30, 0x38 }));
    }

    private InjectedMethodAnalysisContext CreateMethod() => new(_app.SystemTypes.SystemObjectType,
        "SyntheticCall", _app.SystemTypes.SystemStringType, MethodAttributes.Public | MethodAttributes.Static,
        [_app.SystemTypes.SystemIntPtrType]) { Locals = [], ParameterLocals = [], ParameterOperands = [], AnalysisWarnings = [] };

    private void BindMethods(ulong address, List<MethodAnalysisContext> methods)
    {
        Assert.That(_boundAddress, Is.Null, "Each test must own only one synthetic call binding.");
        Assert.That(_app.MethodsByAddress.ContainsKey(address), Is.False,
            "Synthetic call address must not replace a fixture binding.");
        _app.MethodsByAddress.Add(address, methods);
        _boundAddress = address;
    }
}
