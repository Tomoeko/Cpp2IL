using System;
using System.Collections.Generic;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

public class IlRecoverySafetyTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [TestCase(OpCode.NotImplemented)]
    [TestCase(OpCode.Phi)]
    [TestCase(OpCode.Interrupt)]
    public void UnresolvedInstructionCannotBecomeSuccessfulNoOp(OpCode opcode)
    {
        var (context, definition) = CreateMethod(
            [new(0, opcode, Imm(0)), new(1, OpCode.Return)]);

        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(context, definition));
    }

    [Test]
    public void MissingReturnValueCannotBecomeNull()
    {
        var (context, definition) = CreateMethod([new(0, OpCode.Return)], returnsInt: true);

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Return value is unresolved"));
    }

    [Test]
    public void MissingCallArgumentCannotBecomeZero()
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var (context, definition) = CreateMethod([new(1, OpCode.Return)]);
        var target = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "Target",
            app.SystemTypes.SystemVoidType, ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [app.SystemTypes.SystemInt32Type]);
        var module = definition.DeclaringModule!;
        var targetDefinition = new MethodDefinition("Target", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int32]));
        definition.DeclaringType!.Methods.Add(targetDefinition);
        target.PutExtraData("AsmResolverMethod", targetDefinition);
        context.ControlFlowGraph = new ISILControlFlowGraph([new(0, OpCode.CallVoid, target), new(1, OpCode.Return)]);

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Call argument 0 is unresolved"));
    }

    [Test]
    public void UnknownLocalTypeCannotBecomeObject()
    {
        var local = new LocalVariable("unknown", new Register(null, "unknown"));
        var (context, definition) = CreateMethod([new(0, OpCode.Move, local, Imm(1)), new(1, OpCode.Return)]);
        context.Locals.Add(local);

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Local type is unresolved"));
    }

    [TestCase(OpCode.NotImplemented)]
    [TestCase(OpCode.UnresolvedValue)]
    public void LiftingFailureIsDiagnosedBeforeItsUnknownResultType(OpCode opcode)
    {
        var local = new LocalVariable("unknown", new Register(null, "unknown"));
        var (context, definition) = CreateMethod(
            [new(0, opcode, local, new StringLiteral("Synthetic flag value is unresolved")), new(1, OpCode.Return)]);
        context.Locals.Add(local);

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Synthetic flag value is unresolved"));
        Assert.That(definition.CilMethodBody, Is.Null, "Preflight failure must not leave a partially generated body.");
    }

    [Test]
    public void UnresolvedNativeStoreCannotBeDiscarded()
    {
        var (context, definition) = CreateMethod(
            [new(0, OpCode.Move, new MemoryOperand(addend: 4096), Imm(1)), new(1, OpCode.Return)]);

        Assert.That(() => IlGenerator.GenerateIl(context, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Memory store is unresolved"));
    }

    [Test]
    public void CompleteConstantReturnHasValidatedStackAndNoDiagnosticSideEffects()
    {
        var (context, definition) = CreateMethod([new(0, OpCode.Return, Imm(42))], returnsInt: true);
        context.AnalysisWarnings.Add("A report-only observation");

        IlGenerator.GenerateIl(context, definition);

        var body = definition.CilMethodBody!;
        Assert.Multiple(() =>
        {
            Assert.That(body.ComputeMaxStackOnBuild, Is.True);
            Assert.That(body.VerifyLabelsOnBuild, Is.True);
            Assert.That(body.Instructions.Count, Is.EqualTo(2), "Warnings belong in reports, not executable code.");
        });
        Assert.DoesNotThrow(() => body.ComputeMaxStack());
    }

    private static (MethodAnalysisContext Context, MethodDefinition Definition) CreateMethod(
        List<Instruction> instructions, bool returnsInt = false)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var context = new InjectedMethodAnalysisContext(app.SystemTypes.SystemObjectType, "Synthetic",
            returnsInt ? app.SystemTypes.SystemInt32Type : app.SystemTypes.SystemVoidType,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static, [])
        {
            ControlFlowGraph = new ISILControlFlowGraph(instructions),
            Locals = [],
            ParameterLocals = [],
            AnalysisWarnings = []
        };
        var module = new ModuleDefinition("RecoverySafety.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var type = new TypeDefinition("Synthetic", "RecoverySafety", TypeAttributes.Public | TypeAttributes.Class,
            module.CorLibTypeFactory.Object.ToTypeDefOrRef());
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Synthetic", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(returnsInt ? module.CorLibTypeFactory.Int32 : module.CorLibTypeFactory.Void));
        type.Methods.Add(method);
        return (context, method);
    }
}
