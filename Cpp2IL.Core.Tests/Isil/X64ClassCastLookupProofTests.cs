using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64ClassCastLookupProofTests
{
    [Test]
    public void ExactPlayerProvesClassCastAndRejectsCorruptedNativeBody()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT to the neutral exact player input.");

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
            var lookup = app.GetAssemblyByName("RuntimeCastConcatFixture")!.Types
                .Single(type => type.Name == "ResolverBase").Methods
                .Single(method => method.Name == "Lookup");
            lookup.EnsureRawBytes();
            var native = X86Utils.Iterate(lookup).ToArray();
            Assert.That(native.Length, Is.EqualTo(37),
                "This redistributable build is the 37-instruction register allocation.");

            var shape = X64ClassCastLookupProof.TryProveShape(native);
            var proof = X64ClassCastLookupProof.Find(lookup, native);
            Assert.Multiple(() =>
            {
                Assert.That(shape, Is.Not.Null);
                Assert.That(shape!.FieldOffset, Is.EqualTo(16));
                Assert.That(proof, Is.Not.Null);
                Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup, shape),
                    Is.Not.Null);
                Assert.That(proof!.SourceField.Name, Is.EqualTo("Current"));
                Assert.That(proof.TargetType.Name, Is.EqualTo("DerivedNode"));
            });

            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(lookup.UnderlyingPointer,
                native[^1].NextIP);
            Assert.Multiple(() =>
            {
                Assert.That(region.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(region.Start, Is.EqualTo(lookup.UnderlyingPointer));
                Assert.That(region.End, Is.EqualTo(native[^1].NextIP));
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True);
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    new byte[] { 0x06, 0x42, 0x02, 0x30 }), Is.False);
                Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                        shape!.TypeInfoSlot)?.Type, Is.EqualTo(MetadataUsageType.TypeInfo));
                Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe,
                    unwind, shape!.Initializer), Is.True);
            });

            var neighbor = lookup.DeclaringType!.Fields
                .Single(field => field.Name == "Neighbor");
            Assert.That(neighbor.FieldType.Name, Is.EqualTo("Int32"));
            var wrongFieldShape = shape! with { FieldOffset = neighbor.Offset };
            Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup,
                wrongFieldShape), Is.Null,
                "A same-width native load cannot bind to an Int32 neighbor field.");
            var wrongField = native.ToArray();
            wrongField[8].MemoryDisplacement64 = (ulong)neighbor.Offset;
            Assert.That(X64ClassCastLookupProof.TryProveShape(wrongField), Is.Not.Null);
            Assert.That(X64ClassCastLookupProof.Find(lookup, wrongField), Is.Null,
                "A decoded mutation cannot substitute for the file-backed body.");

            var compose = app.GetAssemblyByName("RuntimeCastConcatFixture")!.Types
                .Single(type => type.Name == "Resolver").Methods
                .Single(method => method.Name == "Compose");
            compose.EnsureRawBytes();
            var literalSlot = X86Utils.Iterate(compose).ToArray()[5].IPRelativeMemoryAddress;
            Assert.That(app.LibCpp2IlContext.GetLiteralGlobalByAddress(literalSlot)?.Type,
                Is.EqualTo(MetadataUsageType.StringLiteral));
            Assert.That(X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                unwind, literalSlot, 8), Is.True,
                "This negative reaches the metadata-usage check after writable-slot checks.");
            Assert.That(literalSlot > shape!.Flag ||
                shape.Flag - literalSlot >= 8, Is.True,
                "This negative must not be rejected by the slot/flag overlap check.");
            Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup,
                shape with { TypeInfoSlot = literalSlot }), Is.Null,
                "A StringLiteral slot cannot bind as a target class TypeInfo.");
            var wrongUsage = native.ToArray();
            wrongUsage[5].MemoryDisplacement64 = literalSlot;
            wrongUsage[15].MemoryDisplacement64 = literalSlot;
            Assert.That(X64ClassCastLookupProof.TryProveShape(wrongUsage), Is.Not.Null);
            Assert.That(X64ClassCastLookupProof.Find(lookup, wrongUsage), Is.Null,
                "A decoded mutation cannot substitute for the file-backed body.");

            var wrongInitializer = native[16].IP;
            Assert.That(wrongInitializer, Is.Not.EqualTo(shape!.Initializer));
            Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup,
                shape with { Initializer = wrongInitializer }), Is.Null,
                "The native helper's target must bind to the proved runtime initializer.");

            foreach (var mutation in new[]
                     {
                         "metadata-branch", "flag-store", "field-width", "field-register",
                         "null-branch", "target-depth-width", "target-depth-offset",
                         "object-depth-register", "unsigned-depth-branch", "hierarchy-offset",
                         "hierarchy-index", "hierarchy-target", "hierarchy-branch",
                         "success-cmov", "failure-cmov", "extra-effect", "prefix",
                         "code-size", "body-boundary", "helper-target", "slot-alias",
                     })
            {
                var changed = native.ToArray();
                var index = mutation switch
                {
                    "metadata-branch" => 4,
                    "flag-store" => 7,
                    "field-width" or "field-register" => 8,
                    "null-branch" => 10,
                    "target-depth-width" or "target-depth-offset" or "prefix" => 17,
                    "object-depth-register" => 18,
                    "unsigned-depth-branch" => 19,
                    "hierarchy-offset" => 20,
                    "hierarchy-index" or "hierarchy-target" => 21,
                    "hierarchy-branch" => 22,
                    "success-cmov" => 26,
                    "failure-cmov" => 33,
                    "extra-effect" => 25,
                    "code-size" => 1,
                    "body-boundary" => 36,
                    "helper-target" => 6,
                    _ => 5,
                };
                var instruction = changed[index];
                switch (mutation)
                {
                    case "metadata-branch": instruction.NearBranch64 = changed[9].IP; break;
                    case "flag-store": instruction.Immediate8 = 2; break;
                    case "field-width": instruction.Code = Code.Mov_r32_rm32; break;
                    case "field-register": instruction.MemoryBase = Register.RCX; break;
                    case "null-branch": instruction.NearBranch64 = changed[16].IP; break;
                    case "target-depth-width": instruction.Code = Code.Movzx_r32_rm16; break;
                    case "target-depth-offset": instruction.MemoryDisplacement64++; break;
                    case "object-depth-register": instruction.Op1Register = Register.AL; break;
                    case "unsigned-depth-branch": instruction.Code = Code.Jae_rel8_64; break;
                    case "hierarchy-offset": instruction.MemoryDisplacement64++; break;
                    case "hierarchy-index": instruction.MemoryIndex = Register.RDX; break;
                    case "hierarchy-target": instruction.Op1Register = Register.RDX; break;
                    case "hierarchy-branch": instruction.NearBranch64 = changed[23].IP; break;
                    case "success-cmov": instruction.Code = Code.Cmove_r64_rm64; break;
                    case "failure-cmov": instruction.Code = Code.Cmove_r64_rm64; break;
                    case "extra-effect": instruction.Code = Code.Inc_rm8; break;
                    case "prefix": instruction.HasRepPrefix = true; break;
                    case "code-size": instruction.CodeSize = CodeSize.Code32; break;
                    case "body-boundary": instruction.IP++; break;
                    case "helper-target": instruction.NearBranch64 = changed[16].IP; break;
                    case "slot-alias": instruction.MemoryDisplacement64 += 8; break;
                }
                changed[index] = instruction;
                if (mutation == "helper-target")
                {
                    Assert.That(X64ClassCastLookupProof.TryProveShape(changed), Is.Not.Null);
                    Assert.That(X64ClassCastLookupProof.Find(lookup, changed), Is.Null,
                        "A decoded mutation cannot substitute for the file-backed body.");
                }
                else
                    Assert.That(X64ClassCastLookupProof.TryProveShape(changed), Is.Null,
                        mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
