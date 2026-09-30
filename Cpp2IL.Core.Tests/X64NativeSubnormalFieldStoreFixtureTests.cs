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
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeSubnormalFieldStoreFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SUBNORMAL_FIELD_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_SUBNORMAL_FIELD_STORE_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeSubnormalFieldStoreFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Store", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(8));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("StoreSingleMinimum", 32, "00000001")]
    [TestCase("StoreSingleMaximum", 32, "007FFFFF")]
    [TestCase("StoreSingleNegativeMinimum", 32, "80000001")]
    [TestCase("StoreSingleNegativeMaximum", 32, "807FFFFF")]
    [TestCase("StoreDoubleMinimum", 64, "0000000000000001")]
    [TestCase("StoreDoubleMaximumAndSingleMinimum", 64, "000FFFFFFFFFFFFF")]
    [TestCase("StoreDoubleNegativeMinimum", 64, "8000000000000001")]
    [TestCase("StoreDoubleNegativeMaximum", 64, "800FFFFFFFFFFFFF")]
    public void NativeSubnormalStoresRetainWidthBitsOrderAndValidManagedLiteralTypes(string name, int width, string encoded)
    {
        var caller = Caller(name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, name);
        var stores = Stores(caller);
        var expected = name == "StoreDoubleMaximumAndSingleMinimum"
            ? new (int Width, ulong Bits)[] { (32, 0x80000001), (64, Convert.ToUInt64(encoded, 16)) }
            : [(width, Convert.ToUInt64(encoded, 16))];
        Assert.That(stores, Has.Length.EqualTo(expected.Length));
        for (var index = 0; index < stores.Length; index++)
        {
            AssertLiteral(stores[index], expected[index].Width, expected[index].Bits);
            Assert.That(stores[index].NativeAddress.HasValue, Is.True);
            if (index > 0) Assert.That(stores[index].NativeAddress!.Value, Is.GreaterThan(stores[index - 1].NativeAddress!.Value));
        }
        var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
        var code = definition.CilMethodBody!.Instructions;
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
        Assert.That(code.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(stores.Length));
        var literals = code.Where(instruction => instruction.OpCode == CilOpCodes.Ldc_R4 ||
            instruction.OpCode == CilOpCodes.Ldc_R8).ToArray();
        Assert.That(literals, Has.Length.EqualTo(expected.Length));
        for (var index = 0; index < literals.Length; index++)
        {
            var actual = literals[index].Operand is float single
                ? unchecked((uint)BitConverter.SingleToInt32Bits(single))
                : unchecked((ulong)BitConverter.DoubleToInt64Bits((double)literals[index].Operand!));
            Assert.That(actual, Is.EqualTo(expected[index].Bits));
            Assert.That(literals[index].OpCode, Is.EqualTo(expected[index].Width == 32 ? CilOpCodes.Ldc_R4 : CilOpCodes.Ldc_R8));
        }

        var typed = stores.Select(store => store.Operands[1]).ToArray();
        try
        {
            MakeRaw(stores);
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False,
                "Integer ldc/stfld floating storage cannot be published before normalization.");
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(caller, definition));
            FloatLiteralRecovery.Run(caller);
            for (var index = 0; index < stores.Length; index++)
                AssertLiteral(stores[index], expected[index].Width, expected[index].Bits);
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
        }
        finally
        {
            for (var index = 0; index < stores.Length; index++) stores[index].SetOperand(1, typed[index]);
        }
    }

    [TestCase("StoreSingleMinimum")]
    [TestCase("StoreSingleNegativeMaximum")]
    [TestCase("StoreDoubleNegativeMinimum")]
    [TestCase("StoreDoubleMaximumAndSingleMinimum")]
    public void ChangedNativeFieldAndSiteEffectsRejectNormalizationWithoutLeakingTypedOperands(string name)
    {
        var caller = Caller(name);
        foreach (var mutation in new[] { "literal-bits", "literal-width", "store-address", "store-width", "field-offset", "field-layout", "field-type",
                     "receiver", "call-argument", "call-semantics", "guard-target", "effect-order", "native-bytes",
                     "missing-sites", "unrecorded-store", "unsupported-extra-store" })
            RejectMutation(caller, mutation);
        if (name == "StoreDoubleMaximumAndSingleMinimum") RejectMutation(caller, "store-order");
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    private static void RejectMutation(MethodAnalysisContext caller, string mutation)
    {
        var graph = caller.ControlFlowGraph!;
        var stores = Stores(caller);
        var typed = stores.Select(store => store.Operands[1]).ToArray();
        var store = stores[0];
        var access = (FieldReference)store.Operands[0];
        var call = graph.Instructions.Single(instruction => instruction.IsCall);
        var undo = new List<Action>();
        try
        {
            MakeRaw(stores);
            var block = graph.FindBlockByInstruction(store)!;
            switch (mutation)
            {
                case "literal-bits":
                    var literal = (Immediate)store.Operands[1];
                    store.SetOperand(1, new Immediate(literal.Value ^ 2));
                    break;
                case "literal-width":
                    store.SetOperand(1, new Immediate(0x1_0000_0001));
                    break;
                case "store-address":
                    var address = store.NativeAddress;
                    undo.Add(() => store.NativeAddress = address);
                    store.NativeAddress++;
                    break;
                case "store-width":
                    undo.Add(() => store.IntegerBitWidth = 0);
                    store.IntegerBitWidth = 32;
                    break;
                case "field-offset":
                    var offset = access.Offset;
                    undo.Add(() => access.Offset = offset);
                    access.Offset++;
                    break;
                case "field-layout":
                    var layout = access.Field.OverrideOffset;
                    undo.Add(() => access.Field.OverrideOffset = layout);
                    access.Field.OverrideOffset = access.Field.DefaultOffset + 1;
                    break;
                case "field-type":
                    var fieldType = access.Field.OverrideFieldType;
                    undo.Add(() => access.Field.OverrideFieldType = fieldType);
                    access.Field.OverrideFieldType = caller.AppContext.SystemTypes.SystemInt64Type;
                    break;
                case "receiver":
                    var owner = access.Local;
                    undo.Add(() => access.Local = owner);
                    access.Local = (LocalVariable)call.Operands[call.OpCode == OpCode.Call ? 2 : 1];
                    break;
                case "call-argument":
                    var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
                    var argument = call.Operands[argumentIndex];
                    undo.Add(() => call.SetOperand(argumentIndex, argument));
                    call.SetOperand(argumentIndex, new Immediate(0));
                    break;
                case "call-semantics":
                    var semantics = call.CallSemantics;
                    undo.Add(() => call.CallSemantics = semantics);
                    call.CallSemantics = CallSemantics.Direct;
                    break;
                case "guard-target":
                    var guard = graph.Instructions.First(instruction => instruction.OpCode == OpCode.Jump);
                    var destination = guard.Operands[0];
                    undo.Add(() => guard.SetOperand(0, destination));
                    guard.SetOperand(0, graph.EntryBlock);
                    break;
                case "effect-order":
                    var position = block.Instructions.IndexOf(store);
                    var callBlock = graph.FindBlockByInstruction(call)!;
                    block.Instructions.Remove(store);
                    callBlock.Instructions.Insert(callBlock.Instructions.IndexOf(call), store);
                    undo.Add(() => { callBlock.Instructions.Remove(store); block.Instructions.Insert(position, store); });
                    break;
                case "store-order":
                    var firstPosition = block.Instructions.IndexOf(stores[0]);
                    var secondPosition = block.Instructions.IndexOf(stores[1]);
                    (block.Instructions[firstPosition], block.Instructions[secondPosition]) =
                        (block.Instructions[secondPosition], block.Instructions[firstPosition]);
                    undo.Add(() => { block.Instructions[firstPosition] = stores[0]; block.Instructions[secondPosition] = stores[1]; });
                    break;
                case "native-bytes":
                    var bytes = caller.RawBytes;
                    undo.Add(() => caller.RawBytes = bytes);
                    var changed = bytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    caller.RawBytes = new BinarySlice(changed);
                    break;
                case "missing-sites":
                    var evidence = caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey)!;
                    undo.Add(() => caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, evidence));
                    caller.PutExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey, null!);
                    Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True);
                    break;
                case "unrecorded-store":
                    var replacedPosition = block.Instructions.IndexOf(store);
                    var replacement = new Instruction(-1, OpCode.Move, access, store.Operands[1]) { NativeAddress = store.NativeAddress };
                    block.Instructions[replacedPosition] = replacement;
                    undo.Add(() => block.Instructions[replacedPosition] = store);
                    break;
                case "unsupported-extra-store":
                    var extra = new Instruction(-1, OpCode.Move, access, new Immediate(0)) { NativeAddress = store.NativeAddress };
                    block.Instructions.Insert(block.Instructions.IndexOf(store) + 1, extra);
                    undo.Add(() => block.Instructions.Remove(extra));
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            var raw = Stores(caller).Where(operation => operation.Operands[1] is Immediate)
                .Select(operation => (Operation: operation, Operand: operation.Operands[1])).ToArray();
            Assert.That(X64NativeNullCheckedInvocationProof.TryNormalizeSubnormalFloatingStores(caller), Is.False, mutation);
            foreach (var captured in raw)
                Assert.That(captured.Operation.Operands[1], Is.SameAs(captured.Operand), mutation + " must roll back every operand.");
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(caller,
                caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!), mutation);
        }
        finally
        {
            for (var index = undo.Count - 1; index >= 0; index--) undo[index]();
            for (var index = 0; index < stores.Length; index++) stores[index].SetOperand(1, typed[index]);
        }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, mutation + " restoration");
    }

    private static Instruction[] Stores(MethodAnalysisContext caller) => caller.ControlFlowGraph!.Instructions
        .Where(instruction => instruction is { OpCode: OpCode.Move, Operands: [FieldReference access, _] } &&
            access.Field.Name is "SingleMarker" or "DoubleMarker").ToArray();

    private static void MakeRaw(Instruction[] stores)
    {
        foreach (var store in stores)
        {
            var width = ((FieldReference)store.Operands[0]).Field.Name == "SingleMarker" ? 32 : 64;
            Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(store.Operands[1], width, out var bits), Is.True);
            store.SetOperand(1, new Immediate(unchecked((long)bits)));
        }
    }

    private static void AssertLiteral(Instruction store, int width, ulong expected)
    {
        Assert.That(store.Operands[1], width == 32 ? Is.TypeOf<FloatLiteral>() : Is.TypeOf<DoubleLiteral>());
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(store.Operands[1], width, out var bits), Is.True);
        Assert.That(bits, Is.EqualTo(expected));
    }

    private MethodAnalysisContext Caller(string name) => _callers.Single(method => method.Name == name);
}
