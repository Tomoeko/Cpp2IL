using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64OwnerArrayArgumentTailFixtureTests
{
    private const string InputVariable = "CPP2IL_ARRAY_ELEMENT_ARGUMENT_TAIL_INPUT";

    [Test]
    public void ExactPlayerBindsBothMethodsAndRejectsChangedMetadata()
    {
        var directory = RequireInput();
        Initialize(directory);
        try
        {
            var (forward, consume, items, value, counter, last) = Fixture();
            var forwardEvidence = X64OwnerArrayArgumentTailProof.Find(forward);
            Assert.That(forwardEvidence, Is.Not.Null);
            Assert.That(forwardEvidence!.ArrayField, Is.SameAs(items));
            Assert.That(forwardEvidence.ArgumentField, Is.SameAs(value));
            Assert.That(forwardEvidence.Target, Is.SameAs(consume));
            var consumeEvidence = X64OwnerIncrementReferenceStoreProof.Find(consume);
            Assert.That(consumeEvidence, Is.Not.Null);
            Assert.That(consumeEvidence!.CounterField, Is.SameAs(counter));
            Assert.That(consumeEvidence.ReferenceField, Is.SameAs(last));

            try
            {
                items.OverrideOffset = items.DefaultOffset + 8;
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
            }
            finally { items.OverrideOffset = null; }
            try
            {
                value.OverrideOffset = value.DefaultOffset + 8;
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
            }
            finally { value.OverrideOffset = null; }
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("ArrayElementArgumentTailFixture")!;
            var rawElement = items.BackingData!.Field.RawFieldType!.GetEncapsulatedType();
            var originalClassIndex = rawElement.Data.Dummy;
            var wrongClassIndex = assembly.Types.Single(type => type.Name == "ValueBox")
                .Definition!.RawType.Data.Dummy;
            Assert.That(wrongClassIndex, Is.Not.EqualTo(originalClassIndex));
            try
            {
                rawElement.Data.Dummy = wrongClassIndex;
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
            }
            finally { rawElement.Data.Dummy = originalClassIndex; }
            try
            {
                counter.OverrideOffset = counter.DefaultOffset + 4;
                Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null);
            }
            finally { counter.OverrideOffset = null; }
            try
            {
                last.OverrideOffset = last.DefaultOffset + 8;
                Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null);
            }
            finally { last.OverrideOffset = null; }

            try
            {
                consume.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
                Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null);
            }
            finally { consume.OverrideReturnType = null; }
            var parameter = consume.Parameters.Single();
            try
            {
                parameter.OverrideParameterType = app.SystemTypes.SystemObjectType;
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
                Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null);
            }
            finally { parameter.OverrideParameterType = null; }

            var targetBindings = app.MethodsByAddress[consume.UnderlyingPointer];
            try
            {
                targetBindings.Add(forward);
                Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null);
                Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null);
            }
            finally { targetBindings.RemoveAt(targetBindings.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactPlayerRejectsNativeRootLeafAndPaddingMutations()
    {
        var directory = RequireInput();
        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Initialize(directory);
        List<(string Method, int Offset)> changes;
        byte[] image;
        byte[] metadataBytes;
        try
        {
            var (forward, consume, _, _, _, _) = Fixture();
            var pe = (PE)Cpp2IlApi.CurrentAppContext!.Binary;
            var unwind = X64UnwindProof.ForApplication(Cpp2IlApi.CurrentAppContext!)!;
            var forwardBody = X64Stack28BodyProof.Read(forward, 17, 80);
            var consumeBody = X64NativeInstructionReader.Read(pe, unwind,
                consume.UnderlyingPointer, 4, 32);
            Assert.That(forwardBody, Is.Not.Null);
            Assert.That(consumeBody, Is.Not.Null);
            changes = new List<(string Method, int Offset)>();
            foreach (var index in new[] { 1, 7, 10, 13, 14, 16 })
                changes.Add(("Forward", checked((int)pe.MapVirtualAddressToRaw(
                    forwardBody![index].NextIP - 1, false))));
            foreach (var index in new[] { 0, 1, 2, 3 })
                changes.Add(("Consume", checked((int)pe.MapVirtualAddressToRaw(
                    consumeBody![index].NextIP - 1, false))));
            changes.Add(("Consume", checked((int)pe.MapVirtualAddressToRaw(
                consumeBody![^1].NextIP, false))));
            image = File.ReadAllBytes(binary);
            metadataBytes = File.ReadAllBytes(metadata);
        }
        finally { Cpp2IlApi.ResetInternalState(); }

        foreach (var (name, offset) in changes)
        {
            var changed = (byte[])image.Clone();
            changed[offset] ^= 1;
            Cpp2IlApi.ResetInternalState();
            TestGameLoader.EnsureInit();
            try
            {
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes,
                    UnityVersion.Parse("2021.3.35f1"));
                var (forward, consume, _, _, _, _) = Fixture();
                if (name == "Forward")
                    Assert.That(X64OwnerArrayArgumentTailProof.Find(forward), Is.Null,
                        $"Changed native byte at 0x{offset:X} in {name}");
                else
                    Assert.That(X64OwnerIncrementReferenceStoreProof.Find(consume), Is.Null,
                        $"Changed native byte at 0x{offset:X} in {name}");
            }
            finally { Cpp2IlApi.ResetInternalState(); }
        }
    }

    private static string RequireInput()
    {
        var directory = Environment.GetEnvironmentVariable(InputVariable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set " + InputVariable + " to the neutral exact player input.");
        Assert.That(File.Exists(Path.Combine(directory!, "GameAssembly.dll")), Is.True);
        Assert.That(File.Exists(Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat")), Is.True);
        return directory!;
    }

    private static void Initialize(string directory)
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory, "GameAssembly.dll"),
            Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
                "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
    }

    private static (MethodAnalysisContext Forward, MethodAnalysisContext Consume,
        FieldAnalysisContext Items, FieldAnalysisContext Value,
        FieldAnalysisContext Counter, FieldAnalysisContext Last) Fixture()
    {
        var assembly = Cpp2IlApi.CurrentAppContext!
            .GetAssemblyByName("ArrayElementArgumentTailFixture")!;
        var host = assembly.Types.Single(type => type.Name == "Host");
        var item = assembly.Types.Single(type => type.Name == "Item");
        return (host.Methods.Single(method => method.Name == "Forward"),
            host.Methods.Single(method => method.Name == "Consume"),
            host.Fields.Single(field => field.Name == "Items"),
            item.Fields.Single(field => field.Name == "Value"),
            host.Fields.Single(field => field.Name == "CallCount"),
            host.Fields.Single(field => field.Name == "LastValue"));
    }
}
