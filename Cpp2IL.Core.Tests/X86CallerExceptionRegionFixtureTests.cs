using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional player-metadata and native-EH evidence, not a behavior acceptance gate.</summary>
[NonParallelizable]
public class X86CallerExceptionRegionFixtureTests
{
    [Test]
    public void AuthoredDriverCatchMethodsCannotPassAnOrdinaryControlFlowOnlyRecovery()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LOOP_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LOOP_CALL_FIXTURE_INPUT to the public LoopCallFixture's exact player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var index = X64UnwindProof.ForApplication(app);
            Assert.That(index, Is.Not.Null);
            var driver = app.GetAssemblyByName("RecoveryValidation.Runtime")!.Types
                .Single(type => type.FullName == "RecoveryValidation.BehaviorProbe");
            foreach (var name in new[] { "RecordNull", "RunPlayer" })
            {
                var method = driver.Methods.Single(candidate => candidate.Name == name);
                Assert.That(method.UnderlyingPointer, Is.Not.Zero, "The authored driver control must have a retained native body.");
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(native, Is.Not.Empty);
                Assert.That(index!.ClassifySpan(native[0].IP, native[0].NextIP).Kind,
                    Is.EqualTo(X64UnwindProof.SpanKind.Unsupported), "A real catch path must remain outside current EH support.");
                Assert.That(X86CallerExceptionRegionProof.Check(method, native, new System.Collections.Generic.HashSet<ulong>()),
                    Does.Contain("unsupported native handlers"));
                var lifted = app.InstructionSet.GetIsilFromMethod(method);
                Assert.That(lifted, Has.Count.EqualTo(1));
                Assert.That(lifted[0].OpCode, Is.EqualTo(OpCode.NotImplemented));
                Assert.That(lifted[0].Operands[0], Is.TypeOf<StringLiteral>());
                Assert.That(((StringLiteral)lifted[0].Operands[0]).Value, Does.Contain("unsupported native handlers"));
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactCatchAndFinallyRetainDistinctHandlerDataAndRemainUnsupported()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_EXCEPTION_REGION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_EXCEPTION_REGION_FIXTURE_INPUT to the public ExceptionRegionFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var index = X64UnwindProof.ForApplication(app)!;
            var methods = app.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .SelectMany(type => type.Methods).OrderBy(method => method.Name).ToArray();
            Assert.That(methods.Select(method => method.Name),
                Is.EqualTo(new[] { "CatchZero", "FinallyCount" }));
            var handlers = methods.Select(method =>
            {
                method.EnsureRawBytes();
                var evidence = index.GetHandler(method.UnderlyingPointer);
                Assert.That(evidence, Is.Not.Null);
                Assert.That(evidence!.Value.Flags, Is.EqualTo(3));
                Assert.That(index.MapReadOnlyData(evidence.Value.HandlerDataAddress, 1), Is.GreaterThanOrEqualTo(0));
                var map = X64Eh4MapProof.Parse(((PE)app.Binary).GetRawBinaryContent(), index, evidence.Value);
                Assert.That(map, Is.Not.Null);
                Assert.That(map!.TryBlocks, Has.Count.EqualTo(1));
                Assert.That(map.TryBlocks[0].Handlers, Has.Count.EqualTo(1));
                Assert.That(map.IpStates, Has.Count.EqualTo(2));
                Assert.That(map.UnwindActions, Has.Count.EqualTo(method.Name == "CatchZero" ? 2 : 3));
                Assert.That(map.UnwindActions.Any(action => action.Kind != 0),
                    Is.EqualTo(method.Name == "FinallyCount"));
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(X86CallerExceptionRegionProof.Check(method, native, new System.Collections.Generic.HashSet<ulong>()),
                    Does.Contain("unsupported native handlers"));
                Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { OpCode.NotImplemented }));
                return evidence.Value;
            }).ToArray();
            Assert.That(handlers[0].HandlerAddress, Is.EqualTo(handlers[1].HandlerAddress));
            Assert.That(handlers[0].HandlerDataAddress, Is.Not.EqualTo(handlers[1].HandlerDataAddress));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
