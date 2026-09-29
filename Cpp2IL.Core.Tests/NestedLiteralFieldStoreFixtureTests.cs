using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player regression for field snapshots and ordered effects.</summary>
[NonParallelizable]
public class NestedLiteralFieldStoreFixtureTests
{
    [Test]
    public void NestedStoresKeepTheirOriginalReceiverCaptureThroughFinalEmission()
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
            var assembly = app.GetAssemblyByName("NestedLiteralStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "StoreOwner");
            var target = assembly.Types.Single(type => type.Name == "StoreTarget");
            foreach (var name in new[] { "BooleanPair", "SignedPair", "UnsignedPair", "CapturedReplacement", "SetParameter" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, name);
                var graph = method.ControlFlowGraph!;
                Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.False, name);
                var stores = graph.Instructions.Where(instruction => instruction.Operands is
                    [FieldReference access, Immediate] && ReferenceEquals(access.Field.DeclaringType, target)).ToArray();
                Assert.That(stores, Has.Length.EqualTo(name == "SetParameter" ? 3 : 2), name);
                foreach (var store in stores)
                    Assert.That(LiteralFieldStoreProof.IsValidFor(method, store,
                        (FieldReference)store.Operands[0], (Immediate)store.Operands[1]), Is.True, name);
                foreach (var marker in method.NullCheckedFieldAccesses)
                    Assert.That(marker.IsValidFor(method), Is.True, name);

                var evidence = method.NullCheckedFieldAccesses.First(marker => marker.StoredValue is Immediate);
                var receiver = evidence.Receiver;
                var origin = graph.Instructions.Single(instruction => ReferenceEquals(instruction.Destination, receiver));
                var source = (FieldReference)origin.Operands[1];
                Assert.That(FieldLoadReceiverProof.HasBoundProducer(method, receiver, origin, evidence.Operation), Is.True);
                var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;

                void Reject(Action mutate, Action restore)
                {
                    try
                    {
                        mutate();
                        Assert.That(evidence.IsValidFor(method), Is.False, name);
                        Assert.That(() => IlGenerator.GenerateIl(method, definition),
                            Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-checked field access marker"));
                    }
                    finally { restore(); }
                    Assert.That(evidence.IsValidFor(method), Is.True, name);
                }

                var address = origin.NativeAddress;
                Reject(() => origin.NativeAddress = address + 1, () => origin.NativeAddress = address);
                Reject(() => origin.IntegerBitWidth = 64, () => origin.IntegerBitWidth = 0);
                var offset = source.Offset;
                Reject(() => source.Offset++, () => source.Offset = offset);
                Reject(() => source.Field.OverrideFieldType = app.SystemTypes.SystemObjectType,
                    () => source.Field.OverrideFieldType = null);
                Reject(() => source.Field.OverrideAttributes = FieldAttributes.Private,
                    () => source.Field.OverrideAttributes = null);
                var originalField = source.Field;
                Reject(() => source.Field = owner.Fields.Single(field => field.Name == "Second"),
                    () => source.Field = originalField);
                var originalOwner = source.Local;
                Reject(() => source.Local = new LocalVariable("unbound-owner", originalOwner.Register, originalOwner.Type),
                    () => source.Local = originalOwner);
                var incoming = originalOwner.Register;
                Reject(() => originalOwner.Register = new Register(incoming.Number + 1, incoming.Name, incoming.Version),
                    () => originalOwner.Register = incoming);
                Reject(() => originalOwner.Register = incoming.Copy(0),
                    () => originalOwner.Register = incoming);
                var receiverRegister = receiver.Register;
                Reject(() => receiver.Register = new Register(receiverRegister.Number, "rdx", receiverRegister.Version),
                    () => receiver.Register = receiverRegister);
                Reject(() => receiver.Register = new Register(receiverRegister.Number + 1, receiverRegister.Name, receiverRegister.Version),
                    () => receiver.Register = receiverRegister);
                var receiverType = receiver.Type;
                Reject(() => receiver.Type = owner, () => receiver.Type = receiverType);
                Reject(() => evidence.Field.OverrideFieldType = app.SystemTypes.SystemInt64Type,
                    () => evidence.Field.OverrideFieldType = null);
                var pe = (PE)app.Binary;
                var sizePointer = app.Binary.TypeDefinitionSizePointers[source.Field.DeclaringType.Definition!.TypeIndex!.Value];
                var sizeRaw = checked((int)pe.MapVirtualAddressToRaw(sizePointer));
                var buffer = ((MemoryStream)pe.BaseStream).GetBuffer();
                var originalSize = buffer.AsSpan(sizeRaw, 4).ToArray();
                Reject(() => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(sizeRaw, 4), (uint)source.Offset + 7),
                    () => originalSize.CopyTo(buffer, sizeRaw));
                var originBlock = graph.FindBlockByInstruction(origin)!;
                var originBlockIndex = graph.Blocks.IndexOf(originBlock);
                Reject(() =>
                    {
                        graph.Blocks.Remove(originBlock);
                        Assert.That(FieldLoadReceiverProof.HasBoundRead(method, receiver, origin), Is.False);
                    },
                    () => graph.Blocks.Insert(originBlockIndex, originBlock));
                var rawBytes = method.RawBytes;
                Reject(() =>
                {
                    var changed = rawBytes.AsSpan().ToArray();
                    changed[checked((int)(address!.Value - method.UnderlyingPointer))] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                }, () => method.RawBytes = rawBytes);

                if (name is "BooleanPair" or "SignedPair" or "CapturedReplacement")
                {
                    // Moving a capture after the earlier write/call still leaves it before
                    // the guarded store. Native effect order must reject that mutation.
                    var producerBlock = graph.FindBlockByInstruction(origin)!;
                    var storeBlock = graph.FindBlockByInstruction(evidence.Operation)!;
                    var originalIndex = producerBlock.Instructions.IndexOf(origin);
                    var laterEffect = graph.Instructions.First(instruction => instruction.NativeAddress > address &&
                        instruction.NativeAddress < evidence.Operation.NativeAddress &&
                        (instruction.IsCall || instruction.Destination is FieldReference));
                    var effectBlock = graph.FindBlockByInstruction(laterEffect)!;
                    Reject(() =>
                    {
                        producerBlock.Instructions.Remove(origin);
                        effectBlock.Instructions.Insert(effectBlock.Instructions.IndexOf(laterEffect) + 1, origin);
                    }, () =>
                    {
                        effectBlock.Instructions.Remove(origin);
                        producerBlock.Instructions.Insert(originalIndex, origin);
                    });

                    if (!ReferenceEquals(producerBlock, storeBlock))
                    {
                        var entry = graph.EntryBlock;
                        Reject(() =>
                        {
                            entry.Successors.Add(storeBlock);
                            storeBlock.Predecessors.Add(entry);
                        }, () =>
                        {
                            entry.Successors.Remove(storeBlock);
                            storeBlock.Predecessors.Remove(entry);
                        });
                    }
                }
                Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.Nothing, name);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
