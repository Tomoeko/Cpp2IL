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

[NonParallelizable]
public class X86FixedScalarArrayFixtureTests
{
    [Test]
    public void ConstructorHasProvedStateStore()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIXED_SCALAR_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIXED_SCALAR_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("FixedScalarArrayFixture")!.Types
                .Single(type => type.Name == "ScalarCatalog");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            var native = X86Utils.Iterate(constructor).ToArray();
            var evidence = X64FoldedInt32ConstructorProof.Find(constructor, native);
            Assert.That(evidence?.State, Is.SameAs(owner.Fields.Single(field => field.Name == "State")),
                "The constructor must have a complete proved base call and Int32 state store.");
            Assert.That(app.InstructionSet.GetIsilFromMethod(constructor)
                    .Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.CallVoid, IsilOpCode.Move, IsilOpCode.Return }));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [TestCase("ReadSecond", 1)]
    [TestCase("ReadFourth", 3)]
    public void FixedIndexGetterHasClosedArrayGuards(string name, int index)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIXED_SCALAR_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIXED_SCALAR_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("FixedScalarArrayFixture")!.Types
                .Single(type => type.Name == "ScalarCatalog").Methods
                .Single(candidate => candidate.Name == name);
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X86FieldArrayAccessProof.TryProveShape(native);
            Assert.Multiple(() =>
            {
                Assert.That(shape?.Kind, Is.EqualTo(X86FieldArrayAccessProof.AccessKind.ReadFixed));
                Assert.That(shape?.FixedIndex, Is.EqualTo(index));
                Assert.That(X64Stack28BodyProof.Read(method, 12, 96), Is.Not.Null);
            });

            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, native);
            Assert.That(region, Has.Count.EqualTo(13));
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                region![9].NearBranchTarget), Is.Not.Null);
            Assert.That(X86RuntimeBoundsThrowProof.TryIdentify(app,
                region[11].NearBranchTarget), Is.True);
            Assert.That(X86CallerExceptionRegionProof.Check(method, region,
                new[] { region[9].IP, region[11].IP }.ToHashSet()), Is.Null);

            var evidence = X86FieldArrayAccessProof.Find(method, native);
            Assert.That(evidence?.Shape.FixedIndex, Is.EqualTo(index));
            Assert.That(app.InstructionSet.GetIsilFromMethod(method)
                    .Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Return }));
            method.Analyze();
            Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction =>
                instruction is { OpCode: IsilOpCode.Move, Operands:
                    [_, Cpp2IL.Core.ISIL.ArrayAccess { Index:
                        Cpp2IL.Core.ISIL.Immediate { Value: var value } }] } && value == index),
                Is.True);

            var wrongIndex = native.ToArray();
            wrongIndex[4].Immediate8 = (byte)(index + 1);
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongIndex), Is.Null);
            var wrongElement = native.ToArray();
            wrongElement[6].MemoryDisplacement64 += 4;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongElement), Is.Null);
            var wrongBoundsBranch = native.ToArray();
            wrongBoundsBranch[5].Code = Code.Ja_rel8_64;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongBoundsBranch), Is.Null);
            var differentCompareEncoding = native.ToArray();
            differentCompareEncoding[4].Code = Code.Cmp_rm32_imm32;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(differentCompareEncoding), Is.Null);

            var field = evidence!.Field;
            try
            {
                field.OverrideFieldType = new SzArrayTypeAnalysisContext(app.SystemTypes.SystemInt64Type);
                Assert.That(X86FieldArrayAccessProof.Find(method, native), Is.Null,
                    "An Int64 array cannot use the proved four-byte element load.");
            }
            finally { field.OverrideFieldType = null; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
