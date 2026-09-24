using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
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

    [Test]
    [NonParallelizable]
    public void ExactPlayerIsolatesOverlongLeafConstructorWithoutGuessingAnAlias()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIELD_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_ARRAY_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var owner = app.GetAssemblyByName("FieldArrayFixture")!.Types.Single(type =>
                type.Name == "FieldArrayState");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            Assert.That(constructor.RawBytes.Length, Is.GreaterThan(7));
            Assert.That(native[2].Code, Is.EqualTo(Code.Int3));

            var baseConstructor = X64ObjectConstructorThunkProof.Find(constructor, native);
            Assert.That(baseConstructor, Is.Not.Null);
            Assert.That(baseConstructor!.DeclaringType,
                Is.SameAs(app.SystemTypes.SystemObjectType));
            var lifted = app.InstructionSet.GetIsilFromMethod(constructor);
            Assert.That(lifted.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.CallVoid, ISIL.OpCode.Return }));
            Assert.That(lifted[0].Operands[0], Is.SameAs(baseConstructor));

            var changed = native.ToArray();
            changed[1].Code = Code.Call_rel32_64;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changed), Is.Null);
            changed = native.ToArray();
            changed[1].NearBranch64 += 16;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changed), Is.Null);
            changed = native.ToArray();
            changed[2].Code = Code.Nopd;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changed), Is.Null,
                "A non-trap byte immediately after the tail leaves the overlong span unbounded.");
            changed = native.ToArray();
            var lastTrap = Array.FindIndex(native, 2, instruction => instruction.Code != Code.Int3) - 1;
            Assert.That(lastTrap, Is.GreaterThan(2));
            changed[lastTrap].Code = Code.Nopd;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changed), Is.Null,
                "The following function must have its own unwind boundary after real padding.");
            changed = native.ToArray();
            changed[lastTrap + 1].IP++;
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changed), Is.Null,
                "The first instruction of the separate unwind function must start after padding.");

            var originalBase = owner.OverrideBaseType;
            try
            {
                owner.OverrideBaseType = app.SystemTypes.SystemStringType;
                Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null);
            }
            finally { owner.OverrideBaseType = originalBase; }

            var otherConstructor = new InjectedMethodAnalysisContext(owner, ".ctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Public, []);
            owner.Methods.Add(otherConstructor);
            try { Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null); }
            finally { owner.Methods.Remove(otherConstructor); }

            var staticInitializer = new InjectedMethodAnalysisContext(owner, ".cctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Private | MethodAttributes.Static, []);
            owner.Methods.Add(staticInitializer);
            try { Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null); }
            finally { owner.Methods.Remove(staticInitializer); }

            var entryAliases = app.MethodsByAddress[constructor.UnderlyingPointer];
            var entryIndex = entryAliases.IndexOf(constructor);
            Assert.That(entryIndex, Is.GreaterThanOrEqualTo(0));
            entryAliases.RemoveAt(entryIndex);
            try { Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null); }
            finally { entryAliases.Insert(entryIndex, constructor); }

            var targetAliases = app.MethodsByAddress[baseConstructor.UnderlyingPointer];
            var targetIndex = targetAliases.IndexOf(baseConstructor);
            Assert.That(targetIndex, Is.GreaterThanOrEqualTo(0));
            targetAliases.RemoveAt(targetIndex);
            try { Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null); }
            finally { targetAliases.Insert(targetIndex, baseConstructor); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
