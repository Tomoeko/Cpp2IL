using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarWrapperStaticConstructorFixtureTests
{
    [Test]
    public void ExactPlayerRequiresTheCompleteSelfFieldInitializer()
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_SCALAR_WRAPPER_CCTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_WRAPPER_CCTOR_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(input!, "GameAssembly.dll");
        var metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var assembly = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ScalarWrapperCctorFixture")!;
            foreach (var (name, bits, beforeFieldInit, methods) in new[]
                     {
                         ("NarrowCell", 17UL, true,
                             new[] { "ToString", "CompareTo" }),
                         ("WideCell", 0xffffffff80000000UL, true,
                             new[] { "ToString", "GetHashCode", "CompareTo" }),
                     })
            {
                var type = assembly.Types.Single(candidate => candidate.Name == name);
                var constructor = type.Methods.Single(candidate =>
                    candidate.Name == ".cctor");
                var proof = X64ScalarWrapperStaticConstructorProof.Find(constructor);
                Assert.That(proof, Is.Not.Null, name);
                Assert.Multiple(() =>
                {
                    Assert.That(proof!.ValueBits, Is.EqualTo(bits), name);
                    Assert.That(proof.StaticField.FieldType, Is.SameAs(type), name);
                    Assert.That(proof.ScalarField.Offset, Is.Zero, name);
                    Assert.That((type.Attributes & TypeAttributes.BeforeFieldInit) != 0,
                        Is.EqualTo(beforeFieldInit), name);
                });

                Assert.That(X64ScalarWrapperStaticConstructorProof.TryAuthenticate(constructor, out _), Is.True);
                var pe = (PE)constructor.AppContext.Binary;
                Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>());
                Assert.That(pe.TryGetGenericMethodTableRegistration(out var origin), Is.True);
                var registration = pe.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(origin.MetadataRegistrationAddress);
                var offsets = pe.ReadPointerAtVirtualAddress(registration.fieldOffsetListAddress +
                    (ulong)type.Definition!.TypeIndex.Value * 8);
                var address = offsets + (ulong)proof!.ScalarField.BackingData!.IndexInParent * 4;
                var rawOffset = checked((int)pe.MapVirtualAddressToRaw(address, false));
                var original = pe.GetRawBinaryContent().Slice(rawOffset, 4).ToArray();
                Assert.That(BitConverter.ToInt32(original), Is.EqualTo(16));
                foreach (var changedOffset in new[] { 0, 17 })
                {
                    var position = pe.BaseStream.Position;
                    try
                    {
                        pe.BaseStream.Position = rawOffset;
                        pe.BaseStream.Write(BitConverter.GetBytes(changedOffset));
                        Assert.That(X64ScalarWrapperStaticConstructorProof.Find(constructor), Is.Null,
                            "Fresh qualification requires a genuine boxed field offset translated to the unchanged unboxed layout.");
                        Assert.That(X64ScalarWrapperStaticConstructorProof.TryAuthenticate(constructor, out _), Is.False,
                            "Saved evidence cannot hide a changed raw layout.");
                    }
                    finally
                    {
                        pe.BaseStream.Position = rawOffset;
                        pe.BaseStream.Write(original);
                        pe.BaseStream.Position = position;
                    }
                    Assert.That(X64ScalarWrapperStaticConstructorProof.TryAuthenticate(constructor, out _), Is.True);
                }

                foreach (var methodName in methods)
                {
                    var method = type.Methods.Single(candidate =>
                        candidate.Name == methodName);
                    Assert.That(X64ScalarWrapperTailCallProof.Find(method),
                        Is.Not.Null, $"{name}.{methodName}");
                }

                var previousLimit = MethodAnalysisContext.MaxMethodSizeBytes;
                try
                {
                    MethodAnalysisContext.MaxMethodSizeBytes = 16;
                    foreach (var methodName in methods)
                        Assert.That(X64ScalarWrapperTailCallProof.Find(
                            type.Methods.Single(candidate => candidate.Name == methodName)),
                            Is.Null, $"{name}.{methodName} needs an emitted initializer");
                }
                finally { MethodAnalysisContext.MaxMethodSizeBytes = previousLimit; }

                var body = X86Utils.Iterate(constructor).ToArray();
                Assert.That(X64ScalarWrapperStaticConstructorProof.TryProveShape(body),
                    Is.Not.Null, name);
                var changed = body.ToArray();
                changed[8].MemoryBase = Iced.Intel.Register.RAX;
                Assert.That(X64ScalarWrapperStaticConstructorProof.TryProveShape(changed),
                    Is.Null, $"{name} writes through a different pointer");

                try
                {
                    proof!.StaticField.OverrideOffset = 4;
                    Assert.That(X64ScalarWrapperStaticConstructorProof.Find(constructor),
                        Is.Null, $"{name} static field identity changed");
                    foreach (var methodName in methods)
                        Assert.That(X64ScalarWrapperTailCallProof.Find(
                            type.Methods.Single(candidate => candidate.Name == methodName)),
                            Is.Null, $"{name}.{methodName} requires its initializer");
                }
                finally { proof!.StaticField.OverrideOffset = null; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExplicitConstructorIsNotAdmittedByTheBeforeFieldInitProof()
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_SCALAR_WRAPPER_EXPLICIT_CCTOR_NEGATIVE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_WRAPPER_EXPLICIT_CCTOR_NEGATIVE_INPUT to the neutral explicit-constructor player input.");

        var binary = Path.Combine(input!, "GameAssembly.dll");
        var metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var type = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ScalarWrapperCctorFixture")!.Types
                .Single(candidate => candidate.Name == "ExplicitCell");
            Assert.That((type.Attributes & TypeAttributes.BeforeFieldInit) != 0,
                Is.False);
            Assert.That(X64ScalarWrapperStaticConstructorProof.Find(
                type.Methods.Single(candidate => candidate.Name == ".cctor")),
                Is.Null);
            Assert.That(X64ScalarWrapperTailCallProof.Find(
                type.Methods.Single(candidate => candidate.Name == "ToString")),
                Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
