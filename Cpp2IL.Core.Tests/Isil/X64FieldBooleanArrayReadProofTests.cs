using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64FieldBooleanArrayReadProofTests
{
    [Test]
    public void ExactPlayerBindsThreeFieldsAndRejectsNearbyNativeShapes()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FIELD_BOOLEAN_ARRAY_READ_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_BOOLEAN_ARRAY_READ_FIXTURE_INPUT to the neutral player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("FieldBooleanArrayReadFixture")!.Types
                .Single(type => type.Name == "BooleanArrayOwner");
            var pe = (PE)app.Binary;
            foreach (var (name, fieldName, offset) in new[]
                     {
                         ("ReadFirst", "First", 24),
                         ("ReadSecond", "Second", 40),
                         ("ReadLate", "Late", 104)
                     })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                var body = X64Stack28BodyProof.Read(method, 14, 64);
                Assert.That(body, Is.Not.Null, name);
                Assert.That(X64FieldBooleanArrayReadProof.TryProveShape(body!, pe),
                    Is.EqualTo(offset), name);
                Assert.That(X64FieldBooleanArrayReadProof.Find(method)?.ArrayField.Name,
                    Is.EqualTo(fieldName), name);
                Assert.That(body![^1].NextIP - body[0].IP, Is.EqualTo(47), name);
            }

            var selected = owner.Methods.Single(candidate => candidate.Name == "ReadFirst");
            var native = X64Stack28BodyProof.Read(selected, 14, 64)!;
            foreach (var mutation in new[] { "receiver", "null branch", "length base",
                         "signed bounds", "bounds branch", "index extension", "element base",
                         "element offset", "element width", "compare literal",
                         "opposite predicate", "flag clobber", "missing trap",
                         "missing return", "extra instruction" })
            {
                var changed = native.ToArray();
                switch (mutation)
                {
                    case "receiver": changed[1].MemoryBase = Register.EDX; break;
                    case "null branch": changed[3].NearBranch64 = changed[13].IP; break;
                    case "length base": changed[4].MemoryBase = Register.RCX; break;
                    case "signed bounds": changed[5].Code = Code.Jge_rel8_64; break;
                    case "bounds branch": changed[5].NearBranch64 = changed[11].IP; break;
                    case "index extension": changed[6].Code = Code.Mov_r32_rm32; break;
                    case "element base": changed[7].MemoryBase = Register.RCX; break;
                    case "element offset": changed[7].MemoryDisplacement64++; break;
                    case "element width": changed[7].Code = Code.Cmp_rm32_imm8; break;
                    case "compare literal": changed[7].Immediate8 = 1; break;
                    case "opposite predicate": changed[8].Code = Code.Sete_rm8; break;
                    case "flag clobber": changed[8].Code = Code.Add_rm8_r8; break;
                    case "missing trap": changed[12].Code = Code.Nopd; break;
                    case "missing return": changed[10].Code = Code.Jmp_rel8_64; break;
                    case "extra instruction": changed = [..changed, changed[^1]]; break;
                }
                Assert.That(X64FieldBooleanArrayReadProof.TryProveShape(changed, pe),
                    Is.Null, mutation);
            }

            var first = owner.Fields.Single(field => field.Name == "First");
            var between = owner.Fields.Single(field => field.Name == "Between");
            try
            {
                between.OverrideOffset = first.Offset;
                Assert.That(X64FieldBooleanArrayReadProof.Find(selected), Is.Null,
                    "overlapping fields invalidate the native field binding");
            }
            finally { between.OverrideOffset = null; }

            try
            {
                selected.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64FieldBooleanArrayReadProof.Find(selected), Is.Null,
                    "the SETNE return requires an unchanged Boolean signature");
            }
            finally { selected.OverrideReturnType = null; }

            var interior = selected.UnderlyingPointer + 5;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            try
            {
                app.MethodsByAddress.Add(interior, [selected]);
                Assert.That(X64FieldBooleanArrayReadProof.Find(selected), Is.Null,
                    "another managed entry invalidates the complete body boundary");
            }
            finally { app.MethodsByAddress.Remove(interior); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void FieldlessConstructedBaseReadRequiresUniqueFieldAndCallerBinding()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_FIXTURE_INPUT to the neutral player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(
                Path.Combine(directory, "GameAssembly.dll"),
                Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
                    "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!.Types
                .Single(type => type.Name == "GenericBooleanArrayState");
            var method = owner.Methods.Single(candidate => candidate.Name == "ReadAt");
            var body = X64Stack28BodyProof.Read(method, 14, 64);
            Assert.That(body, Is.Not.Null);
            Assert.That(body![^1].NextIP - body[0].IP, Is.EqualTo(47));
            Assert.That(X64FieldBooleanArrayReadProof.Find(method)?.ArrayField.Name,
                Is.EqualTo("Values"));

            var array = owner.Fields.Single(field => field.Name == "Values");
            var before = owner.Fields.Single(field => field.Name == "Before");
            try
            {
                before.OverrideOffset = array.Offset;
                Assert.That(X64FieldBooleanArrayReadProof.Find(method), Is.Null,
                    "an overlapping sibling invalidates the constructed-base layout");
            }
            finally { before.OverrideOffset = null; }

            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            aliases.Add(method);
            try
            {
                Assert.That(X64FieldBooleanArrayReadProof.Find(method), Is.Null,
                    "duplicate folded bindings are not uniquely identified");
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }

        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void VolatileConstructedBaseReadHasADistinctNativeBody()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_VOLATILE_CONTROL_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTED_BASE_BOOLEAN_ARRAY_VOLATILE_CONTROL_INPUT to the neutral volatile player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(
                Path.Combine(directory, "GameAssembly.dll"),
                Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
                    "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var owner = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConstructedBaseBooleanArrayFixture")!.Types
                .Single(type => type.Name == "VolatileBooleanArrayState");
            var method = owner.Methods.Single(candidate => candidate.Name == "ReadAt");
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.Length, Is.EqualTo(62));
            Assert.That(X64FieldBooleanArrayReadProof.Find(method), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
