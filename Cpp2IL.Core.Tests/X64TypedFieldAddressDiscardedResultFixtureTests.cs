using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64TypedFieldAddressDiscardedResultFixtureTests
{
    private Dictionary<string, MethodAnalysisContext> _methods = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_TYPED_FIELD_ADDRESS_DISCARDED_RESULT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_TYPED_FIELD_ADDRESS_DISCARDED_RESULT_FIXTURE_INPUT to the neutral exact player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("TypedFieldAddressDiscardedResultFixture")!.Types
            .Single(type => type.Name == "AddressResultOwner").Methods
            .Where(method => method.Name != ".ctor").ToDictionary(method => method.Name);
        foreach (var method in _methods.Values) method.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("ResetFirstDiscard", 1)]
    [TestCase("ResetBothDiscard", 2)]
    [TestCase("ApplyDefault", 1)]
    [TestCase("GuardedApplyDefault", 1)]
    public void ExactCallsPreserveFieldStorageAndDiscardOnlyTheEnumResult(string name, int count)
    {
        var method = _methods[name];
        Accept(method);
        var calls = method.ControlFlowGraph!.Instructions.Where(operation => operation.IsCall).ToArray();
        Assert.That(calls, Has.Length.EqualTo(count));
        foreach (var call in calls)
        {
            var target = (MethodAnalysisContext)call.Operands[0];
            Assert.That(call.OpCode, Is.EqualTo(OpCode.CallVoid));
            Assert.That(target.IsVoid, Is.False);
            Assert.That(target.ReturnType.EnumUnderlyingType, Is.SameAs(method.AppContext.SystemTypes.SystemInt32Type));
            Assert.That(call.Operands[1], Is.TypeOf<LocalVariable>());
            Assert.That(((LocalVariable)call.Operands[1]).Type, Is.TypeOf<ByRefTypeAnalysisContext>());
            if (target.Parameters.Count != 0)
            {
                Assert.That(call.Operands[2], Is.TypeOf<Immediate>());
                Assert.That(((Immediate)call.Operands[2]).Value, Is.Zero);
            }
        }
        Assert.That(Definition(method).CilMethodBody!.Instructions.Count(operation => operation.OpCode == CilOpCodes.Pop), Is.EqualTo(count));
        Assert.That(Definition(method).CilMethodBody!.Instructions.Count(operation => operation.OpCode == CilOpCodes.Ldflda), Is.EqualTo(count));
        Accept(method); // Generated labels must retain the same admitted branches.
    }

    [Test]
    public void CompleteEnumCalleeKeepsTheQwordStepAndInt32ParameterReturn()
    {
        var target = Target(_methods["ApplyDefault"]);
        target.Analyze();
        var operations = target.ControlFlowGraph!.Instructions.ToArray();
        Assert.That(operations.Single(operation => operation.OpCode == OpCode.Add).IntegerBitWidth, Is.EqualTo(64));
        var returned = (LocalVariable)operations.Single(operation => operation.OpCode == OpCode.Return).Operands[0];
        Assert.That(returned.Type, Is.SameAs(target.Parameters[0].ParameterType));
        Assert.That(returned.Type!.EnumUnderlyingType, Is.SameAs(target.AppContext.SystemTypes.SystemInt32Type));
        IlGenerator.GenerateIl(target, Definition(target));
        var il = Definition(target).CilMethodBody!.Instructions.ToArray();
        Assert.That(il.Single(operation => operation.OpCode == CilOpCodes.Ldc_I8).Operand, Is.EqualTo(1L));
        Assert.That(il.Any(operation => operation.OpCode == CilOpCodes.Ldc_I4 ||
            operation.OpCode == CilOpCodes.Conv_I4 || operation.OpCode == CilOpCodes.Conv_I8), Is.False);
        Assert.That(il[^2].OpCode, Is.EqualTo(CilOpCodes.Ldarg));
        Assert.That(il[^1].OpCode, Is.EqualTo(CilOpCodes.Ret));
    }

    [TestCase("argument")]
    [TestCase("return")]
    [TestCase("call-width")]
    [TestCase("call-result")]
    [TestCase("callee")]
    public void FinalCallResultAndLiteralMutationsCannotChangeTheProvedEffect(string mutation)
    {
        var method = _methods["ApplyDefault"];
        Accept(method);
        var call = method.ControlFlowGraph!.Instructions.Single(operation => operation.IsCall);
        var returned = method.ControlFlowGraph.Instructions.Single(operation => operation.OpCode == OpCode.Return);
        var target = (MethodAnalysisContext)call.Operands[0];
        var operands = call.Operands.ToArray();
        var returnOperands = returned.Operands.ToArray();
        var opcode = call.OpCode;
        var width = call.IntegerBitWidth;
        try
        {
            if (mutation == "argument") call.SetOperand(2, new Immediate(1));
            if (mutation == "return") returned.SetOperands(new Immediate(0));
            if (mutation == "call-width") call.IntegerBitWidth = 32;
            if (mutation == "call-result")
            {
                call.OpCode = OpCode.Call;
                call.SetOperands(target, new LocalVariable("unexpectedResult", new Register(null, "rax"), target.ReturnType), operands[1], operands[2]);
            }
            if (mutation == "callee") call.SetOperand(0, target.DeclaringType!.Methods.Single(candidate => candidate.Name == "ResetAndReport"));
            Reject(method);
        }
        finally
        {
            call.OpCode = opcode; call.IntegerBitWidth = width;
            call.SetOperands(operands.ToList()); returned.SetOperands(returnOperands.ToList());
        }
        Accept(method);
    }

    [TestCase("underlying-kind")]
    [TestCase("underlying-data")]
    [TestCase("enum-data")]
    [TestCase("backing-data")]
    [TestCase("base-data")]
    [TestCase("base-modifiers")]
    [TestCase("base-kind")]
    [TestCase("base-bits")]
    [TestCase("constant")]
    [TestCase("raw-flags")]
    public void EnumDeclarationsAndRawStorageRemainImmutable(string mutation)
    {
        var method = _methods["ApplyDefault"];
        Accept(method);
        var target = Target(method);
        var type = target.Parameters[0].ParameterType;
        var definition = type.Definition!;
        var underlying = definition.EnumUnderlyingType;
        var rawBase = definition.RawBaseType!;
        var backing = type.Fields.Single(field => !field.IsStatic);
        var literal = type.Fields.First(field => field.IsStatic);
        var kind = underlying.Type;
        var underlyingData = underlying.Data;
        var enumData = definition.RawType.Data;
        var backingData = backing.BackingData!.Field.RawFieldType!.Data;
        var baseData = rawBase.Data;
        var baseModifiers = rawBase.NumMods;
        var baseKind = rawBase.Type;
        var baseBits = rawBase.Bits;
        var useConstant = literal.UseOverrideConstantValue;
        var constant = literal.OverrideConstantValue;
        var flags = definition.Flags;
        try
        {
            if (mutation == "underlying-kind") underlying.Type = Il2CppTypeEnum.IL2CPP_TYPE_U4;
            if (mutation == "underlying-data") underlying.Data = null!;
            if (mutation == "enum-data") definition.RawType.Data = null!;
            if (mutation == "backing-data") backing.BackingData.Field.RawFieldType!.Data = null!;
            if (mutation == "base-data") rawBase.Data = null!;
            if (mutation == "base-modifiers") rawBase.NumMods = 1;
            if (mutation == "base-kind") rawBase.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
            if (mutation == "base-bits") rawBase.Bits ^= 1;
            if (mutation == "constant") { literal.UseOverrideConstantValue = true; literal.OverrideConstantValue = 8; }
            if (mutation == "raw-flags") definition.Flags ^= 1;
            Assert.DoesNotThrow(() => Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False));
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally
        {
            underlying.Type = kind; underlying.Data = underlyingData; definition.RawType.Data = enumData;
            backing.BackingData.Field.RawFieldType!.Data = backingData;
            rawBase.Data = baseData; rawBase.NumMods = baseModifiers; rawBase.Type = baseKind; rawBase.Bits = baseBits;
            literal.UseOverrideConstantValue = useConstant; literal.OverrideConstantValue = constant;
            definition.Flags = flags;
        }
        Accept(method);
    }

    [TestCase("native-caller")]
    [TestCase("native-callee")]
    [TestCase("effect-order")]
    [TestCase("guard")]
    public void NativeFaultEffectOrderAndNoOpControlCannotDrift(string mutation)
    {
        var method = _methods[mutation == "guard" ? "GuardedApplyDefault" : "ResetBothDiscard"];
        Accept(method);
        var graph = method.ControlFlowGraph!;
        var target = Target(method);
        var callerBytes = method.RawBytes;
        var calleeBytes = target.RawBytes;
        var block = graph.Blocks.Single(candidate => candidate.Instructions.Count(operation => operation.IsCall) ==
            (mutation == "guard" ? 1 : 2));
        var order = block.Instructions.ToArray();
        var branch = graph.Instructions.SingleOrDefault(operation => operation.OpCode == OpCode.ConditionalJump);
        var branchOperands = branch?.Operands.ToArray();
        try
        {
            if (mutation == "native-caller" || mutation == "native-callee")
            {
                var changed = (mutation == "native-caller" ? method.RawBytes : target.RawBytes).AsSpan().ToArray();
                changed[0] ^= 1;
                if (mutation == "native-caller") method.RawBytes = new BinarySlice(changed);
                else target.RawBytes = new BinarySlice(changed);
            }
            if (mutation == "effect-order")
            {
                var calls = block.Instructions.Where(operation => operation.IsCall).ToArray();
                var first = block.Instructions.IndexOf(calls[0]);
                var second = block.Instructions.IndexOf(calls[1]);
                block.Instructions[first] = calls[1]; block.Instructions[second] = calls[0];
            }
            if (mutation == "guard") branch!.SetOperand(1, new Immediate(1));
            Reject(method);
        }
        finally
        {
            method.RawBytes = callerBytes; target.RawBytes = calleeBytes;
            block.Instructions.Clear(); block.Instructions.AddRange(order);
            if (branchOperands != null) branch!.SetOperands(branchOperands.ToList());
        }
        Accept(method);
    }

    [Test]
    public void InitialResultConsumersPreventTheDiscardProjection()
    {
        var method = _methods["ResetFirstDiscard"];
        Accept(method);
        var graph = method.ControlFlowGraph!;
        var address = graph.Instructions.Single(operation => operation.Operands is [_, AddressOf]);
        var field = (FieldReference)((AddressOf)address.Operands[1]).Target;
        var pointer = (LocalVariable)address.Operands[0];
        var call = graph.Instructions.Single(operation => operation.IsCall);
        var target = (MethodAnalysisContext)call.Operands[0];
        var block = graph.Blocks.Single(candidate => candidate.Instructions.Contains(call));
        var result = new LocalVariable("result", new Register(null, "rax"), target.ReturnType);
        var copied = new LocalVariable("copy", new Register(null, "rdx"), target.ReturnType);
        var consumer = new Instruction(0, OpCode.Move, copied, result);
        var addressOperands = address.Operands.ToArray();
        var callOperands = call.Operands.ToArray();
        var pointerType = pointer.Type;
        try
        {
            address.OpCode = OpCode.Add; address.IntegerBitWidth = 64;
            address.SetOperands(pointer, field.Local, new Immediate(field.Offset)); pointer.Type = field.Field.FieldType;
            call.OpCode = OpCode.Call; call.SetOperands(target, result, pointer);
            Assert.That(X64TypedFieldAddressProof.Find(method, false), Is.Not.Null);
            block.Instructions.Insert(block.Instructions.IndexOf(call) + 1, consumer);
            Assert.That(X64TypedFieldAddressProof.HasUnusedEnumResult(method, target, call, false), Is.False);
            Assert.That(X64TypedFieldAddressProof.Find(method, false), Is.Null);
            Reject(method);
        }
        finally
        {
            block.Instructions.Remove(consumer); pointer.Type = pointerType;
            address.OpCode = OpCode.Move; address.IntegerBitWidth = 0; address.SetOperands(addressOperands.ToList());
            call.OpCode = OpCode.CallVoid; call.SetOperands(callOperands.ToList());
        }
        Accept(method);
    }

    private static MethodAnalysisContext Target(MethodAnalysisContext method)
        => (MethodAnalysisContext)method.ControlFlowGraph!.Instructions.First(operation => operation.IsCall).Operands[0];

    private static MethodDefinition Definition(MethodAnalysisContext method)
        => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private static void Accept(MethodAnalysisContext method)
    {
        Assert.That(TypedFieldAddressRecovery.HasEvidence(method), Is.True);
        Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, Definition(method)));
    }

    private static void Reject(MethodAnalysisContext method)
    {
        Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
    }
}
