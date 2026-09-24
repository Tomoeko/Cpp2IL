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

/// <summary>Optional exact-player regression for the folded class-init constructor.</summary>
[NonParallelizable]
public class X64GuardedBaseConstructorFixtureTests
{
    [Test]
    public void ConstructorBindsOnlyTheImmediateBaseThroughTheCompleteFoldedChain()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_VIRTUAL_STRING_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_VIRTUAL_STRING_CALL_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var selected = app.GetAssemblyByName("VirtualStringCallFixture")!;
            var hierarchy = app.GetAssemblyByName("Neutral.CastHierarchy")!;
            var owner = selected.Types.Single(type => type.Name == "DerivedNode");
            var middle = hierarchy.Types.Single(type => type.Name == "MiddleNode");
            var chain = hierarchy.Types.Single(type => type.Name == "ChainNode");
            var outer = hierarchy.Types.Single(type => type.Name == "OuterBase");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            var middleConstructor = middle.Methods.Single(method => method.Name == ".ctor");
            var chainConstructor = chain.Methods.Single(method => method.Name == ".ctor");
            var outerConstructor = outer.Methods.Single(method => method.Name == ".ctor");

            Assert.That(X64GuardedBaseConstructorProof.Find(constructor)?.BaseConstructor,
                Is.SameAs(middleConstructor));
            constructor.EnsureRawBytes();
            middleConstructor.EnsureRawBytes();
            outerConstructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            var folded = X86Utils.Iterate(middleConstructor).ToArray();
            var callerShape = X64GuardedBaseConstructorProof.TryProveShape(native);
            var foldedShape = X64GuardedBaseConstructorProof.TryProveShape(folded);
            Assert.Multiple(() =>
            {
                Assert.That(constructor.RawBytes.Length, Is.EqualTo(73));
                Assert.That(native.Length, Is.EqualTo(17));
                Assert.That(middleConstructor.RawBytes.Length, Is.EqualTo(73));
                Assert.That(folded.Length, Is.EqualTo(17));
                Assert.That(outerConstructor.RawBytes.Length, Is.EqualTo(7));
                Assert.That(callerShape, Is.Not.Null);
                Assert.That(foldedShape, Is.Not.Null);
                Assert.That(middle.Definition!.HasCctor, Is.True);
                Assert.That(outer.Definition!.HasCctor, Is.True);
                Assert.That(chain.Definition!.HasCctor, Is.False);
            });
            Assert.That(app.ResolveIl2CppType(app.LibCpp2IlContext
                .GetRawTypeGlobalByAddress(callerShape!.Value.TypeInfoSlot)!
                .AsType()), Is.SameAs(middle));
            Assert.That(app.ResolveIl2CppType(app.LibCpp2IlContext
                .GetRawTypeGlobalByAddress(foldedShape!.Value.TypeInfoSlot)!
                .AsType()), Is.SameAs(outer));
            Assert.That(app.MethodsByAddress[callerShape.Value.Tail],
                Is.EquivalentTo(new[] { middleConstructor, chainConstructor }));
            Assert.That(X64ObjectConstructorThunkProof.Find(outerConstructor,
                X86Utils.Iterate(outerConstructor).ToArray()), Is.Not.Null);

            foreach (var defect in new[] { "once branch", "once write", "class status",
                         "class branch", "argument register", "tail kind" })
            {
                var changed = native.ToArray();
                switch (defect)
                {
                    case "once branch": changed[4].NearBranch64++; break;
                    case "once write": changed[7].Immediate8 = 0; break;
                    case "class status": changed[9].MemoryDisplacement64++; break;
                    case "class branch": changed[10].NearBranch64++; break;
                    case "argument register": changed[13].Op0Register = Register.RDX; break;
                    case "tail kind": changed[16].Code = Code.Call_rel32_64; break;
                }
                Assert.That(X64GuardedBaseConstructorProof.TryProveShape(changed),
                    Is.Null, defect);
            }

            var baseAttributes = middle.OverrideAttributes;
            try
            {
                middle.Attributes |= TypeAttributes.BeforeFieldInit;
                Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null,
                    "Relaxed type-initialization timing is outside the proof.");
            }
            finally { middle.OverrideAttributes = baseAttributes; }

            var middleBits = middle.Definition!.Bitfield;
            try
            {
                middle.Definition.Bitfield = middleBits & ~(1u << 3);
                Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null,
                    "The native class-init guard must agree with metadata.");
            }
            finally { middle.Definition.Bitfield = middleBits; }

            var originalBase = owner.OverrideBaseType;
            try
            {
                owner.BaseType = chain;
                Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null,
                    "The guarded TypeInfo must name the immediate managed base.");
            }
            finally { owner.OverrideBaseType = originalBase; }

            var references = owner.DeclaringAssembly.Definition!.ReferencedAssemblyCount;
            try
            {
                owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = 0;
                Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null,
                    "The cross-assembly base must be an explicit reference.");
            }
            finally { owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = references; }

            var aliases = app.MethodsByAddress[callerShape.Value.Tail];
            var aliasIndex = aliases.IndexOf(chainConstructor);
            Assert.That(aliasIndex, Is.GreaterThanOrEqualTo(0));
            try
            {
                aliases.RemoveAt(aliasIndex);
                Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null,
                    "The folded tail must have the complete proved alias chain.");
            }
            finally { aliases.Insert(aliasIndex, chainConstructor); }
            Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Not.Null);

            var pe = (PE)app.Binary;
            var original = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            var classInitRaw = checked((int)pe.MapVirtualAddressToRaw(native[11].IP,
                false));
            var foldedClassInitRaw = checked((int)pe.MapVirtualAddressToRaw(
                folded[11].IP, false));
            var tailRaw = checked((int)pe.MapVirtualAddressToRaw(native[16].IP,
                false));
            Assert.That(original[classInitRaw], Is.EqualTo(0xE8));
            Assert.That(original[foldedClassInitRaw], Is.EqualTo(0xE8));
            Assert.That(original[tailRaw], Is.EqualTo(0xE9));
            var wrongClassInit = (byte[])original.Clone();
            wrongClassInit[classInitRaw + 1] ^= 1;
            AssertRejected(wrongClassInit, metadataBytes);
            var wrongFoldedClassInit = (byte[])original.Clone();
            wrongFoldedClassInit[foldedClassInitRaw + 1] ^= 1;
            AssertRejected(wrongFoldedClassInit, metadataBytes);
            var wrongTail = (byte[])original.Clone();
            wrongTail[tailRaw + 1] ^= 1;
            AssertRejected(wrongTail, metadataBytes);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void AssertRejected(byte[] binary, byte[] metadata)
    {
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
            UnityVersion.Parse("2021.3.35f1"));
        var constructor = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("VirtualStringCallFixture")!.Types
            .Single(type => type.Name == "DerivedNode").Methods
            .Single(method => method.Name == ".ctor");
        Assert.That(X64GuardedBaseConstructorProof.Find(constructor), Is.Null);
    }
}
