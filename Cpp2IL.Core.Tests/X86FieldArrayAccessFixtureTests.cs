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
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player gate for a field-backed Int32 array access.</summary>
[NonParallelizable]
public class X86FieldArrayAccessFixtureTests
{
    [TestCase("ReadFirst", true, false)]
    [TestCase("ReadAt", false, false)]
    [TestCase("WriteAt", false, true)]
    public void ExactAccessPreservesFieldLoadAndArrayOperation(string name,
        bool constantIndex, bool isWrite)
    {
        var kind = constantIndex ? X86FieldArrayAccessProof.AccessKind.ReadFirst :
            isWrite ? X86FieldArrayAccessProof.AccessKind.WriteAt :
                X86FieldArrayAccessProof.AccessKind.ReadAt;
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIELD_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_ARRAY_FIXTURE_INPUT to the fixture's player-input directory.");
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
            var method = app.GetAssemblyByName("FieldArrayFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == name);
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X86FieldArrayAccessProof.TryProveShape(native);
            Assert.That(shape?.Kind, Is.EqualTo(kind));
            var evidence = X86FieldArrayAccessProof.Find(method, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Field.FieldType, Is.TypeOf<SzArrayTypeAnalysisContext>());
            Assert.That(((SzArrayTypeAnalysisContext)evidence.Field.FieldType).ElementType,
                Is.SameAs(app.SystemTypes.SystemInt32Type));

            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                method.UnderlyingPointer, native, shape!.BoundsCallIndex);
            Assert.That(region, Has.Count.EqualTo(kind == X86FieldArrayAccessProof.AccessKind.ReadFirst ? 13 : 14));
            Assert.That(region![^1].Code, Is.EqualTo(Code.Int3));
            Assert.That(X86CallerExceptionRegionProof.Check(method, region,
                new[] { region[shape.NullCallIndex].IP, region[shape.BoundsCallIndex].IP }.ToHashSet()),
                Is.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Return }));
            method.Analyze();
            var recovered = method.ControlFlowGraph!.Instructions.ToArray();
            Assert.That(recovered.Any(instruction => instruction is
                { OpCode: IsilOpCode.Move, Operands: [_, Cpp2IL.Core.ISIL.FieldReference field] } &&
                ReferenceEquals(field.Field, evidence.Field)), Is.True);
            Assert.That(recovered.Any(instruction => instruction.OpCode == IsilOpCode.Move &&
                instruction.Operands.Count > 1 &&
                instruction.Operands[kind == X86FieldArrayAccessProof.AccessKind.WriteAt ? 0 : 1] is
                    Cpp2IL.Core.ISIL.ArrayAccess access &&
                (constantIndex ? access.Index is Cpp2IL.Core.ISIL.Immediate { Value: 0 } :
                    access.Index is Cpp2IL.Core.ISIL.LocalVariable)), Is.True);

            var wrongField = native.ToArray();
            wrongField[1].MemoryDisplacement64 += 8;
            Assert.That(X86FieldArrayAccessProof.TryLift(method, wrongField), Is.Null,
                "A neighboring field cannot stand in for the array reference.");
            var wrongBounds = native.ToArray();
            wrongBounds[5].Code = Code.Ja_rel8_64;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongBounds), Is.Null);
            var wrongNull = native.ToArray();
            wrongNull[3].NearBranch64 = native[shape.BoundsCallIndex].IP;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongNull), Is.Null);
            var wrongElement = native.ToArray();
            wrongElement[kind == X86FieldArrayAccessProof.AccessKind.ReadFirst ? 6 : 7]
                .MemoryDisplacement64 = 0x24;
            Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongElement), Is.Null);
            if (isWrite)
            {
                var wrongValue = native.ToArray();
                wrongValue[7].Op1Register = Register.R9D;
                Assert.That(X86FieldArrayAccessProof.TryProveShape(wrongValue), Is.Null);
            }

            var originalOffset = evidence.Field.OverrideOffset;
            var originalType = evidence.Field.OverrideFieldType;
            try
            {
                evidence.Field.OverrideOffset = evidence.Field.DefaultOffset + 8;
                Assert.That(X86FieldArrayAccessProof.TryLift(method, native), Is.Null);
                evidence.Field.OverrideOffset = originalOffset;
                evidence.Field.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X86FieldArrayAccessProof.TryLift(method, native), Is.Null);
            }
            finally
            {
                evidence.Field.OverrideOffset = originalOffset;
                evidence.Field.OverrideFieldType = originalType;
            }

            var optionType = new InjectedTypeAnalysisContext(method.DeclaringType!.DeclaringAssembly,
                "Unity.IL2CPP.CompilerServices", "Il2CppSetOptionAttribute",
                app.SystemTypes.SystemAttributeType,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            var optionConstructor = optionType.InjectMethodContext(".ctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Public);
            var option = new AnalyzedCustomAttribute(optionConstructor);
            foreach (var scope in new HasCustomAttributes[]
                     { method, method.DeclaringType, method.DeclaringType.DeclaringAssembly })
            {
                var original = scope.CustomAttributes;
                try
                {
                    scope.CustomAttributes = [option];
                    Assert.That(X86FieldArrayAccessProof.TryLift(method, native), Is.Null,
                        "An IL2CPP output option can alter the implicit exception contract.");
                }
                finally { scope.CustomAttributes = original; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
