using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86ConstructedBaseBooleanArrayFixtureTests
{
    [Test]
    public void ParameterStoreRequiresTheConstructedBaseToRemainFieldless()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT to the neutral player-input directory.");
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
            var owner = app.GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!
                .Types.Single(type => type.Name == "GenericBooleanArrayState");
            var constructed = (GenericInstanceTypeAnalysisContext)owner.BaseType!;
            var method = owner.Methods.Single(candidate => candidate.Name == "SetAt");
            var field = owner.Fields.Single(candidate => candidate.Name == "Values");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(native, Has.Length.EqualTo(13));
                Assert.That(X64FieldParameterBooleanArrayStoreProof
                    .TryProveShape(native, (PE)app.Binary)?.FieldOffset, Is.EqualTo(field.Offset));
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method)?.ArrayField,
                    Is.SameAs(field));
                Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[10].IP));
                Assert.That(native[5].NearBranchTarget, Is.EqualTo(native[12].IP));
                Assert.That(native[7].Op1Register, Is.EqualTo(Iced.Intel.Register.R8L));
            });

            var injected = new InjectedFieldAnalysisContext("Blocked", app.SystemTypes.SystemInt32Type,
                FieldAttributes.Public, constructed.GenericType, 16);
            try
            {
                constructed.GenericType.Fields.Add(injected);
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null);
            }
            finally { constructed.GenericType.Fields.Remove(injected); }

            Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method)?.ArrayField,
                Is.SameAs(field));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [TestCase("SetTrue", true)]
    [TestCase("SetFalse", false)]
    public void FieldlessConstructedBaseRequiresUnchangedNonoverlappingLayout(string name, bool value)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT to the neutral player-input directory.");
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
            var assembly = app.GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "GenericBooleanArrayState");
            var constructed = (GenericInstanceTypeAnalysisContext)owner.BaseType!;
            var baseConstructor = constructed.GenericType.Methods.Single(candidate => candidate.Name == ".ctor");
            var ownerConstructor = owner.Methods.Single(candidate => candidate.Name == ".ctor");
            baseConstructor.EnsureRawBytes();
            ownerConstructor.EnsureRawBytes();
            var baseNative = X86Utils.Iterate(baseConstructor).ToArray();
            var ownerUnwind = X64UnwindProof.ForApplication(app)!
                .ClassifySpan(ownerConstructor.UnderlyingPointer,
                    ownerConstructor.UnderlyingPointer + 1);
            var field = owner.Fields.Single(candidate => candidate.Name == "Values");
            var before = owner.Fields.Single(candidate => candidate.Name == "Before");
            var after = owner.Fields.Single(candidate => candidate.Name == "After");
            var access = new FieldReference(field,
                new LocalVariable("owner", new Cpp2IL.Core.ISIL.Register(null, "rcx"), owner),
                checked((int)field.Offset));

            Assert.Multiple(() =>
            {
                Assert.That(constructed.Fields, Is.Empty);
                Assert.That(constructed.BaseType, Is.Null);
                Assert.That(constructed.GenericType.Fields.Where(candidate => !candidate.IsStatic), Is.Empty);
                Assert.That(constructed.GenericType.GenericParameters, Has.Count.EqualTo(1));
                Assert.That(constructed.GenericArguments, Has.Count.EqualTo(1));
                Assert.That(constructed.GenericType.BaseType, Is.SameAs(app.SystemTypes.SystemObjectType));
                Assert.That(field.FieldType,
                    Is.TypeOf<SzArrayTypeAnalysisContext>());
                Assert.That(((SzArrayTypeAnalysisContext)field.FieldType).ElementType,
                    Is.SameAs(app.SystemTypes.SystemBooleanType));
                Assert.That(new[] { before.Offset, field.Offset, after.Offset },
                    Is.EqualTo(new[] { 16, 24, 32 }));
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access), Is.False);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access),
                    Is.True);
                Assert.That(baseConstructor.RawBytes.Length, Is.EqualTo(7));
                Assert.That(app.MethodsByAddress[baseConstructor.UnderlyingPointer].Count,
                    Is.GreaterThan(1));
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative),
                    Is.SameAs(app.SystemTypes.SystemObjectType.Methods
                        .Single(candidate => candidate.Name == ".ctor")));
                Assert.That(ownerConstructor.RawBytes.Length, Is.GreaterThan(64));
                Assert.That(ownerUnwind.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(ownerUnwind.End - ownerUnwind.Start, Is.EqualTo(57));
                Assert.That(X64GenericBaseConstructorProof.Find(ownerConstructor)?.BaseConstructor.BaseMethodContext,
                    Is.SameAs(baseConstructor));
            });

            var method = owner.Methods.Single(candidate => candidate.Name == name);
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(native, (PE)app.Binary),
                Is.Not.Null, $"{name} at 0x{method.UnderlyingPointer:X}");
            var evidence = X86FieldBooleanArrayLiteralStoreProof.Find(method, native);
            Assert.That(evidence?.ArrayField, Is.SameAs(field));
            Assert.That(evidence?.Value, Is.EqualTo(value));

            var injected = new InjectedFieldAnalysisContext("Blocked", app.SystemTypes.SystemInt32Type,
                FieldAttributes.Public, constructed.GenericType, 16);
            try
            {
                constructed.GenericType.Fields.Add(injected);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access),
                    Is.False);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative), Is.Null);

                injected.OverrideAttributes = FieldAttributes.Public | FieldAttributes.Static;
                Assert.That(injected.IsStatic, Is.True);
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access),
                    Is.False);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative), Is.Null);
            }
            finally
            {
                injected.OverrideAttributes = null;
                constructed.GenericType.Fields.Remove(injected);
            }

            var baseDefinition = constructed.GenericType.Definition!;
            var originalBitfield = baseDefinition.Bitfield;
            try
            {
                baseDefinition.Bitfield |= 1u << 3;
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access),
                    Is.False);
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative), Is.Null);
            }
            finally { baseDefinition.Bitfield = originalBitfield; }

            var aliases = app.MethodsByAddress[baseConstructor.UnderlyingPointer];
            var bindingIndex = aliases.FindIndex(candidate => ReferenceEquals(candidate, baseConstructor));
            Assert.That(bindingIndex, Is.GreaterThanOrEqualTo(0));
            try
            {
                aliases.RemoveAt(bindingIndex);
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative), Is.Null);
            }
            finally { aliases.Insert(bindingIndex, baseConstructor); }

            try
            {
                before.OverrideOffset = field.Offset;
                Assert.That(NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access),
                    Is.False);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
            }
            finally { before.OverrideOffset = null; }

            try
            {
                after.OverrideOffset = after.DefaultOffset + 8;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
            }
            finally { after.OverrideOffset = null; }

            try
            {
                field.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
            }
            finally { field.OverrideFieldType = null; }

            try
            {
                constructed.GenericType.OverrideAttributes =
                    (constructed.GenericType.DefaultAttributes & ~TypeAttributes.LayoutMask) |
                    TypeAttributes.ExplicitLayout;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
                Assert.That(X64ObjectConstructorThunkProof.Find(baseConstructor, baseNative), Is.Null);
            }
            finally { constructed.GenericType.OverrideAttributes = null; }

            try
            {
                ownerConstructor.ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X64GenericBaseConstructorProof.Find(ownerConstructor), Is.Null);
            }
            finally { ownerConstructor.ImplAttributes = ownerConstructor.DefaultImplAttributes; }

            var wrongStore = native.ToArray();
            wrongStore[7].Immediate8 = 2;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongStore, (PE)app.Binary), Is.Null);
            var wrongBounds = native.ToArray();
            wrongBounds[5].Code = Code.Ja_rel8_64;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongBounds, (PE)app.Binary), Is.Null);
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void OverlongConstructorRequiresInt3PaddingAfterItsUnwindEnd()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT to the neutral player-input directory.");
        var binaryPath = Path.Combine(directory!, "GameAssembly.dll");
        var metadataPath = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        var binary = File.ReadAllBytes(binaryPath);
        var metadata = File.ReadAllBytes(metadataPath);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var constructor = app.GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!.Types
                .Single(type => type.Name == "GenericBooleanArrayState").Methods
                .Single(candidate => candidate.Name == ".ctor");
            constructor.EnsureRawBytes();
            var span = X64UnwindProof.ForApplication(app)!
                .ClassifySpan(constructor.UnderlyingPointer, constructor.UnderlyingPointer + 1);
            Assert.That(span.End - span.Start, Is.EqualTo(57));
            Assert.That(constructor.RawBytes.Length, Is.GreaterThan(64));
            Assert.That(X64GenericBaseConstructorProof.Find(constructor), Is.Not.Null);
            var padding = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(span.End, false));
            Assert.That(binary[padding], Is.EqualTo(0xCC));

            var altered = (byte[])binary.Clone();
            altered[padding] = 0x90;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(altered, metadata, UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!.Types
                .Single(type => type.Name == "GenericBooleanArrayState").Methods
                .Single(candidate => candidate.Name == ".ctor");
            Assert.That(X64GenericBaseConstructorProof.Find(changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
