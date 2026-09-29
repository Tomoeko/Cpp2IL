using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;
using Instruction = Iced.Intel.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ReferenceFieldAddressStoreFixtureTests
{
    [Test]
    public void CapturedReferenceTransferRetainsOriginalMetadataEffectsAndBarrierEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NESTED_LITERAL_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NESTED_LITERAL_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("NestedLiteralStoreFixture")!.Types.Single(type => type.Name == "StoreOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == "ReplaceFirst");
            var proof = X64ReferenceFieldAddressStoreProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Source.Name, Is.EqualTo("Replacement"));
            Assert.That(proof.Destination.Name, Is.EqualTo("First"));
            Assert.That(proof.Increments.Select(increment => increment.Field.Name), Is.EqualTo(new[] { "Marker" }));
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(ReferenceFieldAddressStoreRecovery.HasEvidence(method), Is.True);
            Assert.That(ReferenceFieldAddressStoreRecovery.IsValidFor(method), Is.True);
            var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
            var graph = method.ControlFlowGraph!;
            var capture = graph.Instructions.Single(instruction => instruction.NativeAddress == proof.Native.SourceAddress);
            var increment = graph.Instructions.Single(instruction => instruction.NativeAddress == proof.Increments[0].Address);
            Assert.That(increment.IntegerBitWidth, Is.EqualTo(32), "The bound native INC32 width remains explicit after field typing.");
            var store = graph.Instructions.Single(instruction => instruction.NativeAddress == proof.Native.StoreAddress);
            var source = (FieldReference)capture.Operands[1];
            var destination = (FieldReference)store.Operands[0];
            var value = (LocalVariable)store.Operands[1];
            var receiver = destination.Local;
            var pe = (PE)app.Binary;
            var buffer = ((MemoryStream)pe.BaseStream).GetBuffer();

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(ReferenceFieldAddressStoreRecovery.IsValidFor(method), Is.False);
                    Assert.That(() => IlGenerator.GenerateIl(method, definition),
                        Throws.TypeOf<DecompilerException>().With.Message.Contains("Reference-field address store proof"));
                }
                finally { restore(); }
                Assert.That(ReferenceFieldAddressStoreRecovery.IsValidFor(method), Is.True);
            }

            Reject(() => method.Name = "other_transfer", () => method.OverrideName = null);
            Reject(() => method.Attributes |= MethodAttributes.PinvokeImpl, () => method.OverrideAttributes = null);
            Reject(() => method.Attributes |= MethodAttributes.Abstract, () => method.OverrideAttributes = null);
            Reject(() => method.ImplAttributes |= MethodImplAttributes.InternalCall, () => method.OverrideImplAttributes = null);
            Reject(() => method.ImplAttributes |= MethodImplAttributes.Native, () => method.OverrideImplAttributes = null);
            Reject(() => method.OverrideReturnType = app.SystemTypes.SystemVoidType, () => method.OverrideReturnType = null);
            Reject(() => owner.Name = "OtherOwner", () => owner.OverrideName = null);
            Reject(() => owner.Namespace = "OtherNamespace", () => owner.OverrideNamespace = null);
            Reject(() => source.Field.Attributes |= FieldAttributes.Static, () => source.Field.OverrideAttributes = null);
            Reject(() => destination.Field.Attributes |= FieldAttributes.InitOnly, () => destination.Field.OverrideAttributes = null);
            Reject(() => source.Field.FieldType = app.SystemTypes.SystemObjectType, () => source.Field.OverrideFieldType = null);
            Reject(() => destination.Offset++, () => destination.Offset = proof.Destination.Offset);
            Reject(() => increment.IntegerBitWidth = 64, () => increment.IntegerBitWidth = 32);
            Reject(() => increment.IntegerBitWidth = 0, () => increment.IntegerBitWidth = 32);
            var immediate = increment.Operands[2];
            Reject(() => increment.SetOperand(2, new Immediate(2)), () => increment.SetOperand(2, immediate));
            var originalRegister = receiver.Register;
            Reject(() => receiver.Register = originalRegister.Copy(0), () => receiver.Register = originalRegister);
            Reject(() => store.SetOperand(1, new LocalVariable("other_capture", value.Register, value.Type)),
                () => store.SetOperand(1, value));
            var sourceRegister = value.Register;
            Reject(() => value.Register = new Register(sourceRegister.Number + 1, sourceRegister.Name, sourceRegister.Version),
                () => value.Register = sourceRegister);
            var block = graph.FindBlockByInstruction(capture)!;
            var captureIndex = block.Instructions.IndexOf(capture);
            Reject(() =>
            {
                block.Instructions.Remove(capture);
                block.Instructions.Insert(block.Instructions.IndexOf(increment) + 1, capture);
            }, () =>
            {
                block.Instructions.Remove(capture);
                block.Instructions.Insert(captureIndex, capture);
            });
            var originalBytes = method.RawBytes;
            Reject(() =>
            {
                var changed = originalBytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }, () => method.RawBytes = originalBytes);

            // Keep field offsets and native operations unchanged; only the declared
            // object extent changes. Every consumed width must fit that extent.
            var sizePointer = app.Binary.TypeDefinitionSizePointers[owner.Definition!.TypeIndex!.Value];
            var sizeRaw = checked((int)pe.MapVirtualAddressToRaw(sizePointer));
            var oldSize = buffer.AsSpan(sizeRaw, 4).ToArray();
            foreach (var limit in new[] { proof.Source.Offset + 7, proof.Destination.Offset + 7,
                         proof.Increments[0].Field.Offset + 3 })
                Reject(() => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeRaw, 4), (uint)limit),
                    () => oldSize.CopyTo(buffer, sizeRaw));

            var index = X64UnwindProof.ForApplication(app)!;
            var exported = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_gc_wbarrier_set_field");
            var wrapper = X64NativeInstructionReader.Read(pe, index, exported, 3, 16)!;
            var writer = X64NativeInstructionReader.Read(pe, index, wrapper[2].NearBranchTarget, 2, 16)!;
            var marker = writer[1].NearBranchTarget;
            var markerBody = Decode(pe, marker, 17);
            Assert.That(X64ReferenceWriteBarrierProof.CardMarkerShape(markerBody, out _, out _), Is.True);
            var markerLength = checked((int)(markerBody[^1].NextIP - marker));
            var callerCount = Cpp2IL.Core.Utils.X86Utils.Iterate(method).Count();
            foreach (var target in new[] { exported, writer[0].IP, marker - 7, marker, marker + (ulong)markerLength - 1 })
            {
                var changed = WithRelocation(pe, target, out var relocationRaw);
                using var changedStream = new MemoryStream(changed, 0, changed.Length, writable: true, publiclyVisible: true);
                using var changedPe = new PE(changedStream);
                var changedIndex = X64UnwindProof.ForBinary(changedPe);
                Assert.That(changedIndex, Is.Not.Null);
                Assert.That(changedIndex!.IsUnaffectedByBaseRelocation(method.UnderlyingPointer,
                    (uint)method.RawBytes.Length), Is.True);
                Assert.That(X64ReferenceFieldAddressStoreProof.TryProveShape(
                    Decode(changedPe, method.UnderlyingPointer, callerCount)), Is.Not.Null,
                    "The caller still has the bounded transfer shape and unchanged instructions.");
                Assert.That(Decode(changedPe, marker, 17), Is.EqualTo(markerBody),
                    "The loader changes helper instructions despite unchanged encoded file instructions.");
                Assert.That(X64ReferenceWriteBarrierProof.CardMarkerShape(Decode(changedPe, marker, 17), out _, out _), Is.True);
                Assert.That(changedIndex!.ClassifySpan(marker, markerBody[^1].NextIP).Kind,
                    Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
                Assert.That(X64ReferenceWriteBarrierProof.TryIdentify(changedPe, changedIndex, proof.Native.BarrierTarget), Is.False);
                var oldRelocation = buffer.AsSpan(relocationRaw, 10).ToArray();
                Reject(() => changed.AsSpan(relocationRaw, 10).CopyTo(buffer.AsSpan(relocationRaw, 10)),
                    () => oldRelocation.CopyTo(buffer, relocationRaw));
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.Nothing);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] Decode(PE pe, ulong address, int count)
    {
        var raw = checked((int)pe.MapVirtualAddressToRaw(address));
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(pe.GetRawBinaryContent().Slice(raw, 80).ToArray()), address);
        return Enumerable.Range(0, count).Select(_ => decoder.Decode()).ToArray();
    }

    private static byte[] WithRelocation(PE pe, ulong target, out int relocationRaw)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + 5 * 8, 4));
        relocationRaw = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(relocationRaw + 4, 4)), Is.GreaterThanOrEqualTo(10));
        var targetRva = checked((uint)(target - imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(relocationRaw, 4), targetRva & ~0xFFFU);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(relocationRaw + 8, 2), (ushort)(0xA000U | (targetRva & 0xFFFU)));
        return image;
    }
}
