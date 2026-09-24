using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ObjectConstructorThunkProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerPreservesEachImmediateBaseInFoldedConstructorChain()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var constructors = app.GetAssemblyByName("LiteralConcatFixture")!.Types
                .SelectMany(type => type.Methods)
                .Where(method => method.Name == ".ctor").ToArray();
            Assert.That(constructors.Length, Is.EqualTo(5));
            foreach (var constructor in constructors)
            {
                constructor.EnsureRawBytes();
                var native = X86Utils.Iterate(constructor).ToArray();
                var baseConstructor = X64ObjectConstructorThunkProof.Find(constructor, native);
                Assert.That(baseConstructor, Is.Not.Null, constructor.FullNameWithSignature);
                Assert.That(baseConstructor!.DeclaringType,
                    Is.SameAs(constructor.DeclaringType!.BaseType),
                    constructor.FullNameWithSignature);
                var lifted = app.InstructionSet.GetIsilFromMethod(constructor);
                Assert.That(lifted.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.CallVoid, ISIL.OpCode.Return }));
                Assert.That(lifted[0].Operands[0], Is.SameAs(baseConstructor));
            }

            var derived = constructors.Single(method => method.DeclaringType!.Name == "Resolver");
            var changed = X86Utils.Iterate(derived).ToArray();
            changed[1].NearBranch64 += 16;
            Assert.That(X64ObjectConstructorThunkProof.Find(derived, changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsCompleteConstructorThunkToObjectConstructor()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var owner = app.GetAssemblyByName("IteratorFactoryFixture")!.Types.Single(type =>
                type.Name == "IteratorOwner");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            var baseConstructor = X64ObjectConstructorThunkProof.Find(constructor, native);
            Assert.That(baseConstructor, Is.Not.Null);
            Assert.That(baseConstructor!.DeclaringType,
                Is.SameAs(app.SystemTypes.SystemObjectType));
            var lifted = app.InstructionSet.GetIsilFromMethod(constructor);
            Assert.That(lifted.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.CallVoid, ISIL.OpCode.Return }));
            Assert.That(lifted[0].Operands[0], Is.SameAs(baseConstructor));

            var wrongTarget = native.ToArray();
            wrongTarget[1].NearBranch64 += 16;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, wrongTarget), Is.Null);
            var wrongZeroRegister = native.ToArray();
            wrongZeroRegister[0].Op0Register = Register.EAX;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, wrongZeroRegister), Is.Null);
            var wrongTail = native.ToArray();
            wrongTail[1].Code = Code.Call_rel32_64;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, wrongTail), Is.Null);
            try
            {
                constructor.ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null);
            }
            finally { constructor.ImplAttributes = constructor.DefaultImplAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
