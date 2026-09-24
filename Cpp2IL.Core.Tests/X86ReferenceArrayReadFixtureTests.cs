using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player gate for reference-element array reads.</summary>
[NonParallelizable]
public class X86ReferenceArrayReadFixtureTests
{
    [TestCase("ReadObject", Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)]
    [TestCase("ReadString", Il2CppTypeEnum.IL2CPP_TYPE_STRING)]
    [TestCase("ReadClass", Il2CppTypeEnum.IL2CPP_TYPE_CLASS)]
    public void ExactReadBindsReferenceMetadataAndCompleteNativeRegion(string name,
        Il2CppTypeEnum rawType)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_ARRAY_FIXTURE_INPUT to the fixture's player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ReferenceArrayFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == name);
            var array = method.Parameters[0].ParameterType as SzArrayTypeAnalysisContext;
            Assert.Multiple(() =>
            {
                Assert.That(array, Is.Not.Null);
                Assert.That(array!.ElementType, Is.SameAs(method.ReturnType));
                Assert.That(method.ReturnType.Type, Is.EqualTo(rawType));
                Assert.That(method.Definition?.RawReturnType?.Type, Is.EqualTo(rawType));
                Assert.That(method.Parameters[1].ParameterType, Is.SameAs(app.SystemTypes.SystemInt32Type));
            });

            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, false, 8), Is.True);
            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, native);
            Assert.That(region, Has.Count.EqualTo(13));
            Assert.That(region![^1].Code, Is.EqualTo(Code.Int3));
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, region[9].NearBranchTarget), Is.Not.Null);
            Assert.That(X86RuntimeBoundsThrowProof.TryIdentify(app, region[11].NearBranchTarget), Is.True);
            Assert.That(X86CallerExceptionRegionProof.Check(method, region,
                new[] { region[9].IP, region[11].IP }.ToHashSet()), Is.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Return }));
            method.Analyze();
            Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction =>
                instruction.OpCode == IsilOpCode.Move && instruction.Operands.Count > 1 &&
                instruction.Operands[1] is Cpp2IL.Core.ISIL.ArrayAccess access &&
                ReferenceEquals(((SzArrayTypeAnalysisContext)access.Array.Type!).ElementType,
                    method.ReturnType)), Is.True);

            var wrongWidth = native.ToArray();
            wrongWidth[6].Code = Code.Mov_r32_rm32;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongWidth, false, 8), Is.False);
            var wrongScale = native.ToArray();
            wrongScale[6].MemoryIndexScale = 4;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongScale, false, 8), Is.False);
            var wrongBounds = native.ToArray();
            wrongBounds[4].Code = Code.Ja_rel8_64;
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(wrongBounds, false, 8), Is.False);
            Assert.That(X86ScalarArrayAccessProof.TryProveShape(native, true, 8), Is.False,
                "A reference write needs covariance and write-barrier evidence.");

            var originalReturn = method.OverrideReturnType;
            try
            {
                method.OverrideReturnType = app.SystemTypes.SystemInt64Type;
                Assert.That(X86ScalarArrayAccessProof.TryLift(method, native), Is.Null,
                    "An eight-byte load cannot override the declared reference result.");
            }
            finally { method.OverrideReturnType = originalReturn; }

            var optionType = new InjectedTypeAnalysisContext(method.DeclaringType!.DeclaringAssembly,
                "Unity.IL2CPP.CompilerServices", "Il2CppSetOptionAttribute",
                app.SystemTypes.SystemAttributeType,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            var optionConstructor = optionType.InjectMethodContext(".ctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Public);
            var originalAttributes = method.CustomAttributes;
            try
            {
                method.CustomAttributes = [new AnalyzedCustomAttribute(optionConstructor)];
                Assert.That(X86ScalarArrayAccessProof.TryLift(method, native), Is.Null,
                    "IL2CPP output options can alter the implicit exception contract.");
            }
            finally { method.CustomAttributes = originalAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
