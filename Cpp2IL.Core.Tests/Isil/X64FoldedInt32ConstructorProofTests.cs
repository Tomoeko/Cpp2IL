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

namespace Cpp2IL.Core.Tests.Isil;

public class X64FoldedInt32ConstructorProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsUnwindBoundedFoldedConstructors()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_STATE_CONSTRUCTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FOLDED_STATE_CONSTRUCTOR_FIXTURE_INPUT to the neutral player-input directory.");
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
            var assembly = app.GetAssemblyByName("FoldedStateConstructorFixture")!;
            var first = assembly.Types.Single(type => type.Name == "FirstCell");
            var second = assembly.Types.Single(type => type.Name == "SecondCell");
            var firstConstructor = first.Methods.Single(method => method.Name == ".ctor");
            var secondConstructor = second.Methods.Single(method => method.Name == ".ctor");
            Assert.That(firstConstructor.UnderlyingPointer,
                Is.EqualTo(secondConstructor.UnderlyingPointer));
            Assert.That(app.MethodsByAddress[firstConstructor.UnderlyingPointer]
                .Count(candidate => ReferenceEquals(candidate, firstConstructor)), Is.EqualTo(1));
            Assert.That(app.MethodsByAddress[firstConstructor.UnderlyingPointer]
                .Count(candidate => ReferenceEquals(candidate, secondConstructor)), Is.EqualTo(1));

            foreach (var constructor in new[] { firstConstructor, secondConstructor })
            {
                constructor.EnsureRawBytes();
                var native = X86Utils.Iterate(constructor).ToArray();
                var proof = X64FoldedInt32ConstructorProof.Find(constructor, native);
                Assert.That(proof, Is.Not.Null);
                Assert.That(proof!.NativeEnd - constructor.UnderlyingPointer,
                    Is.EqualTo(36UL));
                Assert.That(constructor.RawBytes.Length, Is.GreaterThan(36));
                Assert.That(proof.State.Name, Is.EqualTo("State"));
                Assert.That(proof.State.Offset, Is.EqualTo(16));
                Assert.That(proof.BaseConstructor.DeclaringType,
                    Is.SameAs(app.SystemTypes.SystemObjectType));
                Assert.That(app.MethodsByAddress[proof.BaseConstructor.UnderlyingPointer].Count,
                    Is.GreaterThan(1));
                Assert.That(X64FoldedInt32ConstructorProof.TryLift(constructor, native)!
                    .Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.CallVoid, ISIL.OpCode.Move,
                        ISIL.OpCode.Return }));
            }

            var body = X86Utils.Iterate(firstConstructor).ToArray();
            var fabricatedMove = body.ToArray();
            fabricatedMove[3].Code = Code.Mov_r64_rm64;
            var bound = X64FoldedInt32ConstructorProof.Find(firstConstructor, body)!;
            Assert.That(X64IteratorFactoryProof.TryProveConstructorShape(
                fabricatedMove.Take(12).ToArray(), bound.State.Offset,
                bound.BaseConstructor.UnderlyingPointer), Is.True);
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                fabricatedMove), Is.Null);
            var wrongCall = body.ToArray();
            wrongCall[6].NearBranch64 = firstConstructor.UnderlyingPointer;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, wrongCall),
                Is.Null);
            var wrongField = body.ToArray();
            wrongField[7].MemoryDisplacement64 += 8;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, wrongField),
                Is.Null);
            var missingStore = body.ToArray();
            missingStore[7].Code = Code.Nopd;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, missingStore),
                Is.Null);
            var missingPadding = body.ToArray();
            missingPadding[12].Code = Code.Nopd;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                missingPadding), Is.Null);
            var crossesUnwindBoundary = body.ToArray();
            var nextEntry = Array.FindIndex(crossesUnwindBoundary, 12,
                instruction => instruction.Code != Code.Int3);
            Assert.That(nextEntry, Is.GreaterThan(12));
            var fabricatedSuffix = body.ToArray();
            fabricatedSuffix[nextEntry].Code = Code.Nopd;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                fabricatedSuffix), Is.Null);
            crossesUnwindBoundary[11].Code = Code.Jmp_rel32_64;
            crossesUnwindBoundary[11].NearBranch64 = crossesUnwindBoundary[nextEntry].IP;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                crossesUnwindBoundary), Is.Null);
            var shiftedNextEntry = body.ToArray();
            shiftedNextEntry[nextEntry].IP++;
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                shiftedNextEntry), Is.Null);

            var state = first.Fields.Single(field => field.Name == "State");
            var neighbor = first.Fields.Single(field => field.Name == "Neighbor");
            try
            {
                state.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, body),
                    Is.Null);
            }
            finally { state.OverrideFieldType = null; }
            try
            {
                neighbor.Offset = state.Offset;
                Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, body),
                    Is.Null);
            }
            finally { neighbor.OverrideOffset = null; }
            try
            {
                first.BaseType = second;
                Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, body),
                    Is.Null);
            }
            finally { first.OverrideBaseType = null; }
            try
            {
                firstConstructor.Attributes |= MethodAttributes.Static;
                Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, body),
                    Is.Null);
            }
            finally { firstConstructor.Attributes = firstConstructor.DefaultAttributes; }
            var optionType = new InjectedTypeAnalysisContext(first.DeclaringAssembly,
                "Unity.IL2CPP.CompilerServices", "Il2CppSetOptionAttribute",
                app.SystemTypes.SystemAttributeType,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            var optionConstructor = optionType.InjectMethodContext(".ctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Public);
            var option = new AnalyzedCustomAttribute(optionConstructor);
            foreach (var scope in new HasCustomAttributes[]
                     { firstConstructor, first, first.DeclaringAssembly })
            {
                var original = scope.CustomAttributes;
                try
                {
                    scope.CustomAttributes = [option];
                    Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor,
                        body), Is.Null);
                }
                finally { scope.CustomAttributes = original; }
            }
            Assert.That(X64FoldedInt32ConstructorProof.Find(firstConstructor, body),
                Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
