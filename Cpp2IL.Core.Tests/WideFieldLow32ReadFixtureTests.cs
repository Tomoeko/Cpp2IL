using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class WideFieldLow32ReadFixtureTests
{
    [TestCase("SignedState", "ReadSigned")]
    [TestCase("SignedState", "ReadUnsigned")]
    [TestCase("UnsignedState", "ReadSigned")]
    [TestCase("UnsignedState", "ReadUnsigned")]
    public void ExactNativeFieldReadKeepsFull64StorageAndRejectsChangedFinalProvenance(string ownerName, string name)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_WIDE_FIELD_LOW32_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_WIDE_FIELD_LOW32_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("WideFieldLow32Fixture")!.Types.Single(type => type.Name == ownerName);
            var method = owner.Methods.Single(candidate => candidate.Name == name);
            Assert.That(X64WideFieldLow32ReadProof.Find(method), Is.Not.Null);
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(WideFieldLow32ReadRecovery.HasEvidence(method), Is.True);
            Assert.That(WideFieldLow32ReadRecovery.IsValidFor(method), Is.True);
            var graph = method.ControlFlowGraph!;
            var read = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Move);
            var conversion = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.IntegerExtend);
            var ret = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return);
            var access = (FieldReference)read.Operands[1];
            var field = access.Field;
            var receiver = access.Local;
            var capture = (LocalVariable)read.Operands[0];
            var result = (LocalVariable)conversion.Operands[0];
            var block = graph.FindBlockByInstruction(read)!;
            var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
            Assert.That(capture.Type, Is.SameAs(field.FieldType));
            Assert.That(IntegerExtension.StorageBits(capture.Type, app.SystemTypes), Is.EqualTo(64));
            Assert.That(result.Type, Is.SameAs(method.ReturnType));

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(WideFieldLow32ReadRecovery.IsValidFor(method), Is.False);
                    Assert.That(() => IlGenerator.GenerateIl(method, definition),
                        Throws.TypeOf<DecompilerException>().With.Message.Contains("Wide field low32 read proof"));
                }
                finally { restore(); }
                Assert.That(WideFieldLow32ReadRecovery.IsValidFor(method), Is.True);
            }

            Reject(() => field.OverrideFieldType = app.SystemTypes.SystemInt32Type, () => field.OverrideFieldType = null);
            Reject(() => field.OverrideAttributes = field.DefaultAttributes | FieldAttributes.Static, () => field.OverrideAttributes = null);
            Reject(() => field.OverrideOffset = field.DefaultOffset + 4, () => field.OverrideOffset = null);
            Reject(() => field.Name += "Changed", () => field.OverrideName = null);
            var fieldToken = field.BackingData!.Field.token;
            Reject(() => field.BackingData.Field.token ^= 1, () => field.BackingData.Field.token = fieldToken);
            var attributes = field.BackingData.Attributes;
            Reject(() => field.BackingData.Attributes = (attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private,
                () => field.BackingData.Attributes = attributes);
            var raw = field.BackingData.Field.RawFieldType!;
            var modifiers = raw.NumMods;
            Reject(() => raw.NumMods = 1, () => raw.NumMods = modifiers);
            var sibling = owner.Fields.Single(candidate => candidate.Name == "Neighbor");
            Reject(() => sibling.OverrideOffset = field.Offset + 4, () => sibling.OverrideOffset = null);
            Reject(() => access.Field = sibling, () => access.Field = field);
            Reject(() => access.Offset += 4, () => access.Offset = field.Offset);
            Reject(() => access.Local = new LocalVariable("forgedThis", receiver.Register, receiver.Type) { IsThis = true },
                () => access.Local = receiver);
            Reject(() => owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout, () => owner.OverrideAttributes = null);
            var methodFlags = method.Definition!.flags;
            Reject(() => method.Definition.flags ^= (ushort)MethodAttributes.HideBySig, () => method.Definition.flags = methodFlags);
            var methodToken = method.Definition.token;
            Reject(() => method.Definition.token ^= 1, () => method.Definition.token = methodToken);
            Reject(() => method.Attributes |= MethodAttributes.PinvokeImpl, () => method.OverrideAttributes = null);
            Reject(() => method.ImplAttributes |= MethodImplAttributes.InternalCall, () => method.OverrideImplAttributes = null);
            Reject(() => method.OverrideReturnType = app.SystemTypes.SystemInt64Type, () => method.OverrideReturnType = null);

            var receiverRegister = receiver.Register;
            Reject(() => receiver.Register = receiverRegister.Copy(0), () => receiver.Register = receiverRegister);
            Reject(() => receiver.Register = new Register(receiverRegister.Number + 1, receiverRegister.Name), () => receiver.Register = receiverRegister);
            Reject(() => receiver.Type = app.SystemTypes.SystemObjectType, () => receiver.Type = owner);
            Reject(() => receiver.IsThis = false, () => receiver.IsThis = true);
            Reject(() => receiver.IsMethodInfo = true, () => receiver.IsMethodInfo = false);
            var parameterPosition = method.ParameterLocals.IndexOf(receiver);
            Reject(() => method.ParameterLocals.Remove(receiver), () => method.ParameterLocals.Insert(parameterPosition, receiver));
            Reject(() => method.ParameterLocals.Add(receiver), () => method.ParameterLocals.RemoveAt(method.ParameterLocals.Count - 1));
            var duplicate = new LocalVariable("duplicateReceiver", receiver.Register, receiver.Type);
            Reject(() => method.Locals.Add(duplicate), () => method.Locals.Remove(duplicate));
            var abi = method.ParameterOperands[0];
            Reject(() => method.ParameterOperands[0] = new Register(null, "rdx"), () => method.ParameterOperands[0] = abi);

            var captureRegister = capture.Register;
            var resultRegister = result.Register;
            Reject(() => capture.Register = captureRegister.Copy(captureRegister.Version + 1), () => capture.Register = captureRegister);
            Reject(() => result.Register = resultRegister.Copy(resultRegister.Version + 1), () => result.Register = resultRegister);
            Reject(() => capture.Type = app.SystemTypes.SystemInt32Type, () => capture.Type = field.FieldType);
            Reject(() => result.Type = app.SystemTypes.SystemInt64Type, () => result.Type = method.ReturnType);
            Reject(() => result.IsMethodInfo = true, () => result.IsMethodInfo = false);
            Reject(() => read.IntegerBitWidth = 32, () => read.IntegerBitWidth = 0);
            var readAddress = read.NativeAddress;
            Reject(() => read.NativeAddress++, () => read.NativeAddress = readAddress);
            Reject(() => conversion.SetOperand(1, result), () => conversion.SetOperand(1, capture));
            Reject(() => conversion.SetOperand(2, new Immediate(64)), () => conversion.SetOperand(2, new Immediate(32)));
            var signed = conversion.Operands[4];
            Reject(() => conversion.SetOperand(4, new Immediate(((Immediate)signed).Value ^ 1)), () => conversion.SetOperand(4, signed));
            Reject(() => conversion.IntegerBitWidth = 32, () => conversion.IntegerBitWidth = 0);
            Reject(() => ret.SetOperand(0, capture), () => ret.SetOperand(0, result));
            Reject(() => block.Instructions.Remove(read), () => block.Instructions.Insert(0, read));
            Reject(() =>
            {
                block.Instructions.Remove(conversion);
                block.Instructions.Insert(0, conversion);
            }, () =>
            {
                block.Instructions.Remove(conversion);
                block.Instructions.Insert(1, conversion);
            });
            var extra = new Instruction(-1, OpCode.Move, access, capture) { NativeAddress = read.NativeAddress };
            Reject(() => block.Instructions.Insert(1, extra), () => block.Instructions.Remove(extra));
            var escape = new Instruction(-1, OpCode.Move, result, new AddressOf(capture));
            Reject(() => block.Instructions.Insert(1, escape), () => block.Instructions.Remove(escape));
            var detached = new Block { Instructions = [extra] };
            Reject(() => graph.Blocks.Add(detached), () => graph.Blocks.Remove(detached));
            Reject(() => graph.EntryBlock.Instructions.Add(extra), () => graph.EntryBlock.Instructions.Remove(extra));

            var proof = X64WideFieldLow32ReadProof.GetEvidence(method)!;
            Reject(() => method.PutExtraData<X64WideFieldLow32ReadProof.Proof>(X64WideFieldLow32ReadProof.EvidenceKey, null!),
                () => method.PutExtraData(X64WideFieldLow32ReadProof.EvidenceKey, proof));
            var binding = method.GetExtraData<object>("WideFieldLow32ReadRecovery")!;
            Reject(() => method.PutExtraData<object>("WideFieldLow32ReadRecovery", null!),
                () => method.PutExtraData("WideFieldLow32ReadRecovery", binding));
            Reject(() =>
            {
                method.PutExtraData<X64WideFieldLow32ReadProof.Proof>(X64WideFieldLow32ReadProof.EvidenceKey, null!);
                method.PutExtraData<object>("WideFieldLow32ReadRecovery", null!);
                capture.Register = new Register(null, "ordinary_capture", captureRegister.Version);
                result.Register = new Register(null, "ordinary_result", resultRegister.Version);
                read.NativeAddress++;
                Assert.That(WideFieldLow32ReadRecovery.HasEvidence(method), Is.True,
                    "A successful native admission cannot lose its final guard when mutable evidence is removed.");
            }, () =>
            {
                capture.Register = captureRegister;
                result.Register = resultRegister;
                read.NativeAddress = readAddress;
                method.PutExtraData(X64WideFieldLow32ReadProof.EvidenceKey, proof);
                method.PutExtraData("WideFieldLow32ReadRecovery", binding);
            });
            var bytes = method.RawBytes;
            Reject(() =>
            {
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }, () => method.RawBytes = bytes);
            var interior = method.UnderlyingPointer + 1;
            Reject(() => app.MethodsByAddress.Add(interior, [method]), () => app.MethodsByAddress.Remove(interior));

            var pe = (PE)app.Binary;
            var imagePosition = pe.BaseStream.Position;
            var imageOffset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer);
            var originalByte = pe.GetRawBinaryContent()[checked((int)imageOffset)];
            Reject(() =>
            {
                pe.BaseStream.Position = imageOffset;
                pe.BaseStream.WriteByte((byte)(originalByte ^ 1));
            }, () =>
            {
                pe.BaseStream.Position = imageOffset;
                pe.BaseStream.WriteByte(originalByte);
                pe.BaseStream.Position = imagePosition;
            });
            var sizePointer = app.Binary.TypeDefinitionSizePointers[owner.Definition!.TypeIndex!.Value];
            var sizeOffset = pe.MapVirtualAddressToRaw(sizePointer);
            var originalSize = pe.GetRawBinaryContent().Slice(checked((int)sizeOffset), 4).ToArray();
            Reject(() =>
            {
                pe.BaseStream.Position = sizeOffset;
                var truncatedExtent = BitConverter.GetBytes((uint)(field.Offset + 4));
                pe.BaseStream.Write(truncatedExtent, 0, truncatedExtent.Length);
            }, () =>
            {
                pe.BaseStream.Position = sizeOffset;
                pe.BaseStream.Write(originalSize, 0, originalSize.Length);
                pe.BaseStream.Position = imagePosition;
            });

            var nativeCount = proof.Native.Load.IP == method.UnderlyingPointer ? 2 : 3;
            var native = X64NativeInstructionReader.Read(pe, X64UnwindProof.ForApplication(app)!,
                method.UnderlyingPointer, nativeCount, 32)!;
            foreach (var target in new[] { method.UnderlyingPointer - 7, method.UnderlyingPointer,
                         proof.Native.Return.NextIP - 1 })
            {
                // Initialize the PE and index after adding DIR64. This protects
                // the consumed code guard independently of cache invalidation.
                var changed = WithRelocation(pe, target, out var relocationOffset);
                using var changedStream = new MemoryStream(changed, 0, changed.Length, writable: true, publiclyVisible: true);
                using var changedPe = new PE(changedStream);
                var changedIndex = X64UnwindProof.ForBinary(changedPe);
                Assert.That(changedIndex, Is.Not.Null);
                Assert.That(changedIndex!.ClassifySpan(method.UnderlyingPointer, proof.Native.Return.NextIP).Kind,
                    Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
                var start = checked((int)changedPe.MapVirtualAddressToRaw(method.UnderlyingPointer));
                var unchangedEncodedBody = X86Utils.Iterate(changedPe.GetRawBinaryContent().Slice(start, 32).ToArray(),
                    method.UnderlyingPointer, false).Take(nativeCount).ToArray();
                Assert.That(unchangedEncodedBody, Is.EqualTo(native));
                Assert.That(X64WideFieldLow32ReadProof.TryProveShape(unchangedEncodedBody), Is.Not.Null);
                Assert.That(X64NativeInstructionReader.Read(changedPe, changedIndex, method.UnderlyingPointer, nativeCount, 32), Is.Null);
                var oldRelocation = pe.GetRawBinaryContent().Slice(relocationOffset, 10).ToArray();
                Reject(() =>
                {
                    pe.BaseStream.Position = relocationOffset;
                    pe.BaseStream.Write(changed, relocationOffset, 10);
                }, () =>
                {
                    pe.BaseStream.Position = relocationOffset;
                    pe.BaseStream.Write(oldRelocation, 0, oldRelocation.Length);
                    pe.BaseStream.Position = imagePosition;
                });
            }

            IlGenerator.GenerateIl(method, definition);
            var expected = name == "ReadSigned" ? CilOpCodes.Conv_I4 : CilOpCodes.Conv_U4;
            Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(1));
            Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == expected), Is.EqualTo(1));
            Assert.That(definition.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.False);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static byte[] WithRelocation(PE pe, ulong target, out int offset)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + 5 * 8, 4));
        offset = checked((int)pe.MapVirtualAddressToRaw(imageBase + rva));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset + 4, 4)), Is.GreaterThanOrEqualTo(10));
        var targetRva = checked((uint)(target - imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset, 4), targetRva & ~0xFFFU);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 8, 2), (ushort)(0xA000U | (targetRva & 0xFFFU)));
        return image;
    }
}
