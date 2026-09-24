using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorConstructorProofTests
{
    [TestCase("CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT", "IteratorFactoryFixture",
        "<Iterate>d__", "Iterate")]
    [TestCase("CPP2IL_ITERATOR_FACTORY_FIXTURE_INPUT", "IteratorFactoryManualFixture",
        "ManualEnumerator", "Create")]
    [NonParallelizable]
    public void ExactPlayerBindsBaseConstructorBeforeIteratorStateStore(string inputVariable,
        string assemblyName, string iteratorNamePart, string factoryName)
    {
        var directory = Environment.GetEnvironmentVariable(inputVariable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore($"Set {inputVariable} to the neutral synthetic player-input directory.");
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
            var iterator = app.GetAssemblyByName(assemblyName)!.Types.Single(type =>
                type.Name.Contains(iteratorNamePart, StringComparison.Ordinal));
            var constructor = iterator.Methods.Single(method => method.Name == ".ctor");
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            var proof = X64IteratorConstructorProof.Find(constructor, native);
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.BaseConstructor.DeclaringType,
                    Is.SameAs(app.SystemTypes.SystemObjectType));
                Assert.That(proof.State.Offset, Is.EqualTo(16));
                Assert.That(proof.Factory.Name, Is.EqualTo(factoryName));
            });
            var lifted = app.InstructionSet.GetIsilFromMethod(constructor);
            Assert.That(lifted.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.CallVoid, ISIL.OpCode.Move,
                    ISIL.OpCode.Return }));
            Assert.That(lifted[0].Operands[0], Is.SameAs(proof!.BaseConstructor),
                "the first managed action initializes this through Object::.ctor");

            var wrongObjectCall = native.ToArray();
            wrongObjectCall[6].NearBranch64 = native[6].NearBranchTarget + 16;
            Assert.That(X64IteratorConstructorProof.Find(constructor, wrongObjectCall), Is.Null);
            var wrongState = native.ToArray();
            wrongState[7].MemoryDisplacement64 += 8;
            Assert.That(X64IteratorConstructorProof.Find(constructor, wrongState), Is.Null);
            var extraEffect = native.ToArray();
            extraEffect[7].Code = Code.Inc_rm32;
            Assert.That(X64IteratorConstructorProof.Find(constructor, extraEffect), Is.Null);

            var state = proof.State;
            try
            {
                state.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64IteratorConstructorProof.Find(constructor, native), Is.Null);
            }
            finally { state.Attributes = state.DefaultAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
