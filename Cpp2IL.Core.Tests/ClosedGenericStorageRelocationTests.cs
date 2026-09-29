using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ClosedGenericStorageRelocationTests
{
    [TestCase("caller-code")]
    [TestCase("base-code")]
    [TestCase("required-pointer")]
    [TestCase("cached-class")]
    [TestCase("cache-readonly")]
    public void FreshRelocationEvidenceCannotChangeConsumedConstructorOrLayoutFacts(string target)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CLOSED_GENERIC_STORAGE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CLOSED_GENERIC_STORAGE_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var pe = (PE)app.Binary;
            var state = app.GetAssemblyByName("ClosedGenericStorageFixture")!.Types.Single(type => type.Name == "IntState");
            var constructor = state.Methods.Single(method => method.Name == ".ctor");
            var baseConstructor = app.SystemTypes.SystemObjectType.Methods.Single(method =>
                method.Name == ".ctor" && !method.IsStatic && method.Parameters.Count == 0);
            var generic = (GenericInstanceTypeAnalysisContext)state.Fields.Single(field => field.Name == "Prefix").FieldType;
            Assert.That(pe.TryGetTypeVirtualAddress(generic.OriginalRawType!, out var descriptor), Is.True);
            var image = pe.GetRawBinaryContent();
            var nt = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x3c, 4)));
            var optional = nt + 24;
            var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(optional + 24, 8));
            var tableRva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(optional + 152, 4));
            var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(optional + 156, 4));
            var table = checked((int)pe.MapVirtualAddressToRaw(imageBase + tableRva));
            var originalIndex = X64UnwindProof.Parse(image)!;
            Assert.That(originalIndex.HasCanonicalPointerRelocation(descriptor), Is.True);
            if (target == "cache-readonly")
            {
                var cell = generic.OriginalRawType!.Data.GenericClass + 24;
                var rva = checked((uint)(cell - imageBase));
                Assert.That(originalIndex.IsWritableVirtualRangeInOneSection(rva, 8), Is.True);
                var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 6, 2));
                var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 20, 2));
                var sections = optional + optionalSize;
                var found = false;
                for (var i = 0; i < sectionCount; i++)
                {
                    var at = sections + i * 40;
                    var start = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 12, 4));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 8, 4));
                    if (rva < start || (ulong)rva + 8 > (ulong)start + size)
                        continue;
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 36, 4));
                    Write(at + 36, BitConverter.GetBytes(flags & ~0x80000000U));
                    found = true;
                    break;
                }
                Assert.That(found, Is.True);
            }
            else if (target == "required-pointer")
            {
                var wanted = checked((uint)(descriptor - imageBase));
                var found = false;
                for (uint offset = 0; offset < tableSize;)
                {
                    var page = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(table + checked((int)offset), 4));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(table + checked((int)offset) + 4, 4));
                    for (uint entry = 8; entry < size; entry += 2)
                    {
                        var at = table + checked((int)(offset + entry));
                        var encoded = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(at, 2));
                        if (encoded >> 12 == 10 && page + (encoded & 0xfff) == wanted)
                        {
                            Write(at, new byte[2]);
                            found = true;
                        }
                    }
                    offset += size;
                }
                Assert.That(found, Is.True, "The control removes an actual required DIR64 rather than changing descriptor bytes.");
            }
            else
            {
                var address = target switch
                {
                    "caller-code" => constructor.UnderlyingPointer,
                    "base-code" => baseConstructor.UnderlyingPointer,
                    _ => generic.OriginalRawType!.Data.GenericClass + 24,
                };
                var rva = checked((uint)(address - imageBase));
                var block = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), rva & ~0xfffU);
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 12);
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), checked((ushort)(0xa000 | (rva & 0xfff))));
                var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 6, 2));
                var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 20, 2));
                var sections = optional + optionalSize;
                var found = false;
                for (var i = 0; i < sectionCount; i++)
                {
                    var at = sections + i * 40;
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
                Assert.That(found, Is.True, "The neutral player must have file-backed relocation padding for the appended synthetic record.");
                Write(table + checked((int)tableSize), block);
                Write(optional + 156, BitConverter.GetBytes(tableSize + 12));
                if (target == "cached-class")
                    Write(checked((int)pe.MapVirtualAddressToRaw(address)), BitConverter.GetBytes(descriptor));
            }
            // This is the first cached application index: the modified directory
            // is parsed as the initial image, not rejected as a stale prior snapshot.
            var current = X64UnwindProof.ForApplication(app);
            Assert.That(current, Is.Not.Null);
            Assert.That(current!.HasUnchangedInput(pe.GetRawBinaryContent()), Is.True);
            Assert.That(current.ClassifySpan(constructor.UnderlyingPointer, constructor.UnderlyingPointer + 36).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
            if (target == "cache-readonly")
            {
                var cell = generic.OriginalRawType!.Data.GenericClass + 24;
                var rva = checked((uint)(cell - imageBase));
                Assert.That(current.IsReadableFileBackedRva(rva), Is.True);
                Assert.That(current.IsWritableVirtualRangeInOneSection(rva, 8), Is.False);
                Assert.That(generic.OriginalRawType.GetGenericClass().CachedClass, Is.Zero);
                Assert.That(ClosedGenericValueLayoutProof.Find(generic), Is.Null,
                    "The runtime must be able to publish the computed class into its otherwise unchanged cache cell.");
            }
            else if (target == "required-pointer")
            {
                Assert.That(current.HasCanonicalPointerRelocation(descriptor), Is.False);
                Assert.That(ClosedGenericValueLayoutProof.Find(generic), Is.Null);
            }
            else if (target == "cached-class")
            {
                var cell = generic.OriginalRawType!.Data.GenericClass + 24;
                Assert.That(current.HasCanonicalPointerRelocation(cell), Is.True,
                    "This is a freshly parsed canonical pointer, not a stale directory or malformed relocation.");
                Assert.That(generic.OriginalRawType.GetGenericClass().CachedClass, Is.EqualTo(descriptor));
                Assert.That(ClosedGenericValueLayoutProof.Find(generic), Is.Null,
                    "A nonzero runtime-class cache can bypass computed layout; its layout has separate proof obligations.");
            }
            else
            {
                Assert.That(ClosedGenericValueLayoutProof.Find(generic)?.Size, Is.EqualTo(8),
                    "Unchanged generic storage is independently eligible in both code-relocation controls.");
                var address = target == "caller-code" ? constructor.UnderlyingPointer : baseConstructor.UnderlyingPointer;
                Assert.That(current.IsUnaffectedByBaseRelocation(address, target == "caller-code" ? 36U : 3U), Is.False);
                Assert.That(X64IteratorFactoryProof.ProveInertObjectConstructor(app, baseConstructor.UnderlyingPointer, pe, current),
                    Is.EqualTo(target == "caller-code"));
            }
            constructor.EnsureRawBytes();
            Assert.That(X64FoldedInt32ConstructorProof.Find(constructor, X86Utils.Iterate(constructor).ToArray()), Is.Null);

            void Write(int offset, byte[] bytes)
            {
                var position = pe.BaseStream.Position;
                try { pe.BaseStream.Position = offset; pe.BaseStream.Write(bytes, 0, bytes.Length); }
                finally { pe.BaseStream.Position = position; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
