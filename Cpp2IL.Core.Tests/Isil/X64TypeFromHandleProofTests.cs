using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64TypeFromHandleProofTests
{
    [Test]
    public void ExactPlayerBindsTypeAndClassInitWithoutGuessing()
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
            var assembly = app.GetAssemblyByName("RuntimeCastConcatFixture")!;
            var method = assembly.Types.Single(type =>
                type.Name == "MetadataControls").Methods.Single(candidate =>
                candidate.Name == "TargetType");
            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            Assert.That(body.Length, Is.EqualTo(19));

            var shape = X64TypeFromHandleProof.TryProveShape(body);
            Assert.That(shape, Is.Not.Null);
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                body[^1].NextIP);
            Assert.Multiple(() =>
            {
                Assert.That(region.Kind,
                    Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(region.Start, Is.EqualTo(method.UnderlyingPointer));
                Assert.That(region.End, Is.EqualTo(body[^1].NextIP));
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True);
                Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                    shape!.TypeSlot)?.Type, Is.EqualTo(MetadataUsageType.Type));
                Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                    shape.TypeInfoSlot)?.Type,
                    Is.EqualTo(MetadataUsageType.TypeInfo));
                Assert.That(shape.ClassInitializer,
                    Is.EqualTo(app.GetOrCreateKeyFunctionAddresses()
                        .il2cpp_runtime_class_init_export));
                Assert.That(app.SystemTypes.SystemTypeType.Definition?.HasCctor,
                    Is.True);
            });

            var bound = X64TypeFromHandleProof.BindProvedShape(method, shape!);
            Assert.That(bound, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(bound!.TargetType.FullName,
                    Is.EqualTo("RuntimeCastConcatFixture.DerivedNode"));
                Assert.That(bound.GetTypeFromHandle.DeclaringType,
                    Is.SameAs(app.SystemTypes.SystemTypeType));
                Assert.That(bound.GetTypeFromHandle.Name,
                    Is.EqualTo("GetTypeFromHandle"));
                Assert.That(X64TypeFromHandleProof.Find(method, body), Is.Not.Null);
            });

            var tail = bound!.GetTypeFromHandle;
            var tailDefinition = tail.Definition!;
            var originalFlags = tailDefinition.flags;
            Assert.That(tail.OverrideAttributes, Is.Null);
            try
            {
                tailDefinition.flags = (ushort)(((MethodAttributes)originalFlags &
                    ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private);
                Assert.Multiple(() =>
                {
                    Assert.That(tail.Visibility, Is.EqualTo(MethodAttributes.Private));
                    Assert.That(tail.Attributes, Is.EqualTo(tail.DefaultAttributes));
                });
                Assert.That(X64TypeFromHandleProof.BindProvedShape(method,
                    shape!), Is.Null,
                    "The generated source cannot call a private runtime method.");
            }
            finally { tailDefinition.flags = originalFlags; }
            Assert.That(X64TypeFromHandleProof.BindProvedShape(method, shape!),
                Is.Not.Null);

            var compose = assembly.Types.Single(type =>
                type.Name == "Resolver").Methods.Single(candidate =>
                candidate.Name == "Compose");
            compose.EnsureRawBytes();
            var literalSlot = X86Utils.Iterate(compose).ToArray()[5]
                .IPRelativeMemoryAddress;
            Assert.That(app.LibCpp2IlContext.GetLiteralGlobalByAddress(
                literalSlot)?.Type,
                Is.EqualTo(MetadataUsageType.StringLiteral));
            Assert.That(X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                unwind, literalSlot, 8), Is.True);
            Assert.That(X64TypeFromHandleProof.BindProvedShape(method,
                shape! with { TypeSlot = literalSlot }), Is.Null,
                "A StringLiteral slot cannot be rebound as a Type token.");

            var lookup = assembly.Types.Single(type =>
                type.Name == "ResolverBase").Methods.Single(candidate =>
                candidate.Name == "Lookup");
            lookup.EnsureRawBytes();
            var derivedClassSlot = X86Utils.Iterate(lookup).ToArray()[5]
                .IPRelativeMemoryAddress;
            Assert.That(app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                derivedClassSlot)?.Type, Is.EqualTo(MetadataUsageType.TypeInfo));
            Assert.That(X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                unwind, derivedClassSlot, 8), Is.True);
            Assert.That(X64TypeFromHandleProof.BindProvedShape(method,
                shape! with { TypeInfoSlot = derivedClassSlot }), Is.Null,
                "The class-init receiver must be System.Type, not another TypeInfo.");

            foreach (var changed in new[]
                     {
                         shape! with { MetadataInitializer = body[13].IP },
                         shape! with { ClassInitializer = body[18].IP },
                         shape! with { GetTypeFromHandle = body[18].IP },
                     })
                Assert.That(X64TypeFromHandleProof.BindProvedShape(method,
                    changed), Is.Null);

            foreach (var mutation in new[]
                     {
                         "flag-branch", "metadata-order", "class-check-width",
                         "class-check-offset", "class-branch", "hidden-argument",
                         "tail-kind", "prefix", "body-boundary",
                     })
            {
                var changed = body.ToArray();
                var index = mutation switch
                {
                    "flag-branch" => 3,
                    "metadata-order" => 6,
                    "class-check-width" or "class-check-offset" => 11,
                    "class-branch" => 12,
                    "hidden-argument" => 14,
                    "tail-kind" => 18,
                    "prefix" => 4,
                    _ => 18,
                };
                var instruction = changed[index];
                switch (mutation)
                {
                    case "flag-branch":
                        instruction.Code = Code.Je_rel8_64; break;
                    case "metadata-order":
                        instruction.MemoryDisplacement64 =
                            body[4].IPRelativeMemoryAddress; break;
                    case "class-check-width":
                        instruction.Code = Code.Cmp_rm8_imm8; break;
                    case "class-check-offset":
                        instruction.MemoryDisplacement64++; break;
                    case "class-branch":
                        instruction.NearBranch64 = body[15].IP; break;
                    case "hidden-argument":
                        instruction.Op0Register = Register.ECX; break;
                    case "tail-kind":
                        instruction.Code = Code.Call_rel32_64; break;
                    case "prefix":
                        instruction.HasRepPrefix = true; break;
                    case "body-boundary":
                        instruction.IP++; break;
                }
                changed[index] = instruction;
                Assert.That(X64TypeFromHandleProof.TryProveShape(changed),
                    Is.Null, mutation);
            }

            var retargeted = body.ToArray();
            retargeted[18].NearBranch64 = body[14].IP;
            Assert.That(X64TypeFromHandleProof.TryProveShape(retargeted),
                Is.Not.Null);
            Assert.That(X64TypeFromHandleProof.Find(method, retargeted), Is.Null,
                "A supplied decoded body must match the PE-backed bytes.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
