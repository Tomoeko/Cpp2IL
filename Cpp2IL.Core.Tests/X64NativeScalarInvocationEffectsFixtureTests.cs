using System;
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
public class X64NativeScalarInvocationEffectsFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_INVOCATION_EFFECTS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_SCALAR_INVOCATION_EFFECTS_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeScalarInvocationEffectsFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(2));
        foreach (var caller in _callers) caller.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("SetThenWrite", 1, 0x3F000000u)]
    [TestCase("SetWriteThenFlush", 2, 0x80000000u)]
    public void CheckedCallsAndTypedLiteralStoresRetainAllNativeEffects(string name, int callCount, uint expectedBits)
    {
        var caller = _callers.Single(method => method.Name == name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, name);
        var graph = caller.ControlFlowGraph!;
        var calls = graph.Instructions.Where(instruction => instruction.IsCall).ToArray();
        Assert.That(calls, Has.Length.EqualTo(callCount));
        Assert.That(calls.All(call => call.CallSemantics == CallSemantics.NullCheckedInstance), Is.True);
        var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
        Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt),
            Is.EqualTo(callCount));
        var store = graph.Instructions.Single(instruction => instruction is
            { OpCode: OpCode.Move, Operands: [FieldReference access, FloatLiteral] } && access.Field.Name == "Marker");
        Assert.That(X64NativeNullCheckedInvocationProof.TryFloatingLiteralBits(store.Operands[1], 32, out var bits), Is.True);
        Assert.That(bits, Is.EqualTo(expectedBits));
        var literal = store.Operands[1];
        // Exact native bits alone cannot make ldc.i4/stfld Single valid IL.
        // This also protects subnormal immediates left unconverted by the
        // current FloatLiteralRecovery pass.
        store.SetOperand(1, new Immediate(expectedBits));
        try { Reject(caller); }
        finally { store.SetOperand(1, literal); }
        foreach (var changed in new uint[] { 0, 0x80000000, 0x3F800000, 0x7FC00001, 0x7FC00002 }.Where(value => value != expectedBits))
        {
            store.SetOperand(1, new FloatLiteral(BitConverter.Int32BitsToSingle(unchecked((int)changed))));
            try { Reject(caller); }
            finally { store.SetOperand(1, literal); }
        }
        store.SetOperand(1, new DoubleLiteral(0));
        try { Reject(caller); }
        finally { store.SetOperand(1, literal); }
        var access = (FieldReference)store.Operands[0];
        var fieldType = access.Field.OverrideFieldType;
        access.Field.OverrideFieldType = caller.AppContext.SystemTypes.SystemDoubleType;
        try { Reject(caller); }
        finally { access.Field.OverrideFieldType = fieldType; }
        var nativeAddress = store.NativeAddress;
        store.NativeAddress++;
        try { Reject(caller); }
        finally { store.NativeAddress = nativeAddress; }
        var width = store.IntegerBitWidth;
        store.IntegerBitWidth = 64;
        try { Reject(caller); }
        finally { store.IntegerBitWidth = width; }

        foreach (var call in calls)
        {
            var semantics = call.CallSemantics;
            call.CallSemantics = CallSemantics.Direct;
            try { Reject(caller); }
            finally { call.CallSemantics = semantics; }
            var target = (MethodAnalysisContext)call.Operands[0];
            call.SetOperand(0, target.DeclaringType!.Methods.Single(method => method.Name == (target.Name == "Set" ? "Flush" : "Set")));
            try { Reject(caller); }
            finally { call.SetOperand(0, target); }
        }
        foreach (var bridge in graph.Instructions.Where(instruction => instruction.OpCode == OpCode.Jump).ToArray())
        {
            var target = bridge.Operands[0];
            bridge.SetOperand(0, graph.FindBlockByInstruction(bridge)!);
            try { Reject(caller); }
            finally { bridge.SetOperand(0, target); }
        }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, definition));
    }

    private static void Reject(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False, caller.Name);
        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(caller,
            caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!));
    }
}
