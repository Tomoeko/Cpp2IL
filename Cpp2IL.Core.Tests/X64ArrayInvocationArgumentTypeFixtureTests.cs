using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests;

/// <summary>Typed copy chains must not introduce managed truncation into an authenticated argument.</summary>
[NonParallelizable]
public class X64ArrayInvocationArgumentTypeFixtureTests
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext _method = null!;
    private MethodAnalysisContext _storeMethod = null!;

    [OneTimeSetUp]
    public void LoadFixture()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_CALL_ORIGINS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_ARRAY_CALL_ORIGINS_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        _method = Method("CaptureBeforeEffect");
        _method.Analyze();
        IlGenerator.ValidateGuardedArrayOperations(_method);
        _storeMethod = Method("ChangeValue");
        _storeMethod.Analyze();
    }

    [OneTimeTearDown]
    public void ReleaseFixture() => Cpp2IlApi.ResetInternalState();

    [TestCase(false)]
    [TestCase(true)]
    public void IncomingArgumentCannotNarrowInAnIntermediateCopy(bool narrow)
    {
        var call = _method.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall &&
            instruction.Operands[0] is MethodAnalysisContext { Name: "ChangeValue" });
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        var original = call.Operands[argumentIndex];
        var intermediate = Local("intermediate", narrow ? _app.SystemTypes.SystemInt16Type :
            _app.SystemTypes.SystemInt32Type);
        var result = Local("argument", _app.SystemTypes.SystemInt32Type);
        var first = new Instruction(-1, OpCode.Move, intermediate, original);
        var second = new Instruction(-1, OpCode.Move, result, intermediate);
        var block = _method.ControlFlowGraph.FindBlockByInstruction(call)!;
        var position = block.Instructions.IndexOf(call);
        try
        {
            block.Instructions.Insert(position, first);
            block.Instructions.Insert(position + 1, second);
            call.SetOperand(argumentIndex, result);
            if (narrow)
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method),
                    "An Int16 copy would truncate a valid Int32 input before the managed call.");
            else
                Assert.DoesNotThrow(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally
        {
            call.SetOperand(argumentIndex, original);
            block.Instructions.Remove(second);
            block.Instructions.Remove(first);
        }
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ScalarArrayIndexAndStoreCannotNarrowInAnIntermediateCopy(bool storeValue, bool narrow)
    {
        Assert.That(_storeMethod.GetExtraData<X64GuardedArrayOperationProof.Evidence>(
            X64GuardedArrayOperationProof.EvidenceKey), Is.Not.Null);
        var operation = _storeMethod.ControlFlowGraph!.Instructions.Single(instruction =>
            instruction.Operands is [ArrayAccess, _]);
        var access = (ArrayAccess)operation.Operands[0];
        var original = storeValue ? operation.Operands[1] : access.Index;
        var intermediate = Local("intermediate", narrow ? _app.SystemTypes.SystemInt16Type :
            _app.SystemTypes.SystemInt32Type);
        var result = Local("array-operand", _app.SystemTypes.SystemInt32Type);
        var first = new Instruction(-1, OpCode.Move, intermediate, original);
        var second = new Instruction(-1, OpCode.Move, result, intermediate);
        var block = _storeMethod.ControlFlowGraph.FindBlockByInstruction(operation)!;
        var position = block.Instructions.IndexOf(operation);
        try
        {
            block.Instructions.Insert(position, first);
            block.Instructions.Insert(position + 1, second);
            if (storeValue) operation.SetOperand(1, result);
            else access.Index = result;
            if (narrow)
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_storeMethod),
                    "An Int16 copy cannot preserve the native Int32 array index or stored value.");
            else
                Assert.DoesNotThrow(() => IlGenerator.ValidateGuardedArrayOperations(_storeMethod));
        }
        finally
        {
            if (storeValue) operation.SetOperand(1, original);
            else access.Index = original;
            block.Instructions.Remove(second);
            block.Instructions.Remove(first);
        }
        IlGenerator.ValidateGuardedArrayOperations(_storeMethod);
    }

    [TestCase("call", false, false)]
    [TestCase("call", true, false)]
    [TestCase("call", false, true)]
    [TestCase("field", false, false)]
    [TestCase("field", true, false)]
    [TestCase("field", false, true)]
    public void CapturedArgumentRetainsItsWidthAtDefinitionAndEveryCopy(string source, bool narrowCapture, bool narrowCopy)
    {
        // The real fixture currently invokes ChangeValue with incoming integers.
        // These synthetic capture graphs test the argument validator in isolation;
        // they do not claim native recovery of a field or scalar producer shape.
        var target = Method("ChangeValue");
        var captured = Local("capture", narrowCapture ? _app.SystemTypes.SystemInt16Type :
            _app.SystemTypes.SystemInt32Type);
        var intermediate = Local("intermediate", narrowCopy ? _app.SystemTypes.SystemInt16Type :
            _app.SystemTypes.SystemInt32Type);
        var result = Local("argument", _app.SystemTypes.SystemInt32Type);
        Instruction definition;
        X64GuardedArrayOperationProof.InvocationArgument argument;
        var origin = new X64GuardedArrayOperationProof.ValueOrigin(NativeRegister.RAX, 0x100);
        if (source == "call")
        {
            definition = new Instruction(0, OpCode.Call, _method, captured);
            argument = new(0x200, target, 0, 32, origin, null, Producer: _method);
        }
        else
        {
            var owner = _method.ParameterLocals.Single(local =>
                LocalVariables.GetIncomingParameterIndex(_method, local) == 0);
            var field = target.DeclaringType!.Fields.Single(field => field.Name == "Neighbor");
            definition = new Instruction(0, OpCode.Move, captured, new FieldReference(field, owner, field.Offset));
            argument = new(0x200, target, 0, 32, origin, null, field, NativeRegister.RCX);
        }
        definition.NativeAddress = 0x100;
        var instructions = new List<Instruction>
        {
            definition, new(1, OpCode.Move, intermediate, captured), new(2, OpCode.Move, result, intermediate)
        };
        var validator = typeof(IlGenerator).GetMethod("ValidInvocationArgument", BindingFlags.NonPublic | BindingFlags.Static)!;
        var accepted = (bool)validator.Invoke(null,
            [_method, instructions, result, instructions.Count, argument, new HashSet<Instruction>()])!;

        Assert.That(accepted, Is.EqualTo(!narrowCapture && !narrowCopy),
            "A captured Int32 must survive both its storage and subsequent copies without Int16 truncation.");
    }

    private static LocalVariable Local(string name, TypeAnalysisContext type) =>
        new(name, new ISIL.Register(null, name), type);

    private MethodAnalysisContext Method(string name) =>
        _app.GetAssemblyByName("ArrayCallOriginsFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);
}
