using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorFactoryDirectConstructorProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsDirectConstructorAndCapturedOwner()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ITERATOR_FACTORY_DIRECT_CTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ITERATOR_FACTORY_DIRECT_CTOR_FIXTURE_INPUT to the neutral player-input directory.");
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
            var assembly = app.GetAssemblyByName("IteratorFactoryDirectCtorFixture")!;
            var factory = assembly.Types.Single(type => type.Name == "FactoryOwner")
                .Methods.Single(method => method.Name == "Create");
            factory.EnsureRawBytes();
            var native = X86Utils.Iterate(factory).ToArray();

            Assert.That(X64IteratorFactoryProof.Find(factory, native), Is.Null,
                "The existing inlined-state proof must not accept a direct constructor call.");
            var proof = X64IteratorFactoryProof.FindDirectConstructor(factory, native);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Iterator.Name, Is.EqualTo("DirectStateEnumerator"));
            Assert.That(proof.Constructor.DeclaringType, Is.SameAs(proof.Iterator));
            Assert.That(proof.Constructor.UnderlyingPointer,
                Is.EqualTo(native[17].NearBranchTarget));
            Assert.That(proof.State.Name, Is.EqualTo("State"));
            Assert.That(proof.State.Offset, Is.EqualTo(16));
            Assert.That(proof.Owner.Name, Is.EqualTo("Owner"));
            Assert.That(proof.Owner.Offset, Is.EqualTo(32));
            Assert.That(native[27].NextIP - factory.UnderlyingPointer,
                Is.EqualTo(108UL));
            Assert.That(factory.RawBytes.AsSpan()[108], Is.EqualTo((byte)0xCC));
            Assert.That(X64IteratorFactoryProof.TryLift(factory, native)!
                .Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Newobj, ISIL.OpCode.CallVoid,
                    ISIL.OpCode.Move, ISIL.OpCode.Return }));

            var wrongTarget = native.ToArray();
            wrongTarget[17].NearBranch64 = native[10].NearBranchTarget;
            Assert.That(X64IteratorFactoryProof.FindDirectConstructor(factory, wrongTarget),
                Is.Null);
            var wrongOwnerOffset = native.ToArray();
            wrongOwnerOffset[18].MemoryDisplacement64 += 8;
            Assert.That(X64IteratorFactoryProof.FindDirectConstructor(factory,
                wrongOwnerOffset), Is.Null);
            var wrongMetadataGuard = native.ToArray();
            wrongMetadataGuard[3].Code = Code.Nopd;
            Assert.That(X64IteratorFactoryProof.FindDirectConstructor(factory,
                wrongMetadataGuard), Is.Null);

            try
            {
                proof.Owner.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64IteratorFactoryProof.FindDirectConstructor(factory, native),
                    Is.Null);
            }
            finally { proof.Owner.Attributes = proof.Owner.DefaultAttributes; }
            Assert.That(X64IteratorFactoryProof.FindDirectConstructor(factory, native),
                Is.Not.Null);

            proof.Constructor.EnsureRawBytes();
            var constructorNative = X86Utils.Iterate(proof.Constructor).ToArray();
            Assert.That(constructorNative[7].MemoryDisplacement64, Is.EqualTo(16UL));
            Assert.That(constructorNative[7].MemorySize.GetSize(), Is.EqualTo(4));
            var pe = (PE)app.Binary;
            var paddingOffset = pe.MapVirtualAddressToRaw(
                factory.UnderlyingPointer + 108, false);
            var storeOffset = pe.MapVirtualAddressToRaw(
                constructorNative[7].NextIP - 1, false);
            Assert.That(paddingOffset, Is.GreaterThanOrEqualTo(0));
            Assert.That(storeOffset, Is.GreaterThanOrEqualTo(0));

            var files = new DirectoryInfo(directory!).Parent?.Parent?.Parent;
            Assert.That(files?.Name, Is.EqualTo("Files"),
                "Exact-player byte mutations must remain inside the ignored Files directory.");
            var scratch = Path.Combine(files!.FullName, "validation-tests",
                "iterator-direct-ctor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                RejectPatchedPlayer(paddingOffset, 0xCC, 0x90);
                RejectPatchedPlayer(storeOffset, 0x10, 0x18);
            }
            finally
            {
                Cpp2IlApi.ResetInternalState();
                Directory.Delete(scratch, recursive: true);
            }

            void RejectPatchedPlayer(long rawOffset, byte original, byte replacement)
            {
                Cpp2IlApi.ResetInternalState();
                var patchedBinary = Path.Combine(scratch, "GameAssembly.dll");
                File.Copy(binary, patchedBinary, overwrite: true);
                using (var stream = new FileStream(patchedBinary, FileMode.Open,
                           FileAccess.ReadWrite))
                {
                    stream.Position = rawOffset;
                    Assert.That(stream.ReadByte(), Is.EqualTo(original));
                    stream.Position = rawOffset;
                    stream.WriteByte(replacement);
                }
                Cpp2IlApi.InitializeLibCpp2Il(patchedBinary, metadata,
                    UnityVersion.Parse("2021.3.35f1"));
                var mutated = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("IteratorFactoryDirectCtorFixture")!
                    .Types.Single(type => type.Name == "FactoryOwner")
                    .Methods.Single(method => method.Name == "Create");
                mutated.EnsureRawBytes();
                Assert.That(X64IteratorFactoryProof.FindDirectConstructor(mutated,
                    X86Utils.Iterate(mutated).ToArray()), Is.Null);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
