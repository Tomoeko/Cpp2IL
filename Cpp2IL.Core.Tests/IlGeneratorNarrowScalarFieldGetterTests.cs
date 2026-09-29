using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorNarrowScalarFieldGetterTests
{
    [Test]
    public void ExactStructGettersKeepNarrowTypesAndRejectChangedFinalProvenance()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NARROW_SCALAR_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NARROW_SCALAR_GETTER_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("NarrowScalarGetterFixture")!.Types.Single(type => type.Name == "NarrowScalars");
            var methods = owner.Methods.ToArray();
            Assert.That(methods, Has.Length.EqualTo(8));
            foreach (var method in methods)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                var proof = X64NarrowScalarFieldGetterProof.GetEvidence(method);
                Assert.That(proof, Is.Not.Null);
                Assert.That(proof!.Widen, Is.EqualTo(method.Name.StartsWith("Widen", StringComparison.Ordinal)));
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(1));
                Assert.That(method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.IntegerExtend),
                    Is.EqualTo(proof.Widen ? 1 : 0));
                foreach (var mutation in new[] { "field-offset", "field-type", "field-attributes", "field-name",
                    "sibling-overlap", "layout", "base-type", "owner-extent", "signature-return", "capture-type",
                    "receiver-type", "receiver-copy", "receiver-slot-name", "receiver-slot-number", "receiver-slot-version",
                    "duplicate-receiver", "receiver-methodinfo", "capture-site", "ret-site", "read-offset",
                    "extra-effect", "removed-read", "entry-marker", "exit-marker", "detached-block", "native-bytes",
                    "current-native-byte", "interior-entry", "missing-proof", "capture-version" })
                    RejectMutation(method, definition, mutation);
                if (proof.Widen)
                    foreach (var mutation in new[] { "extension-width", "extension-result-width", "extension-sign",
                        "extension-source", "extension-result-type", "result-version" })
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
        var extension = graph.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCode.IntegerExtend);
        var capture = (LocalVariable)read.Operands[0];
        var field = (FieldReference)read.Operands[1];
        var receiver = field.Local;
        var owner = field.Field.DeclaringType;
        var sibling = owner.Fields.First(candidate => !candidate.IsStatic && candidate != field.Field);
        var proof = X64NarrowScalarFieldGetterProof.GetEvidence(method)!;
        var block = graph.FindBlockByInstruction(read)!;
        var blocks = graph.Blocks.ToArray();
        var instructions = blocks.Select(item => item.Instructions.ToArray()).ToArray();
        var parameterLocals = method.ParameterLocals.ToArray();
        var parameterOperands = method.ParameterOperands.ToArray();
        var localType = capture.Type;
        var receiverType = receiver.Type;
        var receiverFlag = receiver.IsMethodInfo;
        var fieldLocal = field.Local;
        var readOffset = field.Offset;
        var readSite = read.NativeAddress;
        var returnSite = ret.NativeAddress;
        var rawBytes = method.RawBytes;
        var extensionOperands = extension?.Operands.ToArray();
        var result = (LocalVariable)ret.Operands[0];
        var resultType = result.Type;
        var captureRegister = capture.Register;
        var resultRegister = result.Register;
        var extent = owner.Definition!.RawSizes.instance_size;
        var pe = (PE)method.AppContext.Binary;
        var position = pe.BaseStream.Position;
        var nativeOffset = -1L;
        var nativeBytes = Array.Empty<byte>();
        try
        {
            switch (mutation)
            {
                case "field-offset": field.Field.OverrideOffset = field.Field.DefaultOffset + 1; break;
                case "field-type": field.Field.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "field-attributes": field.Field.OverrideAttributes = field.Field.DefaultAttributes | FieldAttributes.Static; break;
                case "field-name": field.Field.Name += "Changed"; break;
                case "sibling-overlap": sibling.OverrideOffset = field.Field.Offset; break;
                case "layout": owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "base-type": owner.OverrideBaseType = method.AppContext.SystemTypes.SystemObjectType; break;
                case "owner-extent":
                    var sizePointer = method.AppContext.Binary.TypeDefinitionSizePointers[owner.Definition.TypeIndex!.Value];
                    nativeOffset = pe.MapVirtualAddressToRaw(sizePointer);
                    nativeBytes = pe.GetRawBinaryContent().Slice(checked((int)nativeOffset), 4).ToArray();
                    pe.BaseStream.Position = nativeOffset;
                    var smaller = BitConverter.GetBytes(extent - 1);
                    pe.BaseStream.Write(smaller, 0, smaller.Length);
                    break;
                case "signature-return": method.OverrideReturnType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "capture-type": capture.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "receiver-type": receiver.Type = method.AppContext.SystemTypes.SystemObjectType; break;
                case "receiver-copy": field.Local = new LocalVariable("copy", receiver.Register, receiver.Type); break;
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
                case "read-offset": field.Offset++; break;
                case "extra-effect": block.Instructions.Insert(1, new Instruction(999, OpCode.Move, field, capture)); break;
                case "removed-read": block.Instructions.Remove(read); break;
                case "entry-marker": block.Instructions.Remove(read); graph.EntryBlock.Instructions.Add(read); break;
                case "exit-marker": graph.ExitBlock.Instructions.Add(new Instruction(999, OpCode.Move, field, capture)); break;
                case "detached-block": graph.Blocks.Add(new Block { Instructions = [new Instruction(999, OpCode.Move, field, capture)] }); break;
                case "native-bytes":
                    var changed = rawBytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "current-native-byte":
                    nativeOffset = pe.MapVirtualAddressToRaw(proof.Native.Load.IP);
                    nativeBytes = [pe.GetByteAtRawAddress((ulong)nativeOffset)];
                    pe.BaseStream.Position = nativeOffset;
                    pe.BaseStream.WriteByte((byte)(nativeBytes[0] ^ 1));
                    break;
                case "interior-entry": method.AppContext.MethodsByAddress[method.UnderlyingPointer + 1] = [method]; break;
                case "missing-proof": method.PutExtraData<X64NarrowScalarFieldGetterProof.Proof>(X64NarrowScalarFieldGetterProof.EvidenceKey, null!); break;
                case "capture-version": capture.Register.Version++; break;
                case "extension-width": extension!.SetOperand(2, new Immediate(proof.Native.Width == 8 ? 16 : 8)); break;
                case "extension-result-width": extension!.SetOperand(3, new Immediate(64)); break;
                case "extension-sign": extension!.SetOperand(4, new Immediate(proof.Native.Signed ? 0 : 1)); break;
                case "extension-source": extension!.SetOperand(1, new LocalVariable("copy", capture.Register, capture.Type)); break;
                case "extension-result-type": result.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "result-version": result.Register.Version++; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, definition),
                $"Final getter mutation {mutation} must remain unresolved.");
        }
        finally
        {
            field.Field.OverrideOffset = null;
            field.Field.OverrideFieldType = null;
            field.Field.OverrideAttributes = null;
            field.Field.Name = field.Field.DefaultName;
            sibling.OverrideOffset = null;
            owner.OverrideAttributes = null;
            owner.OverrideBaseType = null;
            method.OverrideReturnType = null;
            capture.Type = localType;
            receiver.Type = receiverType;
            receiver.IsMethodInfo = receiverFlag;
            field.Local = fieldLocal;
            field.Offset = readOffset;
            read.NativeAddress = readSite;
            ret.NativeAddress = returnSite;
            method.RawBytes = rawBytes;
            result.Type = resultType;
            capture.Register = captureRegister;
            result.Register = resultRegister;
            if (extensionOperands != null) extension!.SetOperands(extensionOperands.ToList());
            graph.Blocks.Clear();
            graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear();
                blocks[index].Instructions.AddRange(instructions[index]);
            }
            method.ParameterLocals.Clear();
            method.ParameterLocals.AddRange(parameterLocals);
            method.ParameterOperands.Clear();
            method.ParameterOperands.AddRange(parameterOperands);
            method.PutExtraData(X64NarrowScalarFieldGetterProof.EvidenceKey, proof);
            method.AppContext.MethodsByAddress.Remove(method.UnderlyingPointer + 1);
            if (nativeOffset >= 0)
            {
                pe.BaseStream.Position = nativeOffset;
                pe.BaseStream.Write(nativeBytes, 0, nativeBytes.Length);
            }
            pe.BaseStream.Position = position;
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
    }
}
