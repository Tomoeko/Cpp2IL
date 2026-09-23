using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace Cpp2IL.Core.Tests;

/// <summary>Executes synthetic recovered IL on the test runtime; this is not Unity validation.</summary>
public class IlGeneratorParameterTests
{
    private ApplicationAnalysisContext _app = null!;
    private AssemblyDefinition _assembly = null!;
    private ModuleDefinition _module = null!;
    private TypeDefinition _type = null!;
    private InjectedTypeAnalysisContext _typeContext = null!;

    [SetUp]
    public void SetUp()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
        // Populate managed type identities from the public fixture; none of its method bodies
        // are executed. All instructions under test below are synthetic.
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        var name = "Cpp2IL.Synthetic.Parameters." + Guid.NewGuid().ToString("N");
        _assembly = new AssemblyDefinition(name, new Version(1, 0, 0, 0));
        _module = new ModuleDefinition(name + ".dll", KnownCorLibs.MsCorLib_v4_0_0_0);
        _assembly.Modules.Add(_module);
        _type = new TypeDefinition("Synthetic", "Generated", TypeAttributes.Public | TypeAttributes.Class, _module.CorLibTypeFactory.Object.Type);
        _module.TopLevelTypes.Add(_type);
        _typeContext = new InjectedTypeAnalysisContext(_app.AssembliesByName["Assembly-CSharp"], "Synthetic", "Generated",
            _app.SystemTypes.SystemObjectType, ReflectionTypeAttributes.Public | ReflectionTypeAttributes.Class);
        _typeContext.PutExtraData("AsmResolverType", _type);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DuplicateAndChangedDisplayNamesPreserveParameterIdentityAndWrites(bool instance)
    {
        var (context, definition, parameters) = CreateMethod("Calculate", _app.SystemTypes.SystemInt32Type,
            [_app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemInt32Type], instance);
        var first = parameters[0];
        var second = parameters[1];
        first.Name = second.Name = "renamed";
        var result = new LocalVariable("renamed", new Register(900, "scratch"), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(result);
        Emit(context, definition,
        [
            new(0, OpCode.Add, first, first, Imm(3)),
            new(1, OpCode.Subtract, result, first, second),
            new(2, OpCode.Return, result),
        ]);
        if (instance)
            AddDefaultConstructor();

        using var runtime = Load();
        var receiver = instance ? Activator.CreateInstance(runtime.Type) : null;
        object?[] arguments = [10, 4];
        Assert.That(runtime.Type.GetMethod("Calculate")!.Invoke(receiver, arguments), Is.EqualTo(9));
        Assert.That(arguments, Is.EqualTo(new object[] { 10, 4 }), "By-value parameter mutation must remain local to the call.");
    }

    [Test]
    public void RenamedThisStillReturnsTheSameReceiver()
    {
        var (context, definition, _) = CreateMethod("Self", _app.SystemTypes.SystemObjectType, [], instance: true);
        var receiver = context.ParameterLocals.Single();
        receiver.Name = "renamedReceiver";
        Emit(context, definition, [new(0, OpCode.Return, receiver)]);
        AddDefaultConstructor();

        using var runtime = Load();
        var instance = Activator.CreateInstance(runtime.Type);
        Assert.That(runtime.Type.GetMethod("Self")!.Invoke(instance, null), Is.SameAs(instance));
    }

    [Test]
    public void ByReferenceIntegerStoreMutatesTheCallerAndReturnsTheStoredValue()
    {
        var (context, definition, parameters) = CreateMethod("Replace", _app.SystemTypes.SystemInt32Type,
            [new ByRefTypeAnalysisContext(_app.SystemTypes.SystemInt32Type)]);
        var destination = new MemoryOperand(parameters[0]);
        Emit(context, definition, [new(0, OpCode.Move, destination, Imm(-71)), new(1, OpCode.Return, destination)]);

        using var runtime = Load();
        object?[] arguments = [123];
        Assert.That(runtime.Type.GetMethod("Replace")!.Invoke(null, arguments), Is.EqualTo(-71));
        Assert.That(arguments[0], Is.EqualTo(-71));
    }

    [Test]
    public void ByReferenceObjectZeroStoreIsNullAndMutatesTheCaller()
    {
        var (context, definition, parameters) = CreateMethod("Clear", _app.SystemTypes.SystemObjectType,
            [new ByRefTypeAnalysisContext(_app.SystemTypes.SystemObjectType)]);
        var destination = new MemoryOperand(parameters[0]);
        Emit(context, definition, [new(0, OpCode.Move, destination, Imm(0)), new(1, OpCode.Return, destination)]);

        using var runtime = Load();
        object?[] arguments = [new object()];
        Assert.That(runtime.Type.GetMethod("Clear")!.Invoke(null, arguments), Is.Null);
        Assert.That(arguments[0], Is.Null);
    }

    [Test]
    public void TakingAParameterAddressPassesItsActualStorageToTheCallee()
    {
        var (callee, calleeDefinition, _) = CreateMethod("Increment", _app.SystemTypes.SystemVoidType,
            [new ByRefTypeAnalysisContext(_app.SystemTypes.SystemInt32Type)]);
        // Independent managed helper: ++value. The caller is the method generated by Cpp2IL.
        calleeDefinition.CilMethodBody = new CilMethodBody();
        var il = calleeDefinition.CilMethodBody.Instructions;
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Ldind_I4);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stind_I4);
        il.Add(CilOpCodes.Ret);

        var (context, definition, parameters) = CreateMethod("IncrementCopy", _app.SystemTypes.SystemInt32Type, [_app.SystemTypes.SystemInt32Type]);
        parameters[0].Name = "changedName";
        Emit(context, definition,
        [
            new(0, OpCode.CallVoid, callee, new AddressOf(parameters[0])),
            new(1, OpCode.Return, parameters[0]),
        ]);

        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("IncrementCopy")!.Invoke(null, [41]), Is.EqualTo(42));
    }

    [Test]
    public void AdjacentConstructorRunsExactlyOnceWithRecoveredArguments()
    {
        var constructor = AddValueConstructor();
        var (context, definition, _) = CreateMethod("Construct", _app.SystemTypes.SystemObjectType, []);
        var result = new LocalVariable("result", new Register(900, "result"), _typeContext);
        context.Locals.Add(result);
        Emit(context, definition,
        [
            new(0, OpCode.Newobj, result, _typeContext),
            new(1, OpCode.CallVoid, constructor, result, Imm(29)),
            new(2, OpCode.Return, result),
        ]);

        using var runtime = Load();
        var instance = runtime.Type.GetMethod("Construct")!.Invoke(null, null);
        Assert.That(runtime.Type.GetField("Value")!.GetValue(instance), Is.EqualTo(29));
        Assert.That(runtime.Type.GetField("ConstructorCalls")!.GetValue(null), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConstructorCannotBeFusedAcrossAnEffectOrBranch(bool branch)
    {
        var constructor = AddValueConstructor();
        var (effect, effectDefinition, _) = CreateMethod("Effect", _app.SystemTypes.SystemVoidType, []);
        effectDefinition.CilMethodBody = new CilMethodBody();
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_7);
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Stsfld, _type.Fields.Single(f => f.Name == "ConstructorCalls"));
        effectDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var (context, definition, _) = CreateMethod("Construct", _app.SystemTypes.SystemObjectType, []);
        var result = new LocalVariable("result", new Register(900, "result"), _typeContext);
        context.Locals.Add(result);
        var constructorCall = new Instruction(2, OpCode.CallVoid, constructor, result, Imm(29));
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            new(0, OpCode.Newobj, result, _typeContext),
            branch ? new(1, OpCode.Jump, constructorCall) : new(1, OpCode.CallVoid, effect),
            constructorCall,
            new(3, OpCode.Return, result),
        ]);

        // No executable IL is expected: the current generator deliberately rejects this
        // unproved reordering. Verify that it also leaves the constructor call intact.
        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("no recovered constructor call"));
        Assert.That(constructorCall.OpCode, Is.EqualTo(OpCode.CallVoid));
    }

    [Test]
    public void ConsumedNativeResultOfAVoidMethodCannotBecomeDefaultZero()
    {
        var (callee, calleeDefinition, _) = CreateMethod("NoManagedResult", _app.SystemTypes.SystemVoidType, []);
        calleeDefinition.CilMethodBody = new CilMethodBody();
        calleeDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var (context, definition, _) = CreateMethod("ReadNativeResult", _app.SystemTypes.SystemInt32Type, []);
        var result = new LocalVariable("result", new Register(900, "result"), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(result);
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            new(0, OpCode.Call, callee, result),
            new(1, OpCode.Return, result),
        ]);

        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(context, definition));
    }

    [Test]
    public void DiscardedNativeResultOfAVoidMethodPreservesTheCallEffect()
    {
        var observable = new FieldDefinition("Observable", FieldAttributes.Public | FieldAttributes.Static, _module.CorLibTypeFactory.Int32);
        _type.Fields.Add(observable);
        var (callee, calleeDefinition, _) = CreateMethod("EffectOnly", _app.SystemTypes.SystemVoidType, []);
        calleeDefinition.CilMethodBody = new CilMethodBody();
        calleeDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_5);
        calleeDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Stsfld, observable);
        calleeDefinition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var (context, definition, _) = CreateMethod("CallEffect", _app.SystemTypes.SystemInt32Type, []);
        var ignored = new LocalVariable("ignored", new Register(900, "ignored"), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(ignored);
        Emit(context, definition, [new(0, OpCode.Call, callee, ignored), new(1, OpCode.Return, Imm(11))]);

        using var runtime = Load();
        Assert.That(runtime.Type.GetMethod("CallEffect")!.Invoke(null, null), Is.EqualTo(11));
        Assert.That(runtime.Type.GetField("Observable")!.GetValue(null), Is.EqualTo(5));
    }

    [Test]
    public void ConstructorFusionCannotDiscardAConsumedNativeCallResult()
    {
        var constructor = AddValueConstructor();
        var (context, definition, _) = CreateMethod("ReadConstructorResult", _app.SystemTypes.SystemObjectType, []);
        var allocated = new LocalVariable("allocated", new Register(900, "allocated"), _typeContext);
        var nativeResult = new LocalVariable("result", new Register(901, "result"), _typeContext);
        context.Locals.AddRange([allocated, nativeResult]);
        context.ControlFlowGraph = new ISILControlFlowGraph(
        [
            new(0, OpCode.Newobj, allocated, _typeContext),
            new(1, OpCode.Call, constructor, nativeResult, allocated, Imm(29)),
            new(2, OpCode.Return, nativeResult),
        ]);

        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(context, definition));
    }

    private (InjectedMethodAnalysisContext Context, MethodDefinition Definition, LocalVariable[] Parameters) CreateMethod(
        string name, TypeAnalysisContext returnType, TypeAnalysisContext[] parameterTypes, bool instance = false)
    {
        var attributes = ReflectionMethodAttributes.Public | (instance ? 0 : ReflectionMethodAttributes.Static);
        var context = new InjectedMethodAnalysisContext(_typeContext, name, returnType, attributes, parameterTypes)
        {
            Locals = [], ParameterLocals = [], ParameterOperands = [], AnalysisWarnings = [],
        };
        var returnSignature = _module.DefaultImporter.ImportTypeSignature(returnType.ToTypeSignature());
        var parameters = parameterTypes.Select(t => _module.DefaultImporter.ImportTypeSignature(t.ToTypeSignature())).ToArray();
        var signature = instance ? MethodSignature.CreateInstance(returnSignature, parameters) : MethodSignature.CreateStatic(returnSignature, parameters);
        var definition = new MethodDefinition(name, (MethodAttributes)attributes, signature);
        _type.Methods.Add(definition);
        context.PutExtraData("AsmResolverMethod", definition);

        if (instance)
        {
            var receiver = new LocalVariable("this", new Register(99, "receiver"), _typeContext) { IsThis = true };
            context.ParameterOperands.Add(receiver.Register);
            context.ParameterLocals.Add(receiver);
            context.Locals.Add(receiver);
        }
        var parameterLocals = parameterTypes.Select((t, i) => new LocalVariable("arg" + i, new Register(100 + i, "input" + i), t)).ToArray();
        foreach (var parameter in parameterLocals)
        {
            context.ParameterOperands.Add(parameter.Register);
            context.ParameterLocals.Add(parameter);
            context.Locals.Add(parameter);
        }
        return (context, definition, parameterLocals);
    }

    private static void Emit(MethodAnalysisContext context, MethodDefinition definition, List<Instruction> instructions)
    {
        context.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        IlGenerator.GenerateIl(context, definition);
    }

    private void AddDefaultConstructor()
    {
        var (_, definition, _) = CreateMethod(".ctor", _app.SystemTypes.SystemVoidType, [], instance: true);
        definition.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName;
        definition.CilMethodBody = new CilMethodBody();
        definition.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
        definition.CilMethodBody.Instructions.Add(CilOpCodes.Call, _module.CorLibTypeFactory.Object.Type.CreateMemberReference(".ctor", MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void)));
        definition.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
    }

    private InjectedMethodAnalysisContext AddValueConstructor()
    {
        var value = new FieldDefinition("Value", FieldAttributes.Public, _module.CorLibTypeFactory.Int32);
        var calls = new FieldDefinition("ConstructorCalls", FieldAttributes.Public | FieldAttributes.Static, _module.CorLibTypeFactory.Int32);
        _type.Fields.Add(value);
        _type.Fields.Add(calls);
        var (context, definition, _) = CreateMethod(".ctor", _app.SystemTypes.SystemVoidType, [_app.SystemTypes.SystemInt32Type], instance: true);
        definition.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName;
        definition.CilMethodBody = new CilMethodBody();
        var il = definition.CilMethodBody.Instructions;
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Call, _module.CorLibTypeFactory.Object.Type.CreateMemberReference(".ctor", MethodSignature.CreateInstance(_module.CorLibTypeFactory.Void)));
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Stfld, value);
        il.Add(CilOpCodes.Ldsfld, calls);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stsfld, calls);
        il.Add(CilOpCodes.Ret);
        return context;
    }

    private LoadedAssembly Load()
    {
        using var stream = new MemoryStream();
        _assembly.WriteManifest(stream, new ManagedPEImageBuilder(ThrowErrorListener.Instance));
        stream.Position = 0;
        var context = new AssemblyLoadContext("Cpp2IL synthetic IL", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            return new LoadedAssembly(context, assembly.GetType("Synthetic.Generated", throwOnError: true)!);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private sealed class LoadedAssembly(AssemblyLoadContext context, Type type) : IDisposable
    {
        public Type Type { get; } = type;
        public void Dispose() => context.Unload();
    }
}
