using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64InstanceParameterReferenceReadProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsThreeArgumentReadsAndUnusedInstanceReceiver()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_INSTANCE_PARAMETER_REFERENCE_READ_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_INSTANCE_PARAMETER_REFERENCE_READ_INPUT to the neutral exact player input.");

        var binary = Path.Combine(input!, "GameAssembly.dll");
        var metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("InstanceParameterReferenceReadFixture")!;
            var reader = assembly.Types.Single(type => type.Name == "Reader");
            var box = assembly.Types.Single(type => type.Name == "ValueBox");
            var pairs = new[]
            {
                (Method: "ReadText", Field: "Text"),
                (Method: "ReadPayload", Field: "Payload"),
                (Method: "ReadNumbers", Field: "Numbers"),
            };
            foreach (var pair in pairs)
            {
                var method = reader.Methods.Single(candidate => candidate.Name == pair.Method);
                var field = box.Fields.Single(candidate => candidate.Name == pair.Field);
                method.EnsureRawBytes();
                method.ParameterOperands = app.InstructionSet.GetParameterOperandsFromMethod(method);
                Assert.Multiple(() =>
                {
                    Assert.That(X64Guarded64BitFieldReadProof.Find(method,
                        X86Utils.Iterate(method).ToArray())?.Field, Is.SameAs(field), pair.Method);
                    Assert.That(X86UnusedReceiverProof.IsUnused(method,
                        new Register(null, "rcx")), Is.True, pair.Method);
                });

                var originalOperand = method.ParameterOperands[1];
                method.ParameterOperands[1] = new Register(null, "r9");
                try
                {
                    Assert.That(X86UnusedReceiverProof.IsUnused(method,
                        new Register(null, "rcx")), Is.False, pair.Method);
                    Assert.That(X64Guarded64BitFieldReadProof.HasUnchangedInstanceArgumentAbi(
                        method), Is.False, pair.Method);
                    Assert.That(X64Guarded64BitFieldReadProof.Find(method,
                        X86Utils.Iterate(method).ToArray()), Is.Null, pair.Method);
                }
                finally { method.ParameterOperands[1] = originalOperand; }

                field.OverrideFieldType = app.SystemTypes.SystemInt64Type;
                try
                {
                    Assert.That(X64Guarded64BitFieldReadProof.Find(method,
                        X86Utils.Iterate(method).ToArray()), Is.Null, pair.Method);
                }
                finally { field.OverrideFieldType = null; }
            }

            var payload = reader.Methods.Single(method => method.Name == "ReadPayload");
            var start = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                payload.UnderlyingPointer, false));
            var original = File.ReadAllBytes(binary);
            foreach (var change in new[] { (Offset: 8, Byte: (byte)8),
                         (Offset: 23, Byte: (byte)0x90) })
            {
                var altered = (byte[])original.Clone();
                altered[start + change.Offset] = change.Byte;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(altered, File.ReadAllBytes(metadata),
                    UnityVersion.Parse("2021.3.35f1"));
                var changed = Cpp2IlApi.CurrentAppContext!
                    .GetAssemblyByName("InstanceParameterReferenceReadFixture")!.Types
                    .Single(type => type.Name == "Reader").Methods
                    .Single(method => method.Name == "ReadPayload");
                Assert.That(X64Guarded64BitFieldReadProof.Find(changed,
                    X86Utils.Iterate(changed).ToArray()), Is.Null,
                    $"changed native byte +{change.Offset}");
            }

            var unwindChanged = (byte[])original.Clone();
            var codeOffset = FindUnwindCodeOffset(original, (PE)app.Binary,
                payload.UnderlyingPointer);
            Assert.Multiple(() =>
            {
                Assert.That(original[codeOffset], Is.EqualTo(4));
                Assert.That(original[codeOffset + 1], Is.EqualTo(0x42));
            });
            unwindChanged[codeOffset] = 3; // Valid but no longer matches the native four-byte prolog.
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(unwindChanged, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var differentUnwind = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("InstanceParameterReferenceReadFixture")!.Types
                .Single(type => type.Name == "Reader").Methods
                .Single(method => method.Name == "ReadPayload");
            differentUnwind.EnsureRawBytes();
            Assert.That(X64NativeInstructionReader.ReadRootBody(differentUnwind), Is.Not.Null);
            Assert.That(X64Guarded64BitFieldReadProof.Find(differentUnwind,
                X86Utils.Iterate(differentUnwind).ToArray()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static int FindUnwindCodeOffset(byte[] image, PE pe, ulong entry)
    {
        var signature = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c, 4));
        var optional = signature + 4 + 20;
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(optional, 2)),
            Is.EqualTo(0x20b));
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var directory = optional + 112 + 3 * 8;
        var tableRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory, 4));
        var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory + 4, 4));
        Assert.That(tableSize % 12, Is.Zero);
        var tableOffset = checked((int)pe.MapVirtualAddressToRaw(imageBase + tableRva, false));
        var entryRva = checked((uint)(entry - imageBase));
        for (var offset = tableOffset; offset < tableOffset + tableSize; offset += 12)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4)) != entryRva)
                continue;
            var unwindRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 8, 4));
            return checked((int)pe.MapVirtualAddressToRaw(imageBase + unwindRva, false)) + 4;
        }
        throw new AssertionException("The exact player has no unwind record for the read root.");
    }
}
