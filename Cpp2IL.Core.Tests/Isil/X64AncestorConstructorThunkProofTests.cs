using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using ManagedMethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64AncestorConstructorThunkProofTests
{
    [Test]
    public void ExactSharedThunkCallsOnlyTheImmediateBase()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONSTRUCTOR_THUNK_CHAIN_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTOR_THUNK_CHAIN_FIXTURE_INPUT to the neutral exact player-input directory.");
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
            var assembly = app.GetAssemblyByName("ConstructorThunkChainFixture")!;
            var leaves = new[] { "FirstLeaf", "SecondLeaf" }
                .Select(name => assembly.Types.Single(type => type.Name == name))
                .ToArray();
            var middleConstructors = new[] { "FirstMiddle", "SecondMiddle" }
                .Select(name => assembly.Types.Single(type => type.Name == name)
                    .Methods.Single(method => method.Name == ".ctor"))
                .ToArray();
            var roots = new[] { "FirstRoot", "SecondRoot" }
                .Select(name => assembly.Types.Single(type => type.Name == name)
                    .Methods.Single(method => method.Name == ".ctor"))
                .ToArray();
            var leafConstructors = leaves.Select(type => type.Methods.Single(method =>
                method.Name == ".ctor")).ToArray();
            var entry = leafConstructors[0].UnderlyingPointer;
            var target = roots[0].UnderlyingPointer;
            Assert.That(entry, Is.EqualTo(leafConstructors[1].UnderlyingPointer));
            Assert.That(entry, Is.EqualTo(middleConstructors[0].UnderlyingPointer));
            Assert.That(entry, Is.EqualTo(middleConstructors[1].UnderlyingPointer));
            Assert.That(target, Is.EqualTo(roots[1].UnderlyingPointer));
            Assert.That(target, Is.Not.EqualTo(entry));
            Assert.That(app.MethodsByAddress[entry], Is.EquivalentTo(
                leafConstructors.Concat(middleConstructors)));
            Assert.That(app.MethodsByAddress[target], Is.EquivalentTo(roots));

            foreach (var (constructor, immediateBase) in leafConstructors.Zip(middleConstructors))
            {
                constructor.EnsureRawBytes();
                var native = X86Utils.Iterate(constructor).ToArray();
                Assert.That(constructor.RawBytes.Length, Is.EqualTo(7));
                Assert.That(native.Select(instruction => instruction.Mnemonic),
                    Is.EqualTo(new[] { Mnemonic.Xor, Mnemonic.Jmp }));
                var evidence = X64AncestorConstructorThunkProof.Find(constructor, native);
                Assert.That(evidence, Is.Not.Null);
                Assert.That(evidence!.BaseConstructor, Is.SameAs(immediateBase));
                Assert.That(evidence.TailTarget, Is.EqualTo(target));

                // The emission operand itself must identify the immediate base,
                // even though the native branch binds only older ancestors.
                var module = new ModuleDefinition("ConstructorChainProof.dll",
                    new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
                var signature = MethodSignature.CreateInstance(module.CorLibTypeFactory.Void);
                var baseDefinition = new MethodDefinition(".ctor",
                    ManagedMethodAttributes.Public | ManagedMethodAttributes.SpecialName |
                    ManagedMethodAttributes.RuntimeSpecialName, signature);
                var leafDefinition = new MethodDefinition(".ctor",
                    ManagedMethodAttributes.Public | ManagedMethodAttributes.SpecialName |
                    ManagedMethodAttributes.RuntimeSpecialName, signature);
                immediateBase.PutExtraData("AsmResolverMethod", baseDefinition);
                Assert.That(X64AncestorConstructorThunkRecovery.TryGenerate(constructor,
                    leafDefinition), Is.True);
                Assert.That(leafDefinition.CilMethodBody!.Instructions.Select(instruction =>
                    instruction.OpCode), Is.EqualTo(new[]
                {
                    CilOpCodes.Ldarg_0, CilOpCodes.Call, CilOpCodes.Ret
                }));
                Assert.That(leafDefinition.CilMethodBody.Instructions[1].Operand,
                    Is.SameAs(baseDefinition));
            }

            var selected = leafConstructors[0];
            var body = X86Utils.Iterate(selected).ToArray();
            var badXor = body.ToArray();
            badXor[0].Op0Register = Register.EAX;
            Assert.That(X64AncestorConstructorThunkProof.Find(selected, badXor), Is.Null);
            var badTarget = body.ToArray();
            badTarget[1].NearBranch64++;
            Assert.That(X64AncestorConstructorThunkProof.Find(selected, badTarget), Is.Null);

            var entryAliases = app.MethodsByAddress[entry];
            var targetAliases = app.MethodsByAddress[target];
            var unrelated = app.SystemTypes.SystemObjectType.Methods.First(method =>
                method.Name == "ToString" && method.Parameters.Count == 0);
            try
            {
                entryAliases.Add(unrelated);
                Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Null,
                    "A nonconstructor sharing the native entry defeats the proof.");
            }
            finally { entryAliases.Remove(unrelated); }
            try
            {
                targetAliases.Add(unrelated);
                Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Null,
                    "A nonconstructor sharing the native target defeats the proof.");
            }
            finally { targetAliases.Remove(unrelated); }
            try
            {
                targetAliases.Remove(roots[0]);
                Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Null,
                    "The ancestor must be bound at the native target.");
            }
            finally { targetAliases.Add(roots[0]); }
            try
            {
                entryAliases.Remove(middleConstructors[0]);
                Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Null,
                    "The immediate base must share the complete thunk.");
            }
            finally { entryAliases.Add(middleConstructors[0]); }
            try
            {
                roots[1].ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Null,
                    "Every target alias must have an ordinary implementation.");
            }
            finally { roots[1].ImplAttributes = roots[1].DefaultImplAttributes; }
            Assert.That(X64AncestorConstructorThunkProof.Find(selected, body), Is.Not.Null);

            // Reinitialize from a byte-mutated PE, so decoding and the
            // file-backed consistency check both see an altered native body.
            var pe = (PE)app.Binary;
            var offset = checked((int)pe.MapVirtualAddressToRaw(entry, false));
            var modifiedBinary = File.ReadAllBytes(binary);
            Assert.That(modifiedBinary[offset], Is.EqualTo((byte)0x33));
            modifiedBinary[offset] = 0x90;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(modifiedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConstructorThunkChainFixture")!.Types
                .Single(type => type.Name == "FirstLeaf").Methods
                .Single(method => method.Name == ".ctor");
            Assert.That(X64AncestorConstructorThunkProof.Find(changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
