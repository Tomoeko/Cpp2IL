using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorFactoryProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void InlinedStoresBindTheStateAndCapturedOwner(bool stateBeforeOwner)
    {
        var assembler = new Assembler(64);
        assembler.lea(rcx, __[rbx + 0x28]);
        if (stateBeforeOwner)
        {
            assembler.mov(__dword_ptr[rbx + 0x18], 0);
            assembler.mov(rdx, rdi);
            assembler.mov(__qword_ptr[rbx + 0x28], rdi);
        }
        else
        {
            assembler.mov(rdx, rdi);
            assembler.mov(__qword_ptr[rcx], rdi);
            assembler.mov(__dword_ptr[rbx + 0x18], 0);
        }
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        var body = new Iced.Intel.Instruction[28];
        X86Utils.Iterate(stream.ToArray(), 0x1000, false).CopyTo(body, 17);
        Assert.That(X64IteratorFactoryProof.TryProveInlinedStores(body, stateBeforeOwner), Is.True);
        Assert.That(X64IteratorFactoryProof.TryProveInlinedStores(body, !stateBeforeOwner), Is.False);

        var wrongCapture = body.ToArray();
        wrongCapture[stateBeforeOwner ? 20 : 19].MemoryDisplacement64 += 8;
        Assert.That(X64IteratorFactoryProof.TryProveInlinedStores(wrongCapture, stateBeforeOwner), Is.False);
        var wrongOwner = body.ToArray();
        wrongOwner[stateBeforeOwner ? 20 : 19].Op1Register = Register.RSI;
        Assert.That(X64IteratorFactoryProof.TryProveInlinedStores(wrongOwner, stateBeforeOwner), Is.False);
        var nonzeroState = body.ToArray();
        nonzeroState[stateBeforeOwner ? 18 : 20].Immediate32 = 1;
        Assert.That(X64IteratorFactoryProof.TryProveInlinedStores(nonzeroState, stateBeforeOwner), Is.False);
    }

    [Test]
    [NonParallelizable]
    public void GeneratedFactoryRejectsChangedCacheAndScheduledStores()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_GENERATED_FIXTURE_INPUT to the neutral generated iterator player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var factory = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("IteratorFactoryFixture")!.Types
                .Single(type => type.Name == "IteratorOwner").Methods.Single(method => method.Name == "Iterate");
            factory.EnsureRawBytes();
            var native = X86Utils.Iterate(factory).ToArray();
            var proof = X64IteratorFactoryProof.Find(factory, native);
            Assert.That(proof, Is.Not.Null);
            foreach (var method in new[] { factory, proof!.Constructor })
            {
                var flags = method.Definition!.iflags;
                method.Definition.iflags = (ushort)(flags | (ushort)MethodImplAttributes.Synchronized);
                try { Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Null); }
                finally { method.Definition.iflags = flags; }
            }
            var bytes = factory.RawBytes;
            var changed = bytes.AsSpan().ToArray();
            changed[0] ^= 1;
            factory.RawBytes = new BinarySlice(changed);
            try { Assert.That(X64IteratorFactoryProof.Find(factory, X86Utils.Iterate(factory).ToArray()), Is.Null); }
            finally { factory.RawBytes = bytes; }
            var fabricated = native.ToArray();
            fabricated[17].MemoryDisplacement64 += 8;
            Assert.That(X64IteratorFactoryProof.Find(factory, fabricated), Is.Null);
            Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsFactoryToConstructorFieldsAndHelpers()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ITERATOR_FACTORY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_FACTORY_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("IteratorFactoryManualFixture")!;
            var factory = assembly.Types.Single(type => type.Name == "ManualOwner")
                .Methods.Single(method => method.Name == "Create");
            factory.EnsureRawBytes();
            var native = X86Utils.Iterate(factory).ToArray();
            var proof = X64IteratorFactoryProof.Find(factory, native);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Iterator.Name, Is.EqualTo("ManualEnumerator"));
            Assert.That(proof.Constructor.Name, Is.EqualTo(".ctor"));
            Assert.That(proof.State.Name, Is.EqualTo("State"));
            Assert.That(proof.Owner.Name, Is.EqualTo("Owner"));
            Assert.That(X64IteratorFactoryProof.TryLift(factory, native)!.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Newobj, ISIL.OpCode.CallVoid, ISIL.OpCode.Move,
                    ISIL.OpCode.Return }));

            var constructor = proof.Constructor;
            constructor.EnsureRawBytes();
            var constructorNative = X86Utils.Iterate(constructor).ToArray();
            Assert.That(X64IteratorFactoryProof.TryProveConstructorShape(constructorNative,
                proof.State.Offset, native[16].NearBranchTarget), Is.True);

            var wrongState = constructorNative.ToArray();
            wrongState[7].MemoryDisplacement64 += 4;
            Assert.That(X64IteratorFactoryProof.TryProveConstructorShape(wrongState,
                proof.State.Offset, native[16].NearBranchTarget), Is.False);
            var missingStore = constructorNative.ToArray();
            missingStore[7].Code = Code.Nopd;
            Assert.That(X64IteratorFactoryProof.TryProveConstructorShape(missingStore,
                proof.State.Offset, native[16].NearBranchTarget), Is.False);
            var extraEffect = constructorNative.ToList();
            var unexpectedCall = constructorNative[7];
            unexpectedCall.Code = Code.Call_rel32_64;
            unexpectedCall.NearBranch64 = 0x2000;
            extraEffect.Insert(8, unexpectedCall);
            Assert.That(X64IteratorFactoryProof.TryProveConstructorShape(extraEffect,
                proof.State.Offset, native[16].NearBranchTarget), Is.False);

            var wrongMetadata = native.ToArray();
            wrongMetadata[7].NearBranch64 = native[10].NearBranchTarget;
            Assert.That(X64IteratorFactoryProof.Find(factory, wrongMetadata), Is.Null);
            var wrongAllocator = native.ToArray();
            wrongAllocator[10].NearBranch64 = native[7].NearBranchTarget;
            Assert.That(X64IteratorFactoryProof.Find(factory, wrongAllocator), Is.Null);
            var wrongBarrier = native.ToArray();
            wrongBarrier[21].NearBranch64 = native[27].NearBranchTarget;
            Assert.That(X64IteratorFactoryProof.Find(factory, wrongBarrier), Is.Null);
            var wrongOwnerField = native.ToArray();
            wrongOwnerField[17].MemoryDisplacement64 += 8;
            Assert.That(X64IteratorFactoryProof.Find(factory, wrongOwnerField), Is.Null);

            try
            {
                proof.Owner.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Null);
            }
            finally { proof.Owner.Attributes = proof.Owner.DefaultAttributes; }
            try
            {
                constructor.Attributes = (constructor.DefaultAttributes & ~MethodAttributes.MemberAccessMask) |
                    MethodAttributes.Private;
                Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Null);
            }
            finally { constructor.Attributes = constructor.DefaultAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
