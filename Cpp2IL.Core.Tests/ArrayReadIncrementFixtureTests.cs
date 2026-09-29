using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ArrayReadIncrementFixtureTests
{
    [Test]
    public void OriginalPlayerHasOneFileBackedArrayIncrementBody()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_READ_INCREMENT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_READ_INCREMENT_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("ArrayReadIncrementFixture")!;
            var method = assembly.Types.SelectMany(type => type.Methods).Single();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, false, 4,
                increment: true), Is.True);
            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, native, 12);
            Assert.That(region, Has.Count.EqualTo(14));
            Assert.That(region![13].Code, Is.EqualTo(Code.Int3));
            Assert.That(X86ScalarArrayAccessProof.TryLift(method, native), Is.Not.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { OpCode.Move, OpCode.Add, OpCode.Return }));
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method,
                method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!));

            var wrongRegister = native.ToArray();
            wrongRegister[7].Op0Register = Iced.Intel.Register.ECX;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongRegister, false, 4,
                increment: true), Is.False);
            var wrongWidth = native.ToArray();
            wrongWidth[7].Code = Code.Inc_rm64;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongWidth, false, 4,
                increment: true), Is.False);
            var wrongBranch = native.ToArray();
            wrongBranch[4].NearBranch64 = native[10].IP;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongBranch, false, 4,
                increment: true), Is.False);
            var wrongBoundsTarget = native.ToArray();
            wrongBoundsTarget[12].NearBranch64 = native[10].NearBranchTarget;
            Assert.That(X86ScalarArrayAccessProof.TryLift(method, wrongBoundsTarget), Is.Null);
            var missingTrap = region.ToArray();
            missingTrap[13].Code = Code.Nopd;
            Assert.That(X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, missingTrap, 12), Is.Null);

            var bindings = app.MethodsByAddress[method.UnderlyingPointer];
            Assert.That(bindings, Has.Count.EqualTo(1));
            bindings.Add(method);
            try { Assert.That(X86ScalarArrayAccessProof.TryLift(method, native), Is.Null); }
            finally { bindings.RemoveAt(bindings.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
