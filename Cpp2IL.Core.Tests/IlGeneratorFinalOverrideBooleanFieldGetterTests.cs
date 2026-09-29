using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorFinalOverrideBooleanFieldGetterTests
{
    [Test]
    public void MissingMetadataCannotProveFinalOverride()
    {
        Assert.That(X64FinalOverrideBooleanFieldGetterProof.Find(null), Is.Null);
    }

    [Test]
    public void ExactFinalOverrideBindsInheritedSlotAndOnlyOriginalField()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FINAL_OVERRIDE_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FINAL_OVERRIDE_BOOLEAN_GETTER_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("FinalOverrideBooleanGetterFixture")!;
            Assert.That(assembly.Types.SelectMany(type => type.Methods).Count(), Is.EqualTo(6));
            var owner = assembly.Types.Single(type => type.Name == "FlagState");
            var parent = assembly.Types.Single(type => type.Name == "FlagBase");
            var shadow = assembly.Types.Single(type => type.Name == "ShadowState");
            var method = owner.Methods.Single(candidate => candidate.Name == "get_Value");
            var declaration = parent.Methods.Single(candidate => candidate.Name == "get_Value");
            var shadowGetter = shadow.Methods.Single(candidate => candidate.Name == "get_Value");
            Assert.Multiple(() =>
            {
                Assert.That(declaration.IsAbstract, Is.True);
                Assert.That(declaration.UnderlyingPointer, Is.Zero);
                Assert.That(method.IsFinal && method.IsVirtual && !method.IsNewSlot, Is.True);
                Assert.That(ReferenceEquals(method.BaseMethod, declaration), Is.True);
                Assert.That(method.Definition!.slot, Is.EqualTo(declaration.Definition!.slot));
                Assert.That(shadowGetter.IsVirtual, Is.False);
                Assert.That(X64FinalOverrideBooleanFieldGetterProof.Find(declaration), Is.Null);
                Assert.That(X64FinalOverrideBooleanFieldGetterProof.Find(shadowGetter), Is.Null);
            });
            method.EnsureRawBytes();
            Assert.That(method.RawBytes.Length, Is.EqualTo(5));
            Assert.That(X64FinalOverrideBooleanFieldGetterProof.Find(method), Is.Not.Null);
            Assert.That(X86DirectBooleanFieldGetterProof.Find(method, X86Utils.Iterate(method).ToArray()),
                Is.Null, "The ordinary getter path must continue rejecting final virtual overrides.");
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(X64FinalOverrideBooleanFieldGetterProof.WasLifted(method), Is.True);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                Is.EqualTo(1));
            foreach (var mutation in new[]
            {
                "nonfinal", "nonvirtual", "newslot", "raw-flags", "method-slot", "base-slot",
                "base-final", "base-return", "base-name", "owner-vtable", "base-vtable",
                "owner-cctor", "owner-layout", "owner-parent", "owner-name", "property-name",
                "owner-interface-count", "base-interface-offset-count",
                "field-offset", "field-kind", "sibling-overlap", "signature-return",
                "capture-type", "receiver-type", "receiver-copy", "receiver-slot", "capture-site",
                "return-site", "read-offset", "read-width", "extra-effect", "detached-block",
                "cached-native-byte", "current-native-byte", "interior-entry", "missing-proof",
                "missing-all-markers", "return-copy"
            })
                RejectMutation(method, definition, mutation);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition,
        string mutation)
    {
        var graph = method.ControlFlowGraph!;
        var read = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Move);
        var ret = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return);
        var capture = (LocalVariable)read.Operands[0];
        var access = (FieldReference)read.Operands[1];
        var receiver = access.Local;
        var owner = method.DeclaringType!;
        var originalOwner = owner.Definition!;
        var parent = owner.BaseType!;
        var baseMethod = method.BaseMethod!;
        var property = owner.Properties.Single(candidate => ReferenceEquals(candidate.Getter, method));
        var sibling = owner.Fields.First(field => field != access.Field && !field.IsStatic);
        var body = graph.FindBlockByInstruction(read)!;
        var blocks = graph.Blocks.ToArray();
        var instructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var parameterOperands = method.ParameterOperands.ToArray();
        var proof = X64FinalOverrideBooleanFieldGetterProof.GetEvidence(method)!;
        var savedCaptureType = capture.Type;
        var savedReceiverType = receiver.Type;
        var savedReceiver = access.Local;
        var savedOffset = access.Offset;
        var savedReturnOperand = ret.Operands[0];
        var savedReadAddress = read.NativeAddress;
        var savedReturnAddress = ret.NativeAddress;
        var savedRawBytes = method.RawBytes;
        var methodFlags = method.Definition!.flags;
        var ownerBitfield = originalOwner.Bitfield;
        var ownerInterfaceCount = originalOwner.InterfacesCount;
        var baseInterfaceOffsetCount = parent.Definition!.InterfaceOffsetsCount;
        var methodSlot = method.Definition.slot;
        var baseSlot = baseMethod.Definition!.slot;
        var ownIndex = originalOwner.VtableStart + methodSlot;
        var baseIndex = parent.Definition!.VtableStart + baseSlot;
        var ownEntry = method.AppContext.Metadata.VTableMethodIndices[ownIndex];
        var baseEntry = method.AppContext.Metadata.VTableMethodIndices[baseIndex];
        var pe = (PE)method.AppContext.Binary;
        var streamPosition = pe.BaseStream.Position;
        var nativeOffset = -1L;
        byte nativeByte = 0;
        try
        {
            switch (mutation)
            {
                case "nonfinal": method.OverrideAttributes = method.DefaultAttributes & ~MethodAttributes.Final; break;
                case "nonvirtual": method.OverrideAttributes = method.DefaultAttributes & ~MethodAttributes.Virtual; break;
                case "newslot": method.OverrideAttributes = method.DefaultAttributes | MethodAttributes.NewSlot; break;
                case "raw-flags": method.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "method-slot": method.Definition.slot = ushort.MaxValue; break;
                case "base-slot": baseMethod.Definition.slot = ushort.MaxValue; break;
                case "base-final": baseMethod.OverrideAttributes = baseMethod.DefaultAttributes | MethodAttributes.Final; break;
                case "base-return": baseMethod.OverrideReturnType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "base-name": baseMethod.Name += "Changed"; break;
                case "owner-vtable": method.AppContext.Metadata.VTableMethodIndices[ownIndex] = 0; break;
                case "base-vtable": method.AppContext.Metadata.VTableMethodIndices[baseIndex] = ownEntry; break;
                case "owner-cctor": originalOwner.Bitfield |= 8; break;
                case "owner-interface-count": originalOwner.InterfacesCount++; break;
                case "base-interface-offset-count": parent.Definition!.InterfaceOffsetsCount++; break;
                case "owner-layout": owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "owner-parent": owner.OverrideBaseType = method.AppContext.SystemTypes.SystemObjectType; break;
                case "owner-name": owner.Name += "Changed"; break;
                case "property-name": property.Name += "Changed"; break;
                case "field-offset": access.Field.OverrideOffset = access.Field.DefaultOffset + 1; break;
                case "field-kind": access.Field.BackingData!.Field.RawFieldType!.Type =
                    LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_I1; break;
                case "sibling-overlap": sibling.OverrideOffset = access.Field.Offset; break;
                case "signature-return": method.OverrideReturnType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "capture-type": capture.Type = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "receiver-type": receiver.Type = method.AppContext.SystemTypes.SystemObjectType; break;
                case "receiver-copy": access.Local = new LocalVariable("copy", receiver.Register, receiver.Type); break;
                case "receiver-slot": method.ParameterOperands[0] = new Register(0, "rdx"); break;
                case "capture-site": read.NativeAddress++; break;
                case "return-site": ret.NativeAddress++; break;
                case "read-offset": access.Offset++; break;
                case "read-width": read.IntegerBitWidth = 0; break;
                case "extra-effect": body.Instructions.Insert(1, new Instruction(999, OpCode.Move, access, capture)); break;
                case "detached-block": graph.Blocks.Add(new Block
                    { Instructions = [new Instruction(999, OpCode.Move, access, capture)] }); break;
                case "cached-native-byte":
                    var changed = savedRawBytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "current-native-byte":
                    nativeOffset = pe.MapVirtualAddressToRaw(proof.LoadIp);
                    nativeByte = pe.GetByteAtRawAddress((ulong)nativeOffset);
                    pe.BaseStream.Position = nativeOffset;
                    pe.BaseStream.WriteByte((byte)(nativeByte ^ 1));
                    break;
                case "interior-entry": method.AppContext.MethodsByAddress[method.UnderlyingPointer + 1] = [method]; break;
                case "missing-proof":
                    method.PutExtraData<X64FinalOverrideBooleanFieldGetterProof.Proof>(
                        X64FinalOverrideBooleanFieldGetterProof.EvidenceKey, null!);
                    break;
                case "missing-all-markers":
                    method.PutExtraData<X64FinalOverrideBooleanFieldGetterProof.Proof>(
                        X64FinalOverrideBooleanFieldGetterProof.EvidenceKey, null!);
                    capture.Register.Name = "renamed_capture";
                    break;
                case "return-copy": ret.SetOperand(0, new LocalVariable("copy", capture.Register, capture.Type)); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, definition), mutation);
        }
        finally
        {
            method.OverrideAttributes = null;
            method.Definition.flags = methodFlags;
            method.Definition.slot = methodSlot;
            baseMethod.Definition.slot = baseSlot;
            baseMethod.OverrideAttributes = null;
            baseMethod.OverrideReturnType = null;
            baseMethod.Name = baseMethod.DefaultName;
            method.AppContext.Metadata.VTableMethodIndices[ownIndex] = ownEntry;
            method.AppContext.Metadata.VTableMethodIndices[baseIndex] = baseEntry;
            originalOwner.Bitfield = ownerBitfield;
            originalOwner.InterfacesCount = ownerInterfaceCount;
            parent.Definition!.InterfaceOffsetsCount = baseInterfaceOffsetCount;
            owner.OverrideAttributes = null;
            owner.OverrideBaseType = null;
            owner.Name = owner.DefaultName;
            property.Name = property.DefaultName;
            access.Field.OverrideOffset = null;
            access.Field.BackingData!.Field.RawFieldType!.Type =
                LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN;
            sibling.OverrideOffset = null;
            method.OverrideReturnType = null;
            capture.Type = savedCaptureType;
            capture.Register.Name = X64FinalOverrideBooleanFieldGetterProof.CaptureRegister;
            receiver.Type = savedReceiverType;
            access.Local = savedReceiver;
            access.Offset = savedOffset;
            ret.SetOperand(0, savedReturnOperand);
            read.NativeAddress = savedReadAddress;
            ret.NativeAddress = savedReturnAddress;
            read.IntegerBitWidth = 8;
            method.RawBytes = savedRawBytes;
            graph.Blocks.Clear(); graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear();
                blocks[index].Instructions.AddRange(instructions[index]);
            }
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(parameterOperands);
            method.PutExtraData(X64FinalOverrideBooleanFieldGetterProof.EvidenceKey, proof);
            method.AppContext.MethodsByAddress.Remove(method.UnderlyingPointer + 1);
            if (nativeOffset >= 0)
            {
                pe.BaseStream.Position = nativeOffset;
                pe.BaseStream.WriteByte(nativeByte);
            }
            pe.BaseStream.Position = streamPosition;
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), $"Restored {mutation}");
    }
}
