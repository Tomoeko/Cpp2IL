using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional native proof gate for signed and unsigned 16-bit array widening.</summary>
[NonParallelizable]
public class X86WordArrayAccessFixtureTests
{
    [TestCase("ReadSigned", true)]
    [TestCase("ReadUnsigned", false)]
    public void ExactWordReadProvesWideningAndCompleteNativeRegion(string name, bool signed)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_WORD_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_WORD_ARRAY_FIXTURE_INPUT to the public fixture's player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("WordArrayFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == name);
            var expectedElement = signed ? app.SystemTypes.SystemInt16Type : app.SystemTypes.SystemUInt16Type;
            var array = method.Parameters[0].ParameterType as SzArrayTypeAnalysisContext;
            Assert.Multiple(() =>
            {
                Assert.That(array?.ElementType, Is.SameAs(expectedElement));
                Assert.That(method.Parameters[1].ParameterType, Is.SameAs(app.SystemTypes.SystemInt32Type));
                Assert.That(method.ReturnType, Is.SameAs(app.SystemTypes.SystemInt32Type));
                Assert.That(method.Definition?.RawReturnType?.Type, Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_I4));
            });

            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, false, 2,
                signedWordRead: signed), Is.True);
            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, native);
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
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, region[9].NearBranchTarget), Is.Not.Null);
            Assert.That(X86RuntimeBoundsThrowProof.TryIdentify(app, region[11].NearBranchTarget), Is.True);
            Assert.That(X86CallerExceptionRegionProof.Check(method, region,
                new[] { region[9].IP, region[11].IP }.ToHashSet()), Is.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Return }));

            var oppositeOpcode = native.ToArray();
            oppositeOpcode[6].Code = signed ? Code.Movzx_r32_rm16 : Code.Movsx_r32_rm16;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(oppositeOpcode, false, 2,
                signedWordRead: signed), Is.False, "Signedness changes the returned Int32 value.");
            var wrongWidth = native.ToArray();
            wrongWidth[6].Code = signed ? Code.Movsx_r32_rm8 : Code.Movzx_r32_rm8;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongWidth, false, 2,
                signedWordRead: signed), Is.False);
            var wrongScale = native.ToArray();
            wrongScale[6].MemoryIndexScale = 4;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongScale, false, 2,
                signedWordRead: signed), Is.False);
            var wrongReturn = native.ToArray();
            wrongReturn[6].Op0Register = Register.RAX;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongReturn, false, 2,
                signedWordRead: signed), Is.False);
            var wrongBounds = native.ToArray();
            wrongBounds[4].Code = Code.Ja_rel8_64;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongBounds, false, 2,
                signedWordRead: signed), Is.False);
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, false, 2), Is.False);
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, true, 2,
                signedWordRead: signed), Is.False, "Word-array writes are outside this proof.");

            method.OverrideReturnType = app.SystemTypes.SystemUInt32Type;
            try { Assert.That(X86ScalarArrayAccessProof.TryLift(method, native), Is.Null); }
            finally { method.OverrideReturnType = null; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
