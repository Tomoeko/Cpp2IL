using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64MetadataInitializationTypeUsageProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void TypeUsageArmRequiresDecodedIndexPointerWidthAndSharedCommit(bool alternate)
    {
        // A neutral structural witness. The full proof authenticates its switch
        // entry and registration identity independently against the player.
        byte[] bytes = alternate
            ? [0x48, 0x8B, 0x05, 0x20, 0, 0, 0, 0x48, 0x8B, 0x48, 0x38,
                0x48, 0x8B, 0x1C, 0xF9, 0xE9, 0x40, 0, 0, 0]
            : [0x48, 0x8B, 0x05, 0x20, 0, 0, 0, 0x8B, 0xD1,
                0x48, 0x8B, 0x48, 0x38, 0x48, 0x8B, 0x1C, 0xD1,
                0xE9, 0x40, 0, 0, 0];
        var body = X86Utils.Iterate(bytes, 0x1000, false).ToArray();
        var join = body[^1].NearBranchTarget;
        Assert.That(X64MetadataInitializationHelperProof.ProveTypeUsageArmShape(
            body, alternate, join, out var registration), Is.True);
        Assert.That(registration, Is.EqualTo(body[0].IPRelativeMemoryAddress));
        Assert.That(X64MetadataInitializationHelperProof.ProveTypeUsageArmShape(
            body, !alternate, join, out _), Is.False);

        foreach (var mutation in new[]
                 { "index", "scale", "width", "table-offset", "result", "join", "prefix", "boundary" })
        {
            var changed = body.ToArray();
            var index = mutation switch
            {
                "index" or "scale" or "width" or "result" => alternate ? 2 : 3,
                "table-offset" => alternate ? 1 : 2,
                "join" => body.Length - 1,
                _ => 0,
            };
            var instruction = changed[index];
            switch (mutation)
            {
                case "index": instruction.MemoryIndex = Register.RSI; break;
                case "scale": instruction.MemoryIndexScale = 4; break;
                case "width":
                    instruction.Code = Code.Mov_r32_rm32;
                    instruction.Op0Register = Register.EBX; break;
                case "table-offset": instruction.MemoryDisplacement64++; break;
                case "result": instruction.Op0Register = Register.RAX; break;
                case "join": instruction.NearBranch64++; break;
                case "prefix": instruction.HasRepPrefix = true; break;
                case "boundary": instruction.IP++; break;
            }
            changed[index] = instruction;
            Assert.That(X64MetadataInitializationHelperProof.ProveTypeUsageArmShape(
                changed, alternate, join, out _), Is.False, mutation);
        }
        if (!alternate)
        {
            var encodedIndex = body.ToArray();
            encodedIndex[1].Op1Register = Register.R9D;
            Assert.That(X64MetadataInitializationHelperProof.ProveTypeUsageArmShape(
                encodedIndex, false, join, out _), Is.False,
                "The registration lookup uses the decoded index, not the encoded usage token.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public void ExactPlayerRejectsChangedTypeArmWhileTypeInfoRemainsProved(bool alternate)
    {
        string? binary, metadata;
        if (alternate)
        {
            binary = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_BINARY");
            metadata = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_FILE");
        }
        else
        {
            var directory = Environment.GetEnvironmentVariable("CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT");
            binary = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "GameAssembly.dll");
            metadata = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory,
                "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        }
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Set the neutral Type control input or the alternate metadata player paths.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var initializer = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, pe, unwind, initializer), Is.True);
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyType(app, pe, unwind, initializer), Is.True);

            var thunk = Decode(pe, initializer, 5, 1);
            var wrapper = Decode(pe, thunk[0].NearBranchTarget, 7, 2);
            var core = wrapper[1].NearBranchTarget;
            var first = unwind.ClassifySpan(core, core + 1);
            Assert.That(first.End - core, Is.EqualTo(alternate ? 0x37UL : 0x5dUL));
            var dispatch = Decode(pe, first.End, alternate ? 0x3a : 0x23, alternate ? 15 : 8);
            var table = unwind.ImageBase + dispatch[alternate ? 12 : 1].MemoryDisplacement64;
            var typeEntryOffset = checked((int)pe.MapVirtualAddressToRaw(table + 4, false));
            var image = File.ReadAllBytes(binary!);
            var armAddress = unwind.ImageBase + (uint)BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(typeEntryOffset, 4));
            var arm = Decode(pe, armAddress, alternate ? 0x14 : 0x16, alternate ? 4 : 5);
            var mutations = new List<(string Name, int Offset, byte Mask)>
            {
                ("registration identity", checked((int)pe.MapVirtualAddressToRaw(arm[0].NextIP - 4, false)), 8),
                ("types table offset", checked((int)pe.MapVirtualAddressToRaw(arm[alternate ? 1 : 2].NextIP - 1, false)), 1),
                ("pointer index scale", checked((int)pe.MapVirtualAddressToRaw(arm[alternate ? 2 : 3].NextIP - 1, false)), 0x40),
                ("shared cache commit", checked((int)pe.MapVirtualAddressToRaw(arm[^1].NextIP - 4, false)), 1),
            };
            var metadataBytes = File.ReadAllBytes(metadata!);
            foreach (var mutation in mutations)
            {
                var changed = (byte[])image.Clone();
                changed[mutation.Offset] ^= mutation.Mask;
                RejectTypeOnly(changed, mutation.Name);
            }
            var redirected = (byte[])image.Clone();
            image.AsSpan(typeEntryOffset - 4, 4).CopyTo(redirected.AsSpan(typeEntryOffset, 4));
            RejectTypeOnly(redirected, "Type usage redirected to the independently valid TypeInfo arm");

            void RejectTypeOnly(byte[] changed, string reason)
            {
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var modified = Cpp2IlApi.CurrentAppContext!;
                var changedPe = (PE)modified.Binary;
                var changedUnwind = X64UnwindProof.ForApplication(modified)!;
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(modified, changedPe,
                    changedUnwind, initializer), Is.True, $"TypeInfo evidence after {reason}");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyType(modified, changedPe,
                    changedUnwind, initializer), Is.False, reason);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] Decode(PE pe, ulong address, int bytes, int count) =>
        X86Utils.Iterate(pe.GetRawBinaryContent().Slice(
            checked((int)pe.MapVirtualAddressToRaw(address, false)), bytes), address, false)
            .Take(count).ToArray();
}
