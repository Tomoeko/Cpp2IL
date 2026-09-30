using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86FoldedBooleanArrayLiteralStoreFixtureTests
{
    [TestCase("before-start", false)]
    [TestCase("start", false)]
    [TestCase("call-operand", false)]
    [TestCase("last-byte", false)]
    [TestCase("after-end", true)]
    public void CompleteStackBodyRequiresUnrelocatedBytes(string target, bool accepted)
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_FOLDED_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT to the neutral player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
                    "global-metadata.dat"), UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("FoldedBooleanArrayStoreFixture")!.Types
                .Single(type => type.Name == "FirstArrayOwner").Methods
                .Single(candidate => candidate.Name == "SetFalse");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            // Parse an independent index so the application reader first sees
            // the modified, otherwise valid relocation directory below.
            var original = X64UnwindProof.Parse(pe.GetRawBinaryContent())!;
            var region = original.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            Assert.That(region.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
            var bodyBytes = method.RawBytes.AsSpan().ToArray();
            var relocated = target switch
            {
                "before-start" => region.Start - 7,
                "start" => region.Start,
                "call-operand" => native[10].IP + 1,
                "last-byte" => region.End - 1,
                "after-end" => region.End,
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
            AppendRelocation(pe, original.ImageBase,
                checked((uint)(relocated - original.ImageBase)));
            var current = X64UnwindProof.ForApplication(app);
            Assert.That(current, Is.Not.Null,
                "The altered image must remain independently parseable, not just invalidate a prior cache.");
            Assert.That(current!.HasUnchangedInput(pe.GetRawBinaryContent()), Is.True);
            Assert.That(current.IsUnaffectedByBaseRelocation(region.Start,
                checked((uint)(region.End - region.Start))), Is.EqualTo(accepted));
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.AsSpan().ToArray(), Is.EqualTo(bodyBytes),
                "The file body stays unchanged; only the loaded relocation overlap differs.");
            Assert.That(X64Stack28BodyProof.Read(method, 13, 96) != null, Is.EqualTo(accepted));
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native) != null,
                Is.EqualTo(accepted));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void AppendRelocation(PE pe, ulong imageBase, uint rva)
    {
        var image = pe.GetRawBinaryContent();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x3C, 4)));
        var optional = header + 24;
        var directory = optional + 112 + 5 * 8;
        var tableRva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory, 4));
        var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory + 4, 4));
        var table = checked((int)pe.MapVirtualAddressToRaw(imageBase + tableRva, false));
        Assert.That(table, Is.GreaterThanOrEqualTo(0));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(header + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(header + 20, 2));
        var sections = optional + optionalSize;
        var found = false;
        for (var index = 0; index < sectionCount; index++)
        {
            var at = sections + index * 40;
            var start = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 12, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 16, 4));
            if (tableRva < start || (ulong)tableRva + tableSize + 12 > (ulong)start + rawSize)
                continue;
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 8, 4));
            var required = checked(tableRva - start + tableSize + 12);
            if (virtualSize < required)
                Write(at + 8, BitConverter.GetBytes(required));
            found = true;
            break;
        }
        Assert.That(found, Is.True,
            "The neutral player needs file-backed padding for an additional synthetic relocation record.");
        var block = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), rva & ~0xFFFU);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2),
            checked((ushort)(0xA000 | (rva & 0xFFF))));
        Write(table + checked((int)tableSize), block);
        Write(directory + 4, BitConverter.GetBytes(tableSize + 12));

        void Write(int offset, byte[] bytes)
        {
            var position = pe.BaseStream.Position;
            try { pe.BaseStream.Position = offset; pe.BaseStream.Write(bytes, 0, bytes.Length); }
            finally { pe.BaseStream.Position = position; }
        }
    }

    [Test]
    public void TwoManagedOwnersMustIndependentlyProveTheSharedNativeStore()
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_FOLDED_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT to the neutral player input.");
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
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("FoldedBooleanArrayStoreFixture")!;
            var firstOwner = assembly.Types.Single(type => type.Name == "FirstArrayOwner");
            var secondOwner = assembly.Types.Single(type => type.Name == "SecondArrayOwner");
            var first = firstOwner.Methods.Single(method => method.Name == "SetFalse");
            var second = secondOwner.Methods.Single(method => method.Name == "SetFalse");
            var firstField = firstOwner.Fields.Single(field => field.Name == "Values");
            var secondField = secondOwner.Fields.Single(field => field.Name == "Values");
            first.EnsureRawBytes();
            second.EnsureRawBytes();
            var native = X86Utils.Iterate(first).ToArray();
            var secondNative = X86Utils.Iterate(second).ToArray();
            var aliases = app.MethodsByAddress[first.UnderlyingPointer];

            Assert.Multiple(() =>
            {
                Assert.That(first.UnderlyingPointer, Is.EqualTo(second.UnderlyingPointer));
                Assert.That(aliases, Has.Count.EqualTo(2));
                Assert.That(aliases.Count(method => ReferenceEquals(method, first)), Is.EqualTo(1));
                Assert.That(aliases.Count(method => ReferenceEquals(method, second)), Is.EqualTo(1));
                Assert.That(first.RawBytes.Length, Is.EqualTo(44));
                Assert.That(native, Has.Length.EqualTo(13));
                Assert.That(secondNative, Is.EqualTo(native));
                Assert.That(X86FieldBooleanArrayLiteralStoreProof
                    .TryProveShape(native, (PE)app.Binary)?.FieldOffset,
                    Is.EqualTo(firstField.Offset));
                Assert.That(firstField.Offset, Is.EqualTo(secondField.Offset));
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native)?.ArrayField,
                    Is.SameAs(firstField));
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(second, native)?.ArrayField,
                    Is.SameAs(secondField));
                Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[10].IP));
                Assert.That(native[5].NearBranchTarget, Is.EqualTo(native[12].IP));
                Assert.That(native[7].Immediate8, Is.Zero);
            });

            var changedStore = native.ToArray();
            changedStore[7].Immediate8 = 2;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, changedStore), Is.Null);
            var changedBounds = native.ToArray();
            changedBounds[5].Code = Code.Ja_rel8_64;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, changedBounds), Is.Null);

            var originalImplementation = second.Definition!.iflags;
            try
            {
                second.Definition.iflags |= 0xF000;
                Assert.That(second.Definition.IsUnmanagedCallersOnly, Is.True);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "every folded binding must retain the ordinary managed ABI");
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(second, secondNative), Is.Null);
            }
            finally { second.Definition.iflags = originalImplementation; }

            var firstRawBytes = first.RawBytes;
            var changedCachedStore = firstRawBytes.AsSpan().ToArray();
            changedCachedStore[checked((int)(native[7].NextIP - first.UnderlyingPointer - 1))] = 1;
            try
            {
                first.RawBytes = new BinarySlice(changedCachedStore);
                Assert.That(X64Stack28BodyProof.Read(first, 13, 96), Is.Null,
                    "A changed cached literal must be rejected before replacing it with fresh extraction.");
            }
            finally { first.RawBytes = firstRawBytes; }
            Assert.That(X64Stack28BodyProof.Read(first, 13, 96), Is.Not.Null);

            var rawField = secondField.BackingData!.Field.RawFieldType!;
            var originalFieldType = rawField.Type;
            try
            {
                rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "both aliases must retain their own Boolean-array descriptor");
            }
            finally { rawField.Type = originalFieldType; }

            var rawElement = rawField.GetEncapsulatedType();
            var originalElementModifiers = rawElement.NumMods;
            var originalElementByref = rawElement.Byref;
            var originalElementPinned = rawElement.Pinned;
            try
            {
                rawElement.NumMods = 1;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "element modifiers cannot be inferred from a byte store");
                rawElement.NumMods = originalElementModifiers;
                rawElement.Byref = 1;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "a byref element is outside the proved Boolean-array layout");
                rawElement.Byref = originalElementByref;
                rawElement.Pinned = 1;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(second, native), Is.Null,
                    "a pinned element is outside the proved Boolean-array layout");
            }
            finally
            {
                rawElement.NumMods = originalElementModifiers;
                rawElement.Byref = originalElementByref;
                rawElement.Pinned = originalElementPinned;
            }

            try
            {
                secondField.OverrideOffset = secondField.DefaultOffset + 8;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "the other owner must retain the native field offset");
            }
            finally { secondField.OverrideOffset = null; }

            var secondIndex = second.Parameters.Single();
            try
            {
                secondIndex.OverrideParameterType = app.SystemTypes.SystemBooleanType;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "the other binding must retain its native signature");
            }
            finally { secondIndex.OverrideParameterType = null; }

            var constructed = (GenericInstanceTypeAnalysisContext)firstOwner.BaseType!;
            var injected = new InjectedFieldAnalysisContext("Blocked",
                app.SystemTypes.SystemInt32Type, FieldAttributes.Public,
                constructed.GenericType, 16);
            try
            {
                constructed.GenericType.Fields.Add(injected);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(second, native), Is.Null);
            }
            finally { constructed.GenericType.Fields.Remove(injected); }

            aliases.Add(first);
            try
            {
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "duplicate managed bindings must not authorize a folded body");
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }

            aliases.Add(firstOwner.Methods.Single(method => method.Name == ".ctor"));
            try
            {
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "a third binding requires its own complete-body proof");
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(second, native), Is.Null);
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }

            var firstIndex = aliases.FindIndex(candidate => ReferenceEquals(candidate, first));
            Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0));
            try
            {
                aliases.RemoveAt(firstIndex);
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native), Is.Null,
                    "an unbound method cannot claim the native body");
            }
            finally { aliases.Insert(firstIndex, first); }

            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(first, native)?.ArrayField,
                Is.SameAs(firstField));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
