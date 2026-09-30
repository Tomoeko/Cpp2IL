using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using NativeCode = Iced.Intel.Code;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64TypedFieldAddressFixtureTests
{
    private Dictionary<string, MethodAnalysisContext> _methods = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_TYPED_FIELD_ADDRESS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_TYPED_FIELD_ADDRESS_FIXTURE_INPUT to the neutral exact player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("TypedFieldAddressFixture")!.Types.Single(type => type.Name == "StorageOwner")
            .Methods.Where(method => method.Name != ".ctor").ToDictionary(method => method.Name);
        foreach (var method in _methods.Values) method.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("AdvanceFirst")]
    [TestCase("AdvanceSecond")]
    [TestCase("AdvanceSmall")]
    [TestCase("ResetFirst")]
    [TestCase("ReadFirst")]
    [TestCase("GuardedAdvance")]
    [TestCase("AdvanceTwice")]
    public void ActualAddressesPreserveSharedStorageAndExactManagedCalleeIdentity(string name)
    {
        var method = _methods[name];
        Accept(method);
        var address = method.ControlFlowGraph!.Instructions.Single(operation =>
            operation.Operands is [LocalVariable, AddressOf { Target: FieldReference }]);
        var pointer = (LocalVariable)address.Operands[0];
        var field = (FieldReference)((AddressOf)address.Operands[1]).Target;
        Assert.That(pointer.Type, Is.TypeOf<ByRefTypeAnalysisContext>());
        Assert.That(((ByRefTypeAnalysisContext)pointer.Type!).ElementType, Is.SameAs(field.Field.FieldType));
        Assert.That(address.IntegerBitWidth, Is.Zero);
        var calls = method.ControlFlowGraph.Instructions.Where(operation => operation.IsCall).ToArray();
        Assert.That(calls.Length, Is.EqualTo(name == "AdvanceTwice" ? 2 : 1));
        foreach (var call in calls)
        {
            var target = (MethodAnalysisContext)call.Operands[0];
            Assert.That(target.DeclaringType, Is.SameAs(field.Field.FieldType));
            Assert.That(call.Operands[target.IsVoid ? 1 : 2], Is.SameAs(pointer));
        }
        Assert.That(Definition(method).CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldflda), Is.EqualTo(1));
        Assert.That(Definition(method).CilMethodBody!.LocalVariables.Any(local => local.VariableType is ByReferenceTypeSignature), Is.True);
        // Branch operands become instruction labels during generation; repeated
        // validation must retain the same proved control-flow target.
        Accept(method);
    }

    [TestCase("AdvanceFirst", "offset")]
    [TestCase("AdvanceFirst", "field")]
    [TestCase("AdvanceFirst", "width")]
    [TestCase("AdvanceFirst", "owner")]
    [TestCase("AdvanceFirst", "return")]
    [TestCase("AdvanceFirst", "callee")]
    [TestCase("AdvanceFirst", "operand")]
    [TestCase("AdvanceTwice", "effect-order")]
    [TestCase("AdvanceFirst", "return-order")]
    [TestCase("GuardedAdvance", "condition")]
    [TestCase("GuardedAdvance", "branch")]
    public void FinalAddressCallReturnAndControlMutationsCannotRetainAdmission(string name, string mutation)
    {
        var method = _methods[name];
        Accept(method);
        var graph = method.ControlFlowGraph!;
        var address = graph.Instructions.Single(operation => operation.Operands is [LocalVariable, AddressOf { Target: FieldReference }]);
        var field = (FieldReference)((AddressOf)address.Operands[1]).Target;
        var originalOffset = field.Offset;
        var originalField = field.Field;
        var originalOwner = field.Local;
        var width = address.IntegerBitWidth;
        var block = graph.Blocks.Single(candidate => candidate.Instructions.Contains(address));
        var order = block.Instructions.ToArray();
        Instruction changed = address;
        IOperand[]? operands = null;
        try
        {
            switch (mutation)
            {
                case "offset": field.Offset++; break;
                case "field": field.Field = method.DeclaringType!.Fields.Single(candidate => candidate.Name == "Second"); break;
                case "width": address.IntegerBitWidth = 64; break;
                case "owner": field.Local = new LocalVariable("other", originalOwner.Register, originalOwner.Type); break;
                case "effect-order":
                    var calls = block.Instructions.Where(operation => operation.IsCall).ToArray();
                    var first = block.Instructions.IndexOf(calls[0]);
                    var second = block.Instructions.IndexOf(calls[1]);
                    block.Instructions[first] = calls[1];
                    block.Instructions[second] = calls[0];
                    break;
                case "return-order":
                    var returned = block.Instructions.Single(operation => operation.OpCode == OpCode.Return);
                    block.Instructions.Remove(returned);
                    block.Instructions.Insert(1, returned);
                    Assert.That(X64TypedFieldAddressProof.Find(method, true), Is.Null);
                    break;
                default:
                    changed = mutation switch
                    {
                        "return" => graph.Instructions.Single(operation => operation.OpCode == OpCode.Return),
                        "callee" or "operand" => graph.Instructions.Single(operation => operation.IsCall),
                        "condition" or "branch" => graph.Instructions.Single(operation => operation.OpCode == OpCode.ConditionalJump),
                        _ => throw new ArgumentOutOfRangeException(nameof(mutation))
                    };
                    operands = changed.Operands.ToArray();
                    if (mutation == "return") changed.SetOperands(new Immediate(0));
                    if (mutation == "callee") changed.SetOperand(0, field.Field.FieldType.Methods.Single(method => method.Name == "Read"));
                    if (mutation == "operand") changed.SetOperand(changed.Operands.Count - 1, new Immediate(0));
                    if (mutation == "condition") changed.SetOperand(1, new Immediate(1));
                    if (mutation == "branch") changed.SetOperand(0, graph.Instructions.First(operation => operation.OpCode == OpCode.Return));
                    break;
            }
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally
        {
            field.Offset = originalOffset;
            field.Field = originalField;
            field.Local = originalOwner;
            address.IntegerBitWidth = width;
            block.Instructions.Clear();
            block.Instructions.AddRange(order);
            if (operands != null) changed.SetOperands(operands.ToList());
        }
        Accept(method);
    }

    [TestCase("AdvanceFirst", "layout")]
    [TestCase("AdvanceSmall", "field-type")]
    [TestCase("ReadFirst", "callee-native")]
    [TestCase("ResetFirst", "synchronized")]
    public void DeclarationAndCalleeFirstFaultFactsRemainImmutable(string name, string mutation)
    {
        var method = _methods[name];
        Accept(method);
        var field = (FieldReference)((AddressOf)method.ControlFlowGraph!.Instructions.Single(operation =>
            operation.Operands is [_, AddressOf { Target: FieldReference }]).Operands[1]).Target;
        var aggregate = field.Field.FieldType;
        var inner = aggregate.Fields.Single(candidate => !candidate.IsStatic);
        var target = (MethodAnalysisContext)method.ControlFlowGraph.Instructions.Single(operation => operation.IsCall).Operands[0];
        var attrs = aggregate.Attributes;
        var type = inner.OverrideFieldType;
        var bytes = target.RawBytes;
        var implementation = target.ImplAttributes;
        try
        {
            if (mutation == "layout") aggregate.Attributes = (aggregate.Attributes & ~TypeAttributes.LayoutMask) | TypeAttributes.ExplicitLayout;
            if (mutation == "field-type") inner.OverrideFieldType = method.AppContext.SystemTypes.SystemUInt32Type;
            if (mutation == "callee-native")
            {
                var changed = target.RawBytes.AsSpan().ToArray();
                changed[0] ^= 1;
                target.RawBytes = new BinarySlice(changed);
            }
            if (mutation == "synchronized") target.ImplAttributes |= MethodImplAttributes.Synchronized;
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally
        {
            aggregate.Attributes = attrs;
            inner.OverrideFieldType = type;
            target.RawBytes = bytes;
            target.ImplAttributes = implementation;
        }
        Accept(method);
    }

    [Test]
    public void LostBindingCannotFallBackToPublishingAFieldAddress()
    {
        var method = _methods["AdvanceFirst"];
        Accept(method);
        // The weak-table marker survives losing mutable method extra data.
        var original = method.GetExtraData<object>(TypedFieldAddressRecovery.EvidenceKey)!;
        try
        {
            method.PutExtraData<object>(TypedFieldAddressRecovery.EvidenceKey, null!);
            Assert.That(TypedFieldAddressRecovery.HasEvidence(method), Is.True);
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally { method.PutExtraData(TypedFieldAddressRecovery.EvidenceKey, original); }
        Accept(method);
    }

    [Test]
    public void NativePointerCalculationRequiresFullWidthAndAReservedNullFaultRange()
    {
        var method = _methods["AdvanceFirst"];
        var native = X64TypedFieldAddressProof.ReadBody(method)![0];
        Assert.That(X64TypedFieldAddressProof.AddressCalculation(native, out _, out _, out _), Is.True);
        var original = native;
        native.Op0Register = NativeRegister.ECX;
        Assert.That(X64TypedFieldAddressProof.AddressCalculation(native, out _, out _, out _), Is.False);
        native = original;
        native.Code = NativeCode.Add_rm32_imm8;
        Assert.That(X64TypedFieldAddressProof.AddressCalculation(native, out _, out _, out _), Is.False);
        Assert.That(X64TypedFieldAddressProof.NullAddressMustFault(0x10000 - 8, 8), Is.True);
        Assert.That(X64TypedFieldAddressProof.NullAddressMustFault(0x10000 - 7, 8), Is.False);
        Assert.That(X64TypedFieldAddressProof.NullAddressMustFault(0x10000, 4), Is.False);
    }

    [TestCase("owner")]
    [TestCase("aggregate")]
    [TestCase("field")]
    [TestCase("return")]
    [TestCase("parameter")]
    public void MissingRawTypeStorageDeclinesProofWithoutResolvingMalformedDescriptors(string origin)
    {
        var method = _methods["AdvanceFirst"];
        Accept(method);
        var field = (FieldReference)((AddressOf)method.ControlFlowGraph!.Instructions.Single(operation =>
            operation.Operands is [_, AddressOf { Target: FieldReference }]).Operands[1]).Target;
        var raw = origin switch
        {
            "owner" => method.DeclaringType!.Definition!.RawType,
            "aggregate" => field.Field.FieldType.Definition!.RawType,
            "field" => field.Field.BackingData!.Field.RawFieldType!,
            "return" => method.Definition!.RawReturnType!,
            "parameter" => method.Parameters[0].Definition!.RawType!,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        var data = raw.Data;
        try
        {
            raw.Data = null!;
            Assert.That(X64TypedFieldAddressProof.Find(method, true), Is.Null);
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally { raw.Data = data; }
        Accept(method);
    }

    [Test]
    public void CompleteAggregateMemberProjectionCannotBeReducedToInventUniqueAliases()
    {
        var method = _methods["ReadFirst"];
        Accept(method);
        var target = (MethodAnalysisContext)method.ControlFlowGraph!.Instructions.Single(operation => operation.IsCall).Operands[0];
        var owner = target.DeclaringType!;
        var original = owner.Methods.ToArray();
        try
        {
            owner.Methods.Remove(original.Single(member => member.Name == "Reset"));
            Assert.That(X64TypedFieldAddressProof.Find(method, true), Is.Null);
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
        }
        finally { owner.Methods.Clear(); owner.Methods.AddRange(original); }
        Accept(method);
    }

    [Test]
    public void APreviouslyBoundContextCannotDisambiguateSameOwnerNativeAliases()
    {
        var method = _methods["ReadFirst"];
        Accept(method);
        var target = (MethodAnalysisContext)method.ControlFlowGraph!.Instructions.Single(operation => operation.IsCall).Operands[0];
        var competing = target.DeclaringType!.Methods.Single(member => member.Name == "Reset");
        var pointerField = competing.Definition!.GetType().GetField("_methodPointer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pointer = pointerField.GetValue(competing.Definition);
        var aliases = method.AppContext.MethodsByAddress[target.UnderlyingPointer];
        try
        {
            pointerField.SetValue(competing.Definition, target.UnderlyingPointer);
            aliases.Add(competing);
            // Even an unchanged managed operand cannot be used as callsite
            // evidence for folded methods from the same addressed owner, even
            // when their managed return signatures differ.
            Assert.That(X64TypedFieldAddressProof.Find(method, true), Is.Null);
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
        }
        finally
        {
            aliases.Remove(competing);
            pointerField.SetValue(competing.Definition, pointer);
        }
        Accept(method);
    }

    [Test]
    public void TheCalleeMustFaultOnTheAddressedReceiverBeforeAnyOtherEffect()
    {
        var method = _methods["AdvanceFirst"];
        var target = (MethodAnalysisContext)method.ControlFlowGraph!.Instructions.Single(operation => operation.IsCall).Operands[0];
        var body = X64TypedFieldAddressProof.ReadBody(target)!;
        Assert.That(X64TypedFieldAddressProof.FirstReceiverFault(target, body, 8), Is.True);
        var changed = body.ToArray();
        changed[0].MemoryBase = NativeRegister.RDX;
        Assert.That(X64TypedFieldAddressProof.FirstReceiverFault(target, changed, 8), Is.False);
    }

    [TestCase("ResetFirst", false)]
    [TestCase("ReadFirst", false)]
    [TestCase("ResetFirst", true)]
    [TestCase("ReadFirst", true)]
    public void ResolvingRawAliasArgumentsRetainsAnAuthenticatedReplacementEntryResult(string name, bool remove)
    {
        var method = _methods[name];
        Accept(method);
        var original = method.GetExtraData<NativeEntryValueValidator.Result>(NativeEntryValueValidator.EvidenceKey)!;
        Assert.That(original.UnprovedValueCount, Is.Zero);
        Assert.That(method.ControlFlowGraph!.Instructions.SelectMany(OperandEffects.ReadLocals)
            .Any(local => local.Register.Version == -1 && !method.ParameterLocals.Contains(local)), Is.False);
        try
        {
            method.PutExtraData(NativeEntryValueValidator.EvidenceKey,
                remove ? null! : new NativeEntryValueValidator.Result(0));
            Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.False);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, Definition(method)));
        }
        finally { method.PutExtraData(NativeEntryValueValidator.EvidenceKey, original); }
        Accept(method);
    }

    private static MethodDefinition Definition(MethodAnalysisContext method)
        => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private static void Accept(MethodAnalysisContext method)
    {
        Assert.That(TypedFieldAddressRecovery.HasEvidence(method), Is.True);
        Assert.That(TypedFieldAddressRecovery.IsValidFor(method), Is.True);
        IlGenerator.GenerateIl(method, Definition(method));
    }
}
