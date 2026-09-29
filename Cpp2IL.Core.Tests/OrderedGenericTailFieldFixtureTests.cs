using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class OrderedGenericTailFieldFixtureTests
{
    [Test]
    public void OriginalLaterValueStorageDoesNotObscureAnEarlierInt32Field()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ORDERED_GENERIC_TAIL_FIELD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ORDERED_GENERIC_TAIL_FIELD_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("OrderedGenericTailFieldFixture")!.Types.Single(type => type.Name == "State");
            var value = owner.Fields.Single(field => field.Name == "Value");
            var later = owner.Fields.Single(field => field.Name == "Later");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            var getter = owner.Methods.Single(method => method.Name == "ReadValue");
            var receiver = new LocalVariable("receiver", new Register(null, "rcx"), owner);
            var access = new FieldReference(value, receiver, value.Offset);
            Assert.That(later.FieldType, Is.InstanceOf<GenericInstanceTypeAnalysisContext>());
            Assert.That(later.FieldType.IsValueType, Is.True);
            Assert.That(TypeSizes.UnboxedSize(later.FieldType, app.Binary.PointerSizeBytes), Is.EqualTo(16),
                "The original closed integer substitution now has independently proved sequential storage.");
            Assert.That(later.Offset, Is.GreaterThanOrEqualTo(value.Offset + 4));
            Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32), Is.True);
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            Assert.That(X64FoldedInt32ConstructorProof.Find(constructor, native), Is.Not.Null,
                "The layout bound enables an existing independently complete native constructor proof.");

            void Reject(string label, Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32), Is.False, label);
                    Assert.That(X64FoldedInt32ConstructorProof.Find(constructor, native), Is.Null, label);
                }
                finally { restore(); }
                Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32), Is.True, label + " restored");
            }

            var laterData = later.BackingData!;
            var offset = laterData.FieldOffset;
            var valueData = value.BackingData!;
            var valueOffset = valueData.FieldOffset;
            Reject("unknown earlier extent beyond the object header", () =>
            {
                valueData.FieldOffset = offset + 8;
                access.Offset = valueData.FieldOffset;
            }, () => { valueData.FieldOffset = valueOffset; access.Offset = valueOffset; });
            Reject("unknown preceding extent", () => laterData.FieldOffset = value.Offset - 1, () => laterData.FieldOffset = offset);
            Reject("unknown same-start extent", () => laterData.FieldOffset = value.Offset, () => laterData.FieldOffset = offset);
            Reject("unknown intersecting extent", () => laterData.FieldOffset = value.Offset + 3, () => laterData.FieldOffset = offset);
            Reject("changed later offset", () => later.OverrideOffset = offset + 8, () => later.OverrideOffset = null);
            Reject("retargeted later type", () => later.OverrideFieldType = app.SystemTypes.SystemInt32Type, () => later.OverrideFieldType = null);
            Reject("changed later name", () => later.OverrideName = later.DefaultName + "Changed", () => later.OverrideName = null);
            Reject("instance hidden as static", () => later.OverrideAttributes = later.DefaultAttributes | FieldAttributes.Static, () => later.OverrideAttributes = null);
            var attributes = laterData.Attributes;
            Reject("later native marshal", () => laterData.Attributes |= FieldAttributes.HasFieldMarshal, () => laterData.Attributes = attributes);
            var rawType = laterData.Field.RawFieldType!;
            var modifiers = rawType.NumMods;
            Reject("later modifiers", () => rawType.NumMods = 1, () => rawType.NumMods = modifiers);
            var byref = rawType.Byref;
            Reject("later byref storage", () => rawType.Byref = 1, () => rawType.Byref = byref);
            var pinned = rawType.Pinned;
            Reject("later pinned storage", () => rawType.Pinned = 1, () => rawType.Pinned = pinned);
            var pe = (PE)app.Binary;
            var sizePointer = pe.TypeDefinitionSizePointers[owner.Definition!.TypeIndex!.Value];
            var sizeOffset = pe.MapVirtualAddressToRaw(sizePointer);
            var sizeBytes = pe.GetRawBinaryContent().Slice(checked((int)sizeOffset), 4).ToArray();
            void WriteSize(byte[] bytes)
            {
                var position = pe.BaseStream.Position;
                try
                {
                    pe.BaseStream.Position = sizeOffset;
                    pe.BaseStream.Write(bytes, 0, bytes.Length);
                }
                finally { pe.BaseStream.Position = position; }
            }
            Reject("accessed field beyond native instance", () => WriteSize(BitConverter.GetBytes((uint)(value.Offset + 3))),
                () => WriteSize(sizeBytes));
            Reject("explicit owner layout", () => owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout, () => owner.OverrideAttributes = null);
            Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 64), Is.False);
            Assert.That(NarrowFieldEqualityProof.HasUnchangedEnum32FieldLayout(access), Is.False,
                "The explicit enum entry must not manufacture enum identity for an Int32 field.");
            foreach (var method in new[] { constructor, getter })
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, method.Name);
                Assert.That(() => IlGenerator.GenerateIl(method, method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!),
                    Throws.Nothing, method.Name);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
