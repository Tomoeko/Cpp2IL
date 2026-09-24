using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional native proof gate using the exact-target synthetic float-array player.</summary>
[NonParallelizable]
public class X86FloatArrayAccessFixtureTests
{
    [TestCase("Read", false)]
    [TestCase("Write", true)]
    public void ExactSingleAccessProvesScalarMoveAndFullUnwindRegion(string name, bool isWrite)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FLOAT_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FLOAT_ARRAY_FIXTURE_INPUT to the public fixture's player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("FloatArrayFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == name);
            var array = method.Parameters[0].ParameterType as SzArrayTypeAnalysisContext;
            Assert.That(array?.ElementType, Is.SameAs(app.SystemTypes.SystemSingleType));
            Assert.That(method.Parameters[1].ParameterType, Is.SameAs(app.SystemTypes.SystemInt32Type));
            if (isWrite)
                Assert.That(method.Parameters[2].ParameterType, Is.SameAs(app.SystemTypes.SystemSingleType));
            else
                Assert.That(method.ReturnType, Is.SameAs(app.SystemTypes.SystemSingleType));

            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, isWrite, 4, true), Is.True);
            var region = X86ScalarArrayAccessProof.TryCompleteSingleRegion(app, method.UnderlyingPointer, native);
            Assert.That(region, Is.Not.Null);
            Assert.That(region!, Has.Count.EqualTo(13));
            Assert.That(region![12].Code, Is.EqualTo(Code.Int3));
            var unwind = X64UnwindProof.ForApplication(app)!.ClassifySpan(method.UnderlyingPointer,
                region[12].NextIP);
            Assert.Multiple(() =>
            {
                Assert.That(unwind.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(unwind.Start, Is.EqualTo(method.UnderlyingPointer));
                Assert.That(unwind.RootStart, Is.EqualTo(method.UnderlyingPointer));
                Assert.That(unwind.End, Is.EqualTo(region[12].NextIP));
            });
            var calls = new[] { region[9], region[11] };
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, calls[0].NearBranchTarget), Is.Not.Null);
            Assert.That(X86RuntimeBoundsThrowProof.TryIdentify(app, calls[1].NearBranchTarget), Is.True);
            Assert.That(X86CallerExceptionRegionProof.Check(method, region,
                calls.Select(call => call.IP).ToHashSet()), Is.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Return }));

            var wrongBounds = native.ToArray();
            wrongBounds[4].Code = Code.Ja_rel8_64;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongBounds, isWrite, 4, true), Is.False);
            var wrongOffset = native.ToArray();
            wrongOffset[6].MemoryDisplacement64 = 0x24;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongOffset, isWrite, 4, true), Is.False);
            var wrongScalarMove = native.ToArray();
            wrongScalarMove[6].Code = isWrite ? Code.Movsd_xmmm64_xmm : Code.Movsd_xmm_xmmm64;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongScalarMove, isWrite, 4, true), Is.False);
            var wrongRegister = native.ToArray();
            if (isWrite)
                wrongRegister[6].Op1Register = Register.XMM3;
            else
                wrongRegister[6].Op0Register = Register.XMM1;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongRegister, isWrite, 4, true), Is.False);
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, isWrite, 4), Is.False,
                "A scalar floating move is not an integer array access.");

            var conflictingSuffix = native.Take(12).ToList();
            var overlap = native[11];
            overlap.IP = native[11].NextIP;
            overlap.Code = Code.Nopd;
            conflictingSuffix.Add(overlap);
            Assert.That(X86ScalarArrayAccessProof.TryCompleteSingleRegion(app, method.UnderlyingPointer,
                conflictingSuffix), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
