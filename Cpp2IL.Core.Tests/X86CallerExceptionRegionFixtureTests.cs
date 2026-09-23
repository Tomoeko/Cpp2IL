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
    public void CatchOnlyControlHasCompleteNativeProof()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CATCH_DIVIDE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CATCH_DIVIDE_FIXTURE_INPUT to the exact synthetic catch-only player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .Single(type => type.FullName == "ExceptionRegionFixture.ExceptionMethods")
                .Methods.Single(candidate => candidate.Name == "CatchZero");
            var funclet = X64CatchFuncletClassProof.Find(method);
            Assert.That(funclet, Is.Not.Null);
            Assert.That(funclet!.CheckedClass.FullName, Is.EqualTo("System.DivideByZeroException"));
            Assert.That(funclet.ConstantContinuationReturn, Is.EqualTo(-17));
            Assert.That(X64CatchFuncletFlowProof.Check(method, funclet), Is.True);
            var body = X64CatchDivideBodyProof.Find(method);
            Assert.That(body, Is.Not.Null);
            Assert.That(X64ManagedThrowHelperProof.Check(method, body!), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

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
    public void ExactCatchAndFinallyRetainDistinctHandlerDataAndRejectOrdinaryLifting()
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
            var pe = (PE)app.Binary;
            Assert.That(pe.GetVirtualAddressOfImportedFunctionByName("KERNEL32.dll", "RaiseException"), Is.Not.Zero);
            Assert.That(pe.GetVirtualAddressOfImportedFunctionByName("KERNEL32.dll", "MissingImport"), Is.Zero);
            var methods = app.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .SelectMany(type => type.Methods).OrderBy(method => method.Name).ToArray();
            Assert.That(methods.Select(method => method.Name),
                Is.EqualTo(new[] { "CatchZero", "FinallyCount" }));
            var catchProof = X64CatchFuncletClassProof.Find(methods[0]);
            Assert.That(catchProof?.CheckedClass.FullName,
                Is.EqualTo("System.DivideByZeroException"));
            Assert.That(catchProof!.ConstantContinuationReturn, Is.EqualTo(-17));
            Assert.That(X64CatchFuncletFlowProof.Check(methods[0], catchProof), Is.True);
            Assert.That(X64CatchFuncletClassProof.Find(methods[1]), Is.Null,
                "A finally funclet must not be mistaken for a typed catch.");
            var catchBody = X64CatchDivideBodyProof.Find(methods[0]);
            Assert.That(catchBody?.ExceptionClass.FullName, Is.EqualTo("System.DivideByZeroException"));
            Assert.That(catchBody?.Constructor.Name, Is.EqualTo(".ctor"));
            Assert.That(catchBody?.CaughtReturn, Is.EqualTo(-17));
            Assert.That(X64ManagedThrowHelperProof.Check(methods[0], catchBody!), Is.True);
            Assert.That(X64CatchDivideBodyProof.Find(methods[1]), Is.Null);
            Assert.That(X64FinallyCountBodyProof.Find(methods[0]), Is.Null);
            var finallyBody = X64FinallyCountBodyProof.Find(methods[1]);
            Assert.That(finallyBody?.ExceptionClass.FullName, Is.EqualTo("System.DivideByZeroException"));
            Assert.That(finallyBody?.Constructor.Name, Is.EqualTo(".ctor"));
            Assert.That(finallyBody?.Dividend, Is.EqualTo(100));
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
                Assert.That(map.UnwindActions.Select(action => action.TargetState),
                    Is.EqualTo(method.Name == "CatchZero" ? new[] { -1, -1 } : new[] { -1, 0, 0 }));
                Assert.That(map.UnwindActions.Any(action => action.Kind != 0),
                    Is.EqualTo(method.Name == "FinallyCount"));
                if (method.Name == "FinallyCount")
                {
                    var actionAddress = index.ImageBase + map.UnwindActions[0].ActionRva!.Value;
                    Assert.That(X64FinallyCleanupActionProof.Check(pe, index,
                        actionAddress), Is.True);
                    var fAddress = index.ImageBase + map.TryBlocks[0].Handlers[0].FuncletRva;
                    Assert.That(X64FinallyFuncletProof.Check(pe, index,
                        fAddress,
                        map.UnwindActions[0].ObjectOffset!.Value), Is.True);
                }
                var native = X86Utils.Iterate(method).ToArray();
                if (method.Name == "FinallyCount")
                {
                    Assert.That(X64ManagedThrowHelperProof.Check(method,
                        native[29].NearBranchTarget, native[32].NearBranchTarget,
                        native[40].NearBranchTarget), Is.True);
                    Assert.That(X64FinallyRethrowProof.Check(pe, index, native[42].NearBranchTarget), Is.True);
                }
                Assert.That(X86CallerExceptionRegionProof.Check(method, native, new System.Collections.Generic.HashSet<ulong>()),
                    Does.Contain("unsupported native handlers"));
                Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { OpCode.NotImplemented }));
                return (Evidence: evidence.Value,
                    NativeTypeDescriptorRva: map.TryBlocks[0].Handlers[0].NativeTypeDescriptorRva);
            }).ToArray();
            Assert.That(handlers[0].Evidence.HandlerAddress, Is.EqualTo(handlers[1].Evidence.HandlerAddress));
            Assert.That(handlers[0].Evidence.HandlerDataAddress, Is.Not.EqualTo(handlers[1].Evidence.HandlerDataAddress));
            Assert.That(handlers[0].NativeTypeDescriptorRva, Is.Not.Null);
            Assert.That(handlers[0].NativeTypeDescriptorRva, Is.EqualTo(handlers[1].NativeTypeDescriptorRva),
                "A shared native C++ handler type does not identify a managed catch or finally clause.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
