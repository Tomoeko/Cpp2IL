using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorSmallAggregateFieldGetterTests
{
    [Test]
    public void ExactByValueAggregatesKeepTheirFieldTypesAndFreshNativeProvenance()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SMALL_AGGREGATE_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SMALL_AGGREGATE_GETTER_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var methods = app.GetAssemblyByName("SmallAggregateGetterFixture")!.Types.SelectMany(type => type.Methods).ToArray();
            Assert.That(methods, Has.Length.EqualTo(8));
            Assert.That(methods.Count(method => method.Name == "op_Implicit"), Is.EqualTo(1));
            foreach (var method in methods)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                var proof = X64SmallAggregateFieldGetterProof.GetEvidence(method);
                Assert.That(proof, Is.Not.Null);
                Assert.That(proof!.Widen, Is.EqualTo(method.Name.StartsWith("Widen", StringComparison.Ordinal)));
                Assert.That(proof.Native.Load.Op1Kind, Is.EqualTo(Iced.Intel.OpKind.Register));
                Assert.That(proof.Field.Offset, Is.Zero);
                Assert.That(method.ParameterLocals, Has.Count.EqualTo(1));
                Assert.That(method.ParameterLocals[0].Type, Is.SameAs(proof.Parameter.ParameterType));
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(1));
                Assert.That(method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.IntegerExtend),
                    Is.EqualTo(proof.Widen ? 1 : 0));
                foreach (var mutation in new[]
                {
                    "field-offset", "field-type", "field-boolean", "field-char", "field-raw-boolean", "field-raw-char",
                    "field-raw-aggregate", "field-attributes",
                    "field-raw-attributes", "field-name", "field-name-index", "added-field", "layout", "raw-layout",
                    "base-type", "aggregate-extent", "aggregate-native-size", "aggregate-cctor", "aggregate-blittable",
                    "aggregate-byref-like", "aggregate-raw-kind", "aggregate-modifier", "owner-cctor", "method-flags",
                    "method-impl-flags", "method-name-index", "signature-return", "parameter-type", "parameter-out",
                    "parameter-byref", "parameter-modifier", "parameter-raw-attributes", "parameter-name-index",
                    "return-raw-attributes", "capture-type", "incoming-type", "incoming-copy", "incoming-this",
                    "incoming-methodinfo", "missing-incoming", "duplicate-incoming", "duplicate-incoming-local",
                    "forged-incoming-local", "slot-name", "slot-number",
                    "slot-version", "metadata-slot", "capture-site", "ret-site", "read-offset", "read-width",
                    "extra-effect", "removed-read", "entry-marker", "exit-marker", "detached-block", "changed-edge",
                    "reordered-body", "cached-native-byte", "current-native-byte", "interior-entry", "missing-proof",
                    "missing-binding", "lost-both-and-register", "capture-version", "return-copy",
                })
                    RejectMutation(method, definition, mutation);
                if (proof.Widen)
                    foreach (var mutation in new[] { "extension-width", "extension-result-width", "extension-sign",
                        "extension-source", "extension-result-type", "extension-site", "result-version" })
                        RejectMutation(method, definition, mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        const string bindingKey = "SmallAggregateFieldGetterRecovery";
        var graph = method.ControlFlowGraph!;
        var read = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Move);
        var ret = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return);
        var extension = graph.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCode.IntegerExtend);
        var capture = (LocalVariable)read.Operands[0];
        var access = (FieldReference)read.Operands[1];
        var field = access.Field;
        var aggregate = field.DeclaringType;
        var owner = method.DeclaringType!;
        var incoming = access.Local;
        var parameter = method.Parameters.Single();
        var proof = X64SmallAggregateFieldGetterProof.GetEvidence(method)!;
        var binding = method.GetExtraData<object>(bindingKey)!;
        var body = graph.FindBlockByInstruction(read)!;
        var blocks = graph.Blocks.ToArray();
        var instructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var successors = body.Successors.ToArray();
        var fields = aggregate.Fields.ToArray();
        var parameterLocals = method.ParameterLocals.ToArray();
        var locals = method.Locals.ToArray();
        var parameterOperands = method.ParameterOperands.ToArray();
        var result = (LocalVariable)ret.Operands[0];
        var captureType = capture.Type;
        var incomingType = incoming.Type;
        var incomingThis = incoming.IsThis;
        var incomingMethodInfo = incoming.IsMethodInfo;
        var accessLocal = access.Local;
        var accessOffset = access.Offset;
        var readAddress = read.NativeAddress;
        var readWidth = read.IntegerBitWidth;
        var returnAddress = ret.NativeAddress;
        var returnOperand = ret.Operands[0];
        var rawBytes = method.RawBytes;
        var captureRegister = capture.Register;
        var resultRegister = result.Register;
        var resultType = result.Type;
        var methodFlags = method.Definition!.flags;
        var methodImplFlags = method.Definition.iflags;
        var methodNameIndex = method.Definition.nameIndex;
        var aggregateBitfield = aggregate.Definition!.Bitfield;
        var aggregateFlags = aggregate.Definition.Flags;
        var ownerBitfield = owner.Definition!.Bitfield;
        var aggregateRawKind = aggregate.Definition.RawType.Type;
        var aggregateModifiers = aggregate.Definition.RawType.NumMods;
        var rawField = field.BackingData!.Field.RawFieldType!;
        var rawFieldAttributes = rawField.Attrs;
        var rawFieldKind = rawField.Type;
        var rawFieldData = rawField.Data.Dummy;
        var fieldNameIndex = field.BackingData.Field.nameIndex;
        var rawParameter = parameter.Definition!.RawType!;
        var parameterByref = rawParameter.Byref;
        var parameterModifiers = rawParameter.NumMods;
        var parameterRawAttributes = rawParameter.Attrs;
        var parameterNameIndex = parameter.Definition.nameIndex;
        var rawReturn = method.Definition.RawReturnType!;
        var returnRawAttributes = rawReturn.Attrs;
        var extensionOperands = extension?.Operands.ToArray();
        var extensionAddress = extension?.NativeAddress;
        var pe = (PE)method.AppContext.Binary;
        var streamPosition = pe.BaseStream.Position;
        var nativeOffset = -1L;
        var nativeBytes = Array.Empty<byte>();
        try
        {
            switch (mutation)
            {
                case "field-offset": field.OverrideOffset = 1; break;
                case "field-type": field.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "field-boolean": field.OverrideFieldType = method.AppContext.SystemTypes.SystemBooleanType; break;
                case "field-char": field.OverrideFieldType = method.AppContext.SystemTypes.SystemCharType; break;
                case "field-raw-boolean": rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN; break;
                case "field-raw-char": rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_CHAR; break;
                case "field-raw-aggregate": rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE; rawField.Data.Dummy = checked((ulong)aggregate.Definition.TypeIndex.Value); break;
                case "field-attributes": field.OverrideAttributes = field.DefaultAttributes | FieldAttributes.HasFieldMarshal; break;
                case "field-raw-attributes": rawField.Attrs ^= (uint)FieldAttributes.InitOnly; break;
                case "field-name": field.Name += "Changed"; break;
                case "field-name-index": field.BackingData.Field.nameIndex++; break;
                case "added-field": aggregate.Fields.Add(new InjectedFieldAnalysisContext("Extra", field.FieldType, FieldAttributes.Public, aggregate, 0)); break;
                case "layout": aggregate.OverrideAttributes = aggregate.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "raw-layout": aggregate.Definition.Flags ^= (uint)TypeAttributes.BeforeFieldInit; break;
                case "base-type": aggregate.OverrideBaseType = method.AppContext.SystemTypes.SystemObjectType; break;
                case "aggregate-extent": ChangeSize(0); break;
                case "aggregate-native-size": ChangeSize(4); break;
                case "aggregate-cctor": aggregate.Definition.Bitfield |= 1U << 3; break;
                case "aggregate-blittable": aggregate.Definition.Bitfield &= ~(1U << 4); break;
                case "aggregate-byref-like": aggregate.Definition.Bitfield |= 1U << 16; break;
                case "aggregate-raw-kind": aggregate.Definition.RawType.Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS; break;
                case "aggregate-modifier": aggregate.Definition.RawType.NumMods = 1; break;
                case "owner-cctor": owner.Definition.Bitfield |= 1U << 3; break;
                case "method-flags": method.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "method-impl-flags": method.Definition.iflags ^= (ushort)MethodImplAttributes.NoInlining; break;
                case "method-name-index": method.Definition.nameIndex++; break;
                case "signature-return": method.OverrideReturnType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "parameter-type": parameter.OverrideParameterType = method.AppContext.SystemTypes.SystemInt16Type; break;
                case "parameter-out": parameter.OverrideAttributes = parameter.DefaultAttributes | ParameterAttributes.Out; break;
                case "parameter-byref": rawParameter.Byref = 1; break;
                case "parameter-modifier": rawParameter.NumMods = 1; break;
                case "parameter-raw-attributes": rawParameter.Attrs ^= (uint)ParameterAttributes.In; break;
                case "parameter-name-index": parameter.Definition.nameIndex++; break;
                case "return-raw-attributes": rawReturn.Attrs ^= (uint)ParameterAttributes.In; break;
                case "capture-type": capture.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "incoming-type": incoming.Type = field.FieldType; break;
                case "incoming-copy": access.Local = new LocalVariable("copy", incoming.Register, incoming.Type); break;
                case "incoming-this": incoming.IsThis = true; break;
                case "incoming-methodinfo": incoming.IsMethodInfo = true; break;
                case "missing-incoming": method.ParameterLocals.Clear(); break;
                case "duplicate-incoming": method.ParameterLocals.Add(new LocalVariable("duplicate", incoming.Register, incoming.Type)); break;
                case "duplicate-incoming-local": method.Locals.Add(incoming); method.Locals.Add(incoming); break;
                case "forged-incoming-local": method.Locals.Add(new LocalVariable("forged", incoming.Register, incoming.Type)); break;
                case "slot-name": method.ParameterOperands[0] = new Register(((Register)parameterOperands[0]).Number, "rdx"); break;
                case "slot-number": method.ParameterOperands[0] = new Register(0, "rcx"); break;
                case "slot-version": method.ParameterOperands[0] = ((Register)parameterOperands[0]).Copy(1); break;
                case "metadata-slot": method.ParameterOperands[1] = parameterOperands[0]; break;
                case "capture-site": read.NativeAddress++; break;
                case "ret-site": ret.NativeAddress++; break;
                case "read-offset": access.Offset = 1; break;
                case "read-width": read.IntegerBitWidth = proof.Native.Width; break;
                case "extra-effect": body.Instructions.Insert(1, new Instruction(999, OpCode.Move, access, capture)); break;
                case "removed-read": body.Instructions.Remove(read); break;
                case "entry-marker": body.Instructions.Remove(read); graph.EntryBlock.Instructions.Add(read); break;
                case "exit-marker": graph.ExitBlock.Instructions.Add(new Instruction(999, OpCode.Move, access, capture)); break;
                case "detached-block": graph.Blocks.Add(new Block { Instructions = [new Instruction(999, OpCode.Move, access, capture)] }); break;
                case "changed-edge": body.Successors.Add(graph.ExitBlock); break;
                case "reordered-body": body.Instructions.Remove(ret); body.Instructions.Insert(0, ret); break;
                case "cached-native-byte":
                    var changed = rawBytes.AsSpan().ToArray(); changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "current-native-byte":
                    nativeOffset = pe.MapVirtualAddressToRaw(proof.Native.Load.IP);
                    nativeBytes = [pe.GetByteAtRawAddress((ulong)nativeOffset)];
                    pe.BaseStream.Position = nativeOffset;
                    pe.BaseStream.WriteByte((byte)(nativeBytes[0] ^ 1));
                    break;
                case "interior-entry": method.AppContext.MethodsByAddress[method.UnderlyingPointer + 1] = [method]; break;
                case "missing-proof": method.PutExtraData<X64SmallAggregateFieldGetterProof.Proof>(X64SmallAggregateFieldGetterProof.EvidenceKey, null!); break;
                case "missing-binding": method.PutExtraData<object>(bindingKey, null!); break;
                case "lost-both-and-register":
                    method.PutExtraData<X64SmallAggregateFieldGetterProof.Proof>(X64SmallAggregateFieldGetterProof.EvidenceKey, null!);
                    method.PutExtraData<object>(bindingKey, null!);
                    capture.Register = new Register(null, "renamed", capture.Register.Version);
                    break;
                case "capture-version": capture.Register.Version++; break;
                case "return-copy": ret.SetOperand(0, new LocalVariable("copy", result.Register, result.Type)); break;
                case "extension-width": extension!.SetOperand(2, new Immediate(proof.Native.Width == 8 ? 16 : 8)); break;
                case "extension-result-width": extension!.SetOperand(3, new Immediate(64)); break;
                case "extension-sign": extension!.SetOperand(4, new Immediate(proof.Native.Signed ? 0 : 1)); break;
                case "extension-source": extension!.SetOperand(1, new LocalVariable("copy", capture.Register, capture.Type)); break;
                case "extension-result-type": result.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "extension-site": extension!.NativeAddress++; break;
                case "result-version": result.Register.Version++; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Small aggregate field getter"),
                $"Final aggregate mutation {mutation} must remain unresolved.");
        }
        finally
        {
            field.OverrideOffset = null;
            field.OverrideFieldType = null;
            field.OverrideAttributes = null;
            field.Name = field.DefaultName;
            rawField.Attrs = rawFieldAttributes;
            rawField.Type = rawFieldKind;
            rawField.Data.Dummy = rawFieldData;
            field.BackingData.Field.nameIndex = fieldNameIndex;
            aggregate.OverrideAttributes = null;
            aggregate.OverrideBaseType = null;
            aggregate.Definition.Bitfield = aggregateBitfield;
            aggregate.Definition.Flags = aggregateFlags;
            aggregate.Definition.RawType.Type = aggregateRawKind;
            aggregate.Definition.RawType.NumMods = aggregateModifiers;
            owner.Definition.Bitfield = ownerBitfield;
            aggregate.Fields.Clear(); aggregate.Fields.AddRange(fields);
            method.Definition.flags = methodFlags;
            method.Definition.iflags = methodImplFlags;
            method.Definition.nameIndex = methodNameIndex;
            method.OverrideReturnType = null;
            parameter.OverrideParameterType = null;
            parameter.OverrideAttributes = null;
            rawParameter.Byref = parameterByref;
            rawParameter.NumMods = parameterModifiers;
            rawParameter.Attrs = parameterRawAttributes;
            parameter.Definition.nameIndex = parameterNameIndex;
            rawReturn.Attrs = returnRawAttributes;
            capture.Type = captureType;
            incoming.Type = incomingType;
            incoming.IsThis = incomingThis;
            incoming.IsMethodInfo = incomingMethodInfo;
            access.Local = accessLocal;
            access.Offset = accessOffset;
            read.NativeAddress = readAddress;
            read.IntegerBitWidth = readWidth;
            ret.NativeAddress = returnAddress;
            ret.SetOperand(0, returnOperand);
            method.RawBytes = rawBytes;
            capture.Register = captureRegister;
            result.Register = resultRegister;
            result.Type = resultType;
            if (extension != null) { extension.SetOperands(extensionOperands!.ToList()); extension.NativeAddress = extensionAddress; }
            graph.Blocks.Clear(); graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear(); blocks[index].Instructions.AddRange(instructions[index]);
            }
            body.Successors.Clear(); body.Successors.AddRange(successors);
            method.ParameterLocals.Clear(); method.ParameterLocals.AddRange(parameterLocals);
            method.Locals.Clear(); method.Locals.AddRange(locals);
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(parameterOperands);
            method.PutExtraData(X64SmallAggregateFieldGetterProof.EvidenceKey, proof);
            method.PutExtraData(bindingKey, binding);
            method.AppContext.MethodsByAddress.Remove(method.UnderlyingPointer + 1);
            if (nativeOffset >= 0)
            {
                pe.BaseStream.Position = nativeOffset;
                pe.BaseStream.Write(nativeBytes, 0, nativeBytes.Length);
            }
            pe.BaseStream.Position = streamPosition;
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));

        void ChangeSize(int component)
        {
            var pointer = pe.TypeDefinitionSizePointers[aggregate.Definition.TypeIndex.Value] + (uint)component;
            nativeOffset = pe.MapVirtualAddressToRaw(pointer);
            nativeBytes = pe.GetRawBinaryContent().Slice(checked((int)nativeOffset), 4).ToArray();
            var changed = BitConverter.GetBytes(BitConverter.ToUInt32(nativeBytes, 0) + 1);
            pe.BaseStream.Position = nativeOffset;
            pe.BaseStream.Write(changed, 0, changed.Length);
        }
    }
}
