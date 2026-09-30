using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarDoubleAccumulatorFixtureTests
{
    private MethodAnalysisContext _method = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_DOUBLE_ACCUMULATOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_DOUBLE_ACCUMULATOR_FIXTURE_INPUT to the neutral exact player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _method = app.GetAssemblyByName("ScalarDoubleAccumulatorFixture")!.Types
            .Single(type => type.Name == "DoubleAccumulator").Methods.Single(method => method.Name == "Apply");
        _method.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void CompleteNativeUpdateRetainsItsGuardSeparateRoundingOperandOrderAndFieldEffects()
    {
        Accept();
        var graph = _method.ControlFlowGraph!;
        var subtraction = Arithmetic(OpCode.FloatSubtract);
        var addition = Arithmetic(OpCode.FloatAdd);
        var update = graph.FindBlockByInstruction(subtraction)!;
        var current = Capture("Current");
        var baseline = Capture("Baseline");
        var total = Capture("Total");
        Assert.That(subtraction.Operands[1], Is.SameAs(current.Destination));
        Assert.That(subtraction.Operands[2], Is.SameAs(baseline.Destination));
        Assert.That(addition.Operands[1], Is.SameAs(subtraction.Destination),
            "Native ADDSD keeps delta as its left operand, including multiple-NaN operand priority.");
        Assert.That(addition.Operands[2], Is.SameAs(total.Destination));
        Assert.That(update.Instructions.IndexOf(Store("Pending")), Is.GreaterThan(update.Instructions.IndexOf(subtraction)));
        Assert.That(update.Instructions.IndexOf(Store("Pending")), Is.LessThan(update.Instructions.IndexOf(total)));
        Assert.That(Store("Total").Operands[1], Is.SameAs(addition.Destination));
        Assert.That(Capture("Pending").IntegerBitWidth, Is.EqualTo(8));
        var code = Definition.CilMethodBody!.Instructions;
        foreach (var operation in new[] { CilOpCodes.Sub, CilOpCodes.Add })
        {
            var position = code.ToList().FindIndex(instruction => instruction.OpCode == operation);
            Assert.That(position, Is.GreaterThanOrEqualTo(0));
            Assert.That(code[position + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_R8));
        }
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(4));
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(2));
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Ret), Is.EqualTo(1));
    }

    [TestCase("subtraction-order")]
    [TestCase("addition-order")]
    [TestCase("subtraction-opcode")]
    [TestCase("addition-precision")]
    [TestCase("arithmetic-result-type")]
    [TestCase("arithmetic-integer-width")]
    [TestCase("arithmetic-address")]
    [TestCase("guard-comparison")]
    [TestCase("guard-literal")]
    [TestCase("guard-target")]
    [TestCase("capture-owner")]
    [TestCase("capture-type")]
    [TestCase("reset-value")]
    [TestCase("reset-order")]
    [TestCase("total-field")]
    [TestCase("stored-value")]
    [TestCase("field-layout")]
    [TestCase("field-type")]
    [TestCase("raw-field-identity")]
    [TestCase("owner-raw-flags")]
    [TestCase("native-bytes")]
    [TestCase("missing-evidence")]
    [TestCase("extra-effect")]
    [TestCase("entry-effect")]
    [TestCase("exit-effect")]
    [TestCase("guard-update-order")]
    [TestCase("guard-return-order")]
    [TestCase("update-successor")]
    public void FinalNativeAndManagedProofRejectsChangedPrecisionDataFlowEffectsAndControl(string mutation)
    {
        Accept();
        var graph = _method.ControlFlowGraph!;
        var subtraction = Arithmetic(OpCode.FloatSubtract);
        var addition = Arithmetic(OpCode.FloatAdd);
        var comparison = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.CheckEqual);
        var branch = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.ConditionalJump);
        var current = Capture("Current");
        var currentAccess = (FieldReference)current.Operands[1];
        var reset = Store("Pending");
        var store = Store("Total");
        var guard = graph.FindBlockByInstruction(comparison)!;
        var update = graph.FindBlockByInstruction(subtraction)!;
        var returned = graph.FindBlockByInstruction(graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return))!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "subtraction-order": SwapOperands(subtraction); break;
                case "addition-order": SwapOperands(addition); break;
                case "subtraction-opcode":
                    undo.Add(() => subtraction.OpCode = OpCode.FloatSubtract);
                    subtraction.OpCode = OpCode.FloatAdd;
                    break;
                case "addition-precision": Replace(addition, 3, new Immediate(32)); break;
                case "arithmetic-result-type":
                    var result = (LocalVariable)addition.Destination!;
                    var type = result.Type;
                    undo.Add(() => result.Type = type);
                    result.Type = _method.AppContext.SystemTypes.SystemSingleType;
                    break;
                case "arithmetic-integer-width":
                    undo.Add(() => subtraction.IntegerBitWidth = 0);
                    subtraction.IntegerBitWidth = 64;
                    break;
                case "arithmetic-address":
                    var address = addition.NativeAddress;
                    undo.Add(() => addition.NativeAddress = address);
                    addition.NativeAddress++;
                    break;
                case "guard-comparison":
                    undo.Add(() => comparison.OpCode = OpCode.CheckEqual);
                    comparison.OpCode = OpCode.CheckNotEqual;
                    break;
                case "guard-literal": Replace(comparison, 2, new Immediate(1)); break;
                case "guard-target": Replace(branch, 0, update); break;
                case "capture-owner":
                    var owner = currentAccess.Local;
                    undo.Add(() => currentAccess.Local = owner);
                    currentAccess.Local = new LocalVariable("other-owner", new Register(null, "rdx"), owner.Type);
                    break;
                case "capture-type":
                    var captured = (LocalVariable)current.Destination!;
                    var capturedType = captured.Type;
                    undo.Add(() => captured.Type = capturedType);
                    captured.Type = _method.AppContext.SystemTypes.SystemInt64Type;
                    break;
                case "reset-value": Replace(reset, 1, new Immediate(1)); break;
                case "reset-order":
                    var resetPosition = update.Instructions.IndexOf(reset);
                    update.Instructions.Remove(reset);
                    update.Instructions.Add(reset);
                    undo.Add(() => { update.Instructions.Remove(reset); update.Instructions.Insert(resetPosition, reset); });
                    break;
                case "total-field": Replace(store, 0, currentAccess); break;
                case "stored-value": Replace(store, 1, subtraction.Destination!); break;
                case "field-layout":
                    var layout = currentAccess.Field.OverrideOffset;
                    undo.Add(() => currentAccess.Field.OverrideOffset = layout);
                    currentAccess.Field.OverrideOffset = currentAccess.Field.DefaultOffset + 8;
                    break;
                case "field-type":
                    var fieldType = currentAccess.Field.OverrideFieldType;
                    undo.Add(() => currentAccess.Field.OverrideFieldType = fieldType);
                    currentAccess.Field.OverrideFieldType = _method.AppContext.SystemTypes.SystemInt64Type;
                    break;
                case "raw-field-identity":
                    var raw = currentAccess.Field.BackingData!.Field;
                    var name = raw.nameIndex;
                    undo.Add(() => raw.nameIndex = name);
                    raw.nameIndex++;
                    break;
                case "owner-raw-flags":
                    var definition = _method.DeclaringType!.Definition!;
                    var flags = definition.Flags;
                    undo.Add(() => definition.Flags = flags);
                    definition.Flags ^= (uint)TypeAttributes.Sealed;
                    break;
                case "native-bytes":
                    var bytes = _method.RawBytes;
                    undo.Add(() => _method.RawBytes = bytes);
                    var changed = bytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    _method.RawBytes = new BinarySlice(changed);
                    break;
                case "missing-evidence":
                    var evidence = _method.GetExtraData<object>(X64ScalarDoubleAccumulatorProof.EvidenceKey)!;
                    undo.Add(() => _method.PutExtraData(X64ScalarDoubleAccumulatorProof.EvidenceKey, evidence));
                    _method.PutExtraData<object>(X64ScalarDoubleAccumulatorProof.EvidenceKey, null!);
                    Assert.That(X64ScalarDoubleAccumulatorProof.HasEvidence(_method), Is.True);
                    break;
                case "extra-effect":
                    var extra = new Instruction(-1, OpCode.Move, store.Operands[0], store.Operands[1]);
                    update.Instructions.Add(extra);
                    undo.Add(() => update.Instructions.Remove(extra));
                    break;
                case "entry-effect":
                case "exit-effect":
                    var position = update.Instructions.IndexOf(current);
                    var sentinel = mutation == "entry-effect" ? graph.EntryBlock : graph.ExitBlock;
                    update.Instructions.Remove(current);
                    sentinel.Instructions.Add(current);
                    undo.Add(() => { sentinel.Instructions.Remove(current); update.Instructions.Insert(position, current); });
                    break;
                case "guard-update-order": SwapBlocks(guard, update); break;
                case "guard-return-order": SwapBlocks(guard, returned); break;
                case "update-successor":
                    var successor = update.Successors.Single();
                    update.Successors[0] = guard;
                    undo.Add(() => update.Successors[0] = successor);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64ScalarDoubleAccumulatorProof.IsValidFor(_method), Is.False, mutation);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(_method, Definition), mutation);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept();
        return;

        void Replace(Instruction instruction, int index, IOperand replacement)
        {
            var original = instruction.Operands[index];
            undo.Add(() => instruction.SetOperand(index, original));
            instruction.SetOperand(index, replacement);
        }

        void SwapOperands(Instruction instruction)
        {
            var left = instruction.Operands[1];
            var right = instruction.Operands[2];
            undo.Add(() => { instruction.SetOperand(1, left); instruction.SetOperand(2, right); });
            instruction.SetOperand(1, right);
            instruction.SetOperand(2, left);
        }

        void SwapBlocks(Cpp2IL.Core.Graphs.Block first, Cpp2IL.Core.Graphs.Block second)
        {
            var firstPosition = graph.Blocks.IndexOf(first);
            var secondPosition = graph.Blocks.IndexOf(second);
            undo.Add(() => { graph.Blocks[firstPosition] = first; graph.Blocks[secondPosition] = second; });
            graph.Blocks[firstPosition] = second;
            graph.Blocks[secondPosition] = first;
        }
    }

    private MethodDefinition Definition => _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
    private Instruction Arithmetic(OpCode operation) => _method.ControlFlowGraph!.Instructions.Single(instruction => instruction.OpCode == operation);
    private Instruction Capture(string field) => _method.ControlFlowGraph!.Instructions.Single(instruction => instruction is
        { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference access] } && access.Field.Name == field);
    private Instruction Store(string field) => _method.ControlFlowGraph!.Instructions.Single(instruction => instruction is
        { OpCode: OpCode.Move, Operands: [FieldReference access, _] } && access.Field.Name == field);

    private void Accept()
    {
        Assert.That(X64ScalarDoubleAccumulatorProof.HasEvidence(_method), Is.True);
        Assert.That(X64ScalarDoubleAccumulatorProof.IsValidFor(_method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_method, Definition));
    }
}
