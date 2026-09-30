using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeScalarPairInvocationFixtureTests
{
    private MethodAnalysisContext[] _callers = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_PAIR_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_SCALAR_PAIR_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _callers = app.GetAssemblyByName("NativeScalarPairInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods
            .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal)).ToArray();
        Assert.That(_callers, Has.Length.EqualTo(8));
        foreach (var method in _callers) method.Analyze();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("SetProducedPair")]
    [TestCase("SetProducedReverse")]
    [TestCase("SetProducedIntegers")]
    [TestCase("SetProducedFlags")]
    [TestCase("SetSnapshotPair")]
    [TestCase("SetSnapshotReverse")]
    [TestCase("SetReplacedSnapshot")]
    [TestCase("SetSnapshotLiterals")]
    public void BothTypedArgumentsRetainNativeIdentityAfterGraphRewrites(string name)
    {
        var method = _callers.Single(candidate => candidate.Name == name);
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(method), Is.True, name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True, name);
        var call = method.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall &&
            instruction.Operands[0] is MethodAnalysisContext { Parameters.Count: 2 });
        var target = (MethodAnalysisContext)call.Operands[0];
        var argumentIndex = call.OpCode == OpCode.Call ? 3 : 2;
        var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
        Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt),
            Is.EqualTo(1));
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True);
        for (var index = 0; index < 2; index++)
        {
            var operand = call.Operands[argumentIndex + index];
            // This changes native provenance even when the new literal is legal for the target type.
            call.SetOperand(argumentIndex + index, operand is Immediate { Value: 0 } ? new Immediate(1) : new Immediate(0));
            try { Reject(method); }
            finally { call.SetOperand(argumentIndex + index, operand); }
            var type = target.Parameters[index].ParameterType;
            target.Parameters[index].OverrideParameterType = ReferenceEquals(type, method.AppContext.SystemTypes.SystemBooleanType)
                ? method.AppContext.SystemTypes.SystemInt32Type : method.AppContext.SystemTypes.SystemBooleanType;
            try { Reject(method); }
            finally { target.Parameters[index].OverrideParameterType = null; }
        }
        var first = call.Operands[argumentIndex];
        var second = call.Operands[argumentIndex + 1];
        call.SetOperand(argumentIndex, second);
        call.SetOperand(argumentIndex + 1, first);
        try { Reject(method); }
        finally { call.SetOperand(argumentIndex, first); call.SetOperand(argumentIndex + 1, second); }
        call.CallSemantics = CallSemantics.Direct;
        try { Reject(method); }
        finally { call.CallSemantics = CallSemantics.NullCheckedInstance; }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
    }

    [Test]
    public void ReplacedSnapshotAuthenticatesMultipleNativeHomeSaves()
    {
        var method = _callers.Single(candidate => candidate.Name == "SetReplacedSnapshot");
        var body = X64NativeInstructionReader.ReadRootBody(method)!;
        var homes = body.TakeWhile(instruction => instruction.Code == Code.Mov_rm64_r64 &&
            instruction.MemoryBase == NativeRegister.RSP && instruction.MemoryIndex == NativeRegister.None).Count();
        Assert.That(homes, Is.GreaterThanOrEqualTo(2), "This exact fixture must exercise the new multi-home native frame.");
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.True);
    }

    private static void Reject(MethodAnalysisContext method)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(method), Is.False, method.Name);
        Assert.That(() => IlGenerator.GenerateIl(method, method.GetExtraData<MethodDefinition>("AsmResolverMethod")!),
            Throws.TypeOf<DecompilerException>());
    }
}
