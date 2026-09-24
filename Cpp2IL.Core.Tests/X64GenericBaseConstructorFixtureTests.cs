using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player regression for a constructed generic base call.</summary>
[NonParallelizable]
public class X64GenericBaseConstructorFixtureTests
{
    [Test]
    public void MethodRefBindsOnlyTheImmediateGenericBaseConstructor()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_GETTER_METADATA_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_GETTER_METADATA_FIXTURE_INPUT to the neutral player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("BooleanGetterMetadataFixture")!.Types
                .Single(type => type.Name == "GenericFieldOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == ".ctor");
            var evidence = X64GenericBaseConstructorProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.BaseConstructor.DeclaringType,
                Is.TypeOf<GenericInstanceTypeAnalysisContext>());
            var constructedBase = (GenericInstanceTypeAnalysisContext)evidence.BaseConstructor.DeclaringType!;
            Assert.Multiple(() =>
            {
                Assert.That(constructedBase.GenericType,
                    Is.SameAs(((GenericInstanceTypeAnalysisContext)owner.BaseType!).GenericType));
                Assert.That(evidence.BaseConstructor.BaseMethodContext.Name, Is.EqualTo(".ctor"));
                Assert.That(evidence.BaseConstructor.BaseMethodContext.DeclaringType,
                    Is.SameAs(constructedBase.GenericType));
            });

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X64GenericBaseConstructorProof.TryProveShape(native, out var shape), Is.True);
            Assert.That(app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot)?.Type,
                Is.EqualTo(MetadataUsageType.MethodRef));
            Assert.That(app.MethodsByAddress[shape.Target].Count, Is.GreaterThan(1),
                "the MethodRef identifies the base constructor despite a folded native target");

            foreach (var defect in new[] { "branch", "slot register", "once write", "argument register", "tail kind" })
            {
                var changed = native.ToArray();
                switch (defect)
                {
                    case "branch": changed[4].NearBranch64++; break;
                    case "slot register": changed[5].Op0Register = Register.RDX; break;
                    case "once write": changed[7].Immediate8 = 0; break;
                    case "argument register": changed[8].Op0Register = Register.R8; break;
                    case "tail kind": changed[12].Code = Code.Call_rel32_64; break;
                }
                Assert.That(X64GenericBaseConstructorProof.TryProveShape(changed, out _), Is.False, defect);
            }

            try
            {
                method.ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X64GenericBaseConstructorProof.Find(method), Is.Null);
            }
            finally { method.ImplAttributes = method.DefaultImplAttributes; }
            Assert.That(X64GenericBaseConstructorProof.Find(method), Is.Not.Null);

            var pe = (PE)app.Binary;
            var tailRaw = checked((int)pe.MapVirtualAddressToRaw(native[12].IP, false));
            var slotRaw = checked((int)pe.MapVirtualAddressToRaw(shape.MethodSlot, false));
            var original = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            Assert.That(original[tailRaw], Is.EqualTo(0xE9));
            var wrongTarget = (byte[])original.Clone();
            wrongTarget[tailRaw + 1] ^= 1;
            AssertRejected(wrongTarget, metadataBytes);

            var wrongMethodRef = (byte[])original.Clone();
            Array.Clear(wrongMethodRef, slotRaw, 8);
            AssertRejected(wrongMethodRef, metadataBytes);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void AssertRejected(byte[] binary, byte[] metadata)
    {
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
        var method = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("BooleanGetterMetadataFixture")!.Types
            .Single(type => type.Name == "GenericFieldOwner").Methods.Single(candidate => candidate.Name == ".ctor");
        Assert.That(X64GenericBaseConstructorProof.Find(method), Is.Null);
    }
}
