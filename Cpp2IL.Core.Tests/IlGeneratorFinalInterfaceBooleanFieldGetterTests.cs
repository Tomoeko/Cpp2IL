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
public class IlGeneratorFinalInterfaceBooleanFieldGetterTests
{
    [Test]
    public void MissingMetadataCannotProveFinalInterfaceDispatch()
    {
        Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(null), Is.Null);
        Assert.That(X64FinalInterfaceBooleanFieldGetterProof.HasUnchangedDispatch(null), Is.False);
    }

    [Test]
    public void ExactFinalInterfaceGettersBindFieldsSlotsAndFreshFinalOperations()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FINAL_INTERFACE_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FINAL_INTERFACE_BOOLEAN_GETTER_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("FinalInterfaceBooleanGetterFixture")!;
            Assert.That(assembly.Types.SelectMany(type => type.Methods).Count(), Is.EqualTo(11));
            var methods = assembly.Types.Where(type => type.Name is "FlagState" or "ExplicitFlagState")
                .SelectMany(type => type.Methods).Where(method => method.IsVirtual && method.IsFinal).ToArray();
            Assert.That(methods, Has.Length.EqualTo(4));
            foreach (var method in methods)
            {
                method.EnsureRawBytes();
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(method), Is.Not.Null);
                Assert.That(X86DirectBooleanFieldGetterProof.Find(method, X86Utils.Iterate(method).ToArray()), Is.Null,
                    "The independent final-interface route must not relax ordinary virtual getter eligibility.");
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.GetEvidence(method), Is.Not.Null);
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(1));
                foreach (var mutation in new[]
                {
                    "nonfinal", "nonvirtual", "not-newslot", "raw-flags", "raw-name-index", "slot", "vtable", "mapping-offset",
                    "removed-interface", "added-interface", "removed-override", "added-override", "interface-slot",
                    "interface-return", "interface-name", "interface-attributes", "owner-cctor", "owner-layout", "object-raw-kind",
                    "owner-name", "base-type", "field-offset", "field-type", "field-attributes", "field-name",
                    "sibling-overlap", "signature-return", "capture-type", "receiver-type", "receiver-copy",
                    "receiver-slot-name", "receiver-slot-number", "receiver-slot-version", "duplicate-receiver",
                    "receiver-methodinfo", "capture-site", "ret-site", "read-offset", "read-width", "extra-effect",
                    "removed-read", "entry-marker", "exit-marker", "detached-block", "cached-native-byte",
                    "current-native-byte", "interior-entry", "missing-proof", "capture-version", "return-copy",
                })
                    RejectMutation(method, definition, mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        var graph = method.ControlFlowGraph!;
        var read = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Move);
        var ret = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return);
        var capture = (LocalVariable)read.Operands[0];
        var access = (FieldReference)read.Operands[1];
        var receiver = access.Local;
        var owner = method.DeclaringType!;
        var ownerDefinition = owner.Definition!;
        var contract = owner.InterfaceContexts.Single();
        var declaration = method.Overrides.Single();
        var mapping = ownerDefinition.InterfaceOffsets.Single();
        var sibling = owner.Fields.First(field => field != access.Field && !field.IsStatic);
        var body = graph.FindBlockByInstruction(read)!;
        var blocks = graph.Blocks.ToArray();
        var instructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var parameterLocals = method.ParameterLocals.ToArray();
        var parameterOperands = method.ParameterOperands.ToArray();
        var interfaces = owner.InterfaceContexts.ToArray();
        var overrides = method.Overrides.ToArray();
        var proof = X64FinalInterfaceBooleanFieldGetterProof.GetEvidence(method)!;
        var captureRegister = capture.Register;
        var captureType = capture.Type;
        var receiverType = receiver.Type;
        var receiverMethodInfo = receiver.IsMethodInfo;
        var fieldLocal = access.Local;
        var fieldOffset = access.Offset;
        var readAddress = read.NativeAddress;
        var returnAddress = ret.NativeAddress;
        var returnOperand = ret.Operands[0];
        var rawBytes = method.RawBytes;
        var flags = method.Definition!.flags;
        var methodNameIndex = method.Definition.nameIndex;
        var objectRawType = method.AppContext.SystemTypes.SystemObjectType.Definition!.RawType;
        var objectRawKind = objectRawType.Type;
        var slot = method.Definition.slot;
        var interfaceSlot = declaration.Definition!.slot;
        var bitfield = ownerDefinition.Bitfield;
        var mappingOffset = mapping.offset;
        var vtableIndex = ownerDefinition.VtableStart + slot;
        var vtableEntry = method.AppContext.Metadata.VTableMethodIndices[vtableIndex];
        var pe = (PE)method.AppContext.Binary;
        var position = pe.BaseStream.Position;
        var nativeOffset = -1L;
        var nativeByte = (byte)0;
        try
        {
            switch (mutation)
            {
                case "nonfinal": method.OverrideAttributes = method.DefaultAttributes & ~MethodAttributes.Final; break;
                case "nonvirtual": method.OverrideAttributes = method.DefaultAttributes & ~MethodAttributes.Virtual; break;
                case "not-newslot": method.OverrideAttributes = method.DefaultAttributes & ~MethodAttributes.NewSlot; break;
                case "raw-flags": method.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "raw-name-index": method.Definition.nameIndex++; break;
                case "object-raw-kind": objectRawType.Type = LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_CLASS; break;
                case "slot": method.Definition.slot = ushort.MaxValue; break;
                case "vtable": method.AppContext.Metadata.VTableMethodIndices[vtableIndex] = 0; break;
                case "mapping-offset": mapping.offset++; break;
                case "removed-interface": owner.InterfaceContexts.Clear(); break;
                case "added-interface": owner.InterfaceContexts.Add(contract); break;
                case "removed-override": method.Overrides.Clear(); break;
                case "added-override": method.Overrides.Add(declaration); break;
                case "interface-slot": declaration.Definition.slot = ushort.MaxValue; break;
                case "interface-return": declaration.OverrideReturnType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "interface-name": declaration.Name += "Changed"; break;
                case "interface-attributes": contract.OverrideAttributes = contract.DefaultAttributes & ~TypeAttributes.Interface; break;
                case "owner-cctor": ownerDefinition.Bitfield |= 8; break;
                case "owner-layout": owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "owner-name": owner.Name += "Changed"; break;
                case "base-type": owner.OverrideBaseType = contract; break;
                case "field-offset": access.Field.OverrideOffset = access.Field.DefaultOffset + 1; break;
                case "field-type": access.Field.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "field-attributes": access.Field.OverrideAttributes = access.Field.DefaultAttributes | FieldAttributes.Static; break;
                case "field-name": access.Field.Name += "Changed"; break;
                case "sibling-overlap": sibling.OverrideOffset = access.Field.Offset; break;
                case "signature-return": method.OverrideReturnType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "capture-type": capture.Type = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "receiver-type": receiver.Type = method.AppContext.SystemTypes.SystemObjectType; break;
                case "receiver-copy": access.Local = new LocalVariable("copy", receiver.Register, receiver.Type); break;
                case "receiver-slot-name":
                    var original = (Register)parameterOperands[0];
                    method.ParameterOperands[0] = new Register(original.Number, "rdx", original.Version);
                    break;
                case "receiver-slot-number": method.ParameterOperands[0] = new Register(0, "rcx"); break;
                case "receiver-slot-version": method.ParameterOperands[0] = ((Register)parameterOperands[0]).Copy(5); break;
                case "duplicate-receiver": method.ParameterLocals.Add(new LocalVariable("duplicate", receiver.Register, owner)); break;
                case "receiver-methodinfo": receiver.IsMethodInfo = true; break;
                case "capture-site": read.NativeAddress++; break;
                case "ret-site": ret.NativeAddress++; break;
                case "read-offset": access.Offset++; break;
                case "read-width": read.IntegerBitWidth = 0; break;
                case "extra-effect": body.Instructions.Insert(1, new Instruction(999, OpCode.Move, access, capture)); break;
                case "removed-read": body.Instructions.Remove(read); break;
                case "entry-marker": body.Instructions.Remove(read); graph.EntryBlock.Instructions.Add(read); break;
                case "exit-marker": graph.ExitBlock.Instructions.Add(new Instruction(999, OpCode.Move, access, capture)); break;
                case "detached-block": graph.Blocks.Add(new Block { Instructions = [new Instruction(999, OpCode.Move, access, capture)] }); break;
                case "cached-native-byte":
                    var changed = rawBytes.AsSpan().ToArray();
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
                case "missing-proof": method.PutExtraData<X64FinalInterfaceBooleanFieldGetterProof.Proof>(X64FinalInterfaceBooleanFieldGetterProof.EvidenceKey, null!); break;
                case "capture-version": capture.Register.Version++; break;
                case "return-copy": ret.SetOperand(0, new LocalVariable("copy", capture.Register, capture.Type)); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, definition), mutation);
        }
        finally
        {
            method.OverrideAttributes = null;
            method.Definition.flags = flags;
            method.Definition.nameIndex = methodNameIndex;
            objectRawType.Type = objectRawKind;
            method.Definition.slot = slot;
            declaration.Definition.slot = interfaceSlot;
            declaration.OverrideReturnType = null;
            declaration.Name = declaration.DefaultName;
            contract.OverrideAttributes = null;
            ownerDefinition.Bitfield = bitfield;
            mapping.offset = mappingOffset;
            method.AppContext.Metadata.VTableMethodIndices[vtableIndex] = vtableEntry;
            owner.InterfaceContexts.Clear(); owner.InterfaceContexts.AddRange(interfaces);
            method.Overrides.Clear(); method.Overrides.AddRange(overrides);
            owner.OverrideAttributes = null;
            owner.Name = owner.DefaultName;
            owner.OverrideBaseType = null;
            access.Field.OverrideOffset = null;
            access.Field.OverrideFieldType = null;
            access.Field.OverrideAttributes = null;
            access.Field.Name = access.Field.DefaultName;
            sibling.OverrideOffset = null;
            method.OverrideReturnType = null;
            capture.Type = captureType;
            capture.Register = captureRegister;
            receiver.Type = receiverType;
            receiver.IsMethodInfo = receiverMethodInfo;
            access.Local = fieldLocal;
            access.Offset = fieldOffset;
            read.NativeAddress = readAddress;
            read.IntegerBitWidth = 8;
            ret.NativeAddress = returnAddress;
            ret.SetOperand(0, returnOperand);
            method.RawBytes = rawBytes;
            graph.Blocks.Clear(); graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear(); blocks[index].Instructions.AddRange(instructions[index]);
            }
            method.ParameterLocals.Clear(); method.ParameterLocals.AddRange(parameterLocals);
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(parameterOperands);
            method.PutExtraData(X64FinalInterfaceBooleanFieldGetterProof.EvidenceKey, proof);
            method.AppContext.MethodsByAddress.Remove(method.UnderlyingPointer + 1);
            if (nativeOffset >= 0) { pe.BaseStream.Position = nativeOffset; pe.BaseStream.WriteByte(nativeByte); }
            pe.BaseStream.Position = position;
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), $"Restored {mutation}");
    }
}
