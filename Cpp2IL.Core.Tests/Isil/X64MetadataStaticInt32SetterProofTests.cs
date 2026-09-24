using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64MetadataStaticInt32SetterProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerAcceptsOnlyTheBoundedOwnFieldStore()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_STATIC_SCALAR_SETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STATIC_SCALAR_SETTER_FIXTURE_INPUT to the neutral player-input directory.");
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
            var owner = app.GetAssemblyByName("StaticScalarSetterFixture")!.Types
                .Single(type => type.Name == "StaticState");
            var method = owner.Methods.Single(candidate => candidate.Name == "Assign");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var evidence = X64MetadataStaticInt32SetterProof.Find(method, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Field.Name, Is.EqualTo("Value"));
            Assert.That(evidence.Field.Offset, Is.EqualTo(4));
            Assert.That(native[13].NextIP - method.UnderlyingPointer,
                Is.EqualTo(59UL));
            Assert.That(method.RawBytes.Length, Is.GreaterThanOrEqualTo(59));
            Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                evidence.TypeInfoSlot)?.Type, Is.EqualTo(MetadataUsageType.TypeInfo));
            Assert.That(X64MetadataStaticInt32SetterProof.TryProveShape(native.Take(14).ToArray()),
                Is.Not.Null);

            foreach (var defect in new[]
                     { "width", "source", "base", "offset", "guard", "branch" })
            {
                var changed = native.ToArray();
                switch (defect)
                {
                    case "width": changed[10].Code = Code.Mov_rm64_r64; break;
                    case "source": changed[10].Op1Register = Register.ECX; break;
                    case "base": changed[10].MemoryBase = Register.RAX; break;
                    case "offset": changed[10].MemoryDisplacement64 =
                        (ulong)int.MaxValue + 1; break;
                    case "guard": changed[2].Code = Code.Nopd; break;
                    case "branch": changed[4].NearBranch64 = native[9].IP; break;
                }
                Assert.That(X64MetadataStaticInt32SetterProof.TryProveShape(
                    changed.Take(14).ToArray()), Is.Null, defect);
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, changed),
                    Is.Null, defect);
            }

            var wrongHelper = native.ToArray();
            wrongHelper[6].NearBranch64 = native[13].IP;
            Assert.That(X64MetadataStaticInt32SetterProof.Find(method, wrongHelper),
                Is.Null, "changed metadata initializer target");

            var field = evidence.Field;
            var neighbor = owner.Fields.Single(candidate => candidate.Name == "After");
            try
            {
                field.OverrideFieldType = app.SystemTypes.SystemInt64Type;
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "changed field type");
            }
            finally { field.OverrideFieldType = null; }
            try
            {
                field.OverrideOffset = field.DefaultOffset + 1;
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "changed static field offset");
            }
            finally { field.OverrideOffset = null; }
            try
            {
                field.OverrideAttributes = field.DefaultAttributes |
                    FieldAttributes.HasFieldMarshal;
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "changed field declaration");
            }
            finally { field.OverrideAttributes = null; }
            var originalBackingAttributes = field.BackingData!.Attributes;
            try
            {
                field.BackingData.Attributes |= FieldAttributes.InitOnly;
                Assert.That(field.Attributes, Is.EqualTo(field.DefaultAttributes));
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "readonly static field outside a class constructor");
            }
            finally { field.BackingData.Attributes = originalBackingAttributes; }
            try
            {
                neighbor.OverrideOffset = field.DefaultOffset + 2;
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "overlapping neighboring field");
            }
            finally { neighbor.OverrideOffset = null; }

            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            var unrelated = app.SystemTypes.SystemObjectType.Methods.Single(candidate =>
                candidate.Name == ".ctor" && candidate.Parameters.Count == 0);
            aliases.Add(unrelated);
            try
            {
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "shared native address");
            }
            finally { aliases.Remove(unrelated); }

            var oldBitfield = owner.Definition!.Bitfield;
            try
            {
                owner.Definition.Bitfield |= 1u << 3;
                Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                    Is.Null, "class constructor effects");
            }
            finally { owner.Definition.Bitfield = oldBitfield; }

            var optionType = new InjectedTypeAnalysisContext(owner.DeclaringAssembly,
                "Unity.IL2CPP.CompilerServices", "Il2CppSetOptionAttribute",
                app.SystemTypes.SystemAttributeType,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            var optionConstructor = optionType.InjectMethodContext(".ctor",
                app.SystemTypes.SystemVoidType, MethodAttributes.Public);
            var option = new AnalyzedCustomAttribute(optionConstructor);
            foreach (var scope in new HasCustomAttributes[]
                     { method, owner, owner.DeclaringAssembly })
            {
                var original = scope.CustomAttributes;
                try
                {
                    scope.CustomAttributes = [option];
                    Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                        Is.Null, "changed IL2CPP output option");
                }
                finally { scope.CustomAttributes = original; }
            }
            Assert.That(X64MetadataStaticInt32SetterProof.Find(method, native),
                Is.Not.Null, "restored original evidence");

            var pe = (PE)app.Binary;
            var raw = checked((int)pe.MapVirtualAddressToRaw(native[10].IP, false));
            var changedBinary = File.ReadAllBytes(binary);
            Assert.That(changedBinary[raw], Is.EqualTo((byte)0x89));
            Assert.That(changedBinary[raw + 1], Is.EqualTo((byte)0x5A));
            changedBinary[raw + 1] = 0x4A; // mov dword [rdx+offset], ecx
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changedApp = Cpp2IlApi.CurrentAppContext!;
            var changedMethod = changedApp.GetAssemblyByName("StaticScalarSetterFixture")!
                .Types.Single(type => type.Name == "StaticState").Methods
                .Single(candidate => candidate.Name == "Assign");
            changedMethod.EnsureRawBytes();
            Assert.That(X64MetadataStaticInt32SetterProof.Find(changedMethod,
                X86Utils.Iterate(changedMethod).ToArray()), Is.Null,
                "the player-byte mutation changes the stored source register");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
