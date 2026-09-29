using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ScalarVirtualDispatchFixtureTests
{
    [Test]
    public void ExactScalarCallsRetainDynamicSlotsOrderedEffectsAndLateProvenance()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_DYNAMIC_VIRTUAL_DISPATCH_FIXTURE_INPUT");
        var assembly = "DynamicVirtualDispatchFixture";
        if (string.IsNullOrEmpty(input))
        {
            input = Environment.GetEnvironmentVariable("CPP2IL_DYNAMIC_SCALAR_DISPATCH_FIXTURE_INPUT");
            assembly = "DynamicScalarDispatchFixture";
        }
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_DYNAMIC_VIRTUAL_DISPATCH_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName(assembly)!;
            Assert.That(owner.Types.SelectMany(type => type.Methods).Count(), Is.EqualTo(assembly == "DynamicVirtualDispatchFixture" ? 10 : 15));
            var calls = owner.Types.Single(type => type.Name == "Dispatcher").Methods
                .Where(method => method.Name is "CallVirtual" or "CallVirtualThenStore").ToArray();
            Assert.That(calls, Has.Length.EqualTo(2));
            foreach (var method in calls)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, method.Name);
                var proof = X64ScalarVirtualDispatchProof.GetEvidence(method);
                Assert.That(proof, Is.Not.Null, method.Name);
                Assert.That(proof!.Contract.DeclaringType!.Name, Is.EqualTo("BaseState"));
                Assert.That(proof.Contract.IsVirtual, Is.True);
                Assert.That(proof.Contract.IsFinal, Is.False);
                Assert.That(proof.Matches(X64ScalarVirtualDispatchProof.Find(method)!), Is.True);
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), method.Name);
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
                Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Call), Is.Zero);
                foreach (var mutation in new[] { "direct-call", "null-checked-call", "target-override", "receiver", "argument", "result",
                             "call-site", "call-width", "extra-argument", "return", "return-site", "extra-operation", "missing-call",
                             "entry-operation", "exit-operation", "lost-proof", "lost-binding", "lost-all-markers", "result-type", "result-register",
                             "incoming-type", "incoming-this", "incoming-info", "incoming-copy", "missing-parameter", "duplicate-parameter",
                             "extra-parameter", "forged-local", "duplicate-local", "changed-slot", "addressed-input", "caller-flags", "caller-name",
                             "caller-return", "parameter-type", "parameter-modifier", "parameter-flags", "target-flags", "target-slot", "target-return",
                             "receiver-cctor", "receiver-base", "receiver-import", "receiver-method-count", "vtable-entry", "cached-native", "current-native",
                             "interior-entry", "caller-impl", "target-parameter", "target-name", "null-helper-native",
                             "receiver-cache-cycle", "argument-cache-cycle", "target-parameter-cache-cycle", "vtable-target" })
                    RejectMutation(method, definition, mutation);
                if (proof.StoredField != null)
                    foreach (var mutation in new[] { "store-before-call", "missing-store", "store-field", "store-receiver", "store-value",
                                 "store-site", "store-width", "field-offset", "field-type", "field-readonly", "field-raw-flags", "field-name",
                                 "sink-cache-cycle", "earlier-sibling-cache-cycle" })
                        RejectMutation(method, definition, mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        var proof = X64ScalarVirtualDispatchProof.GetEvidence(method)!;
        var binding = method.GetExtraData<object>(ScalarVirtualDispatchRecovery.BindingKey)!;
        var graph = method.ControlFlowGraph!;
        var block = graph.Blocks.Single(item => item != graph.EntryBlock && item != graph.ExitBlock);
        var operations = block.Instructions.ToArray();
        var call = operations.Single(instruction => instruction.OpCode == OpCode.Call);
        var ret = operations.Single(instruction => instruction.OpCode == OpCode.Return);
        var store = operations.SingleOrDefault(instruction => instruction.OpCode == OpCode.Move);
        var field = proof.StoredField;
        var access = store?.Operands[0] as FieldReference;
        var receiver = (LocalVariable)call.Operands[2];
        var argument = (LocalVariable)call.Operands[3];
        var result = (LocalVariable)call.Operands[1];
        var target = proof.Contract;
        var raw = method.Definition!;
        var parameter = method.Parameters[1];
        var rawParameter = parameter.Definition!.RawType!;
        var rawTargetParameter = target.Parameters[0].Definition!.RawType!;
        var receiverDefinition = proof.ReceiverType.Definition!;
        var slotIndex = receiverDefinition.VtableStart + target.Definition!.slot;
        var originalVtable = method.AppContext.Metadata.VTableMethodIndices[slotIndex];
        var flags = raw.flags; var impl = raw.iflags; var nameIndex = raw.nameIndex;
        var targetFlags = target.Definition.flags; var targetSlot = target.Definition.slot; var targetName = target.Definition.nameIndex;
        var parameterFlags = rawParameter.Attrs; var parameterModifier = rawParameter.NumMods;
        var typeBits = receiverDefinition.Bitfield; var methodCount = receiverDefinition.MethodCount;
        var bytes = method.RawBytes;
        var callOperands = call.Operands.ToArray(); var callSite = call.NativeAddress; var callWidth = call.IntegerBitWidth;
        var semantics = call.CallSemantics;
        var returnOperands = ret.Operands.ToArray(); var returnSite = ret.NativeAddress;
        var storeOperands = store?.Operands.ToArray(); var storeSite = store?.NativeAddress; var storeWidth = store?.IntegerBitWidth;
        var resultType = result.Type; var resultRegister = result.Register; var receiverType = receiver.Type;
        var receiverThis = receiver.IsThis; var receiverInfo = receiver.IsMethodInfo;
        var parameters = method.ParameterLocals.ToArray(); var slots = method.ParameterOperands.ToArray(); var locals = method.Locals.ToArray();
        var entry = graph.EntryBlock.Instructions.ToArray(); var exit = graph.ExitBlock.Instructions.ToArray();
        var rawField = field?.BackingData!.Field.RawFieldType;
        var fieldFlags = rawField?.Attrs; var fieldName = field?.BackingData!.Field.nameIndex;
        var targetParameterKind = rawTargetParameter.Type;
        var pe = (PE)method.AppContext.Binary;
        var position = pe.BaseStream.Position;
        long nativeOffset = -1; byte nativeByte = 0;
        Il2CppType? cyclicParameter = null;
        Il2CppTypeEnum cyclicKind = default;
        ulong cyclicData = 0;
        FieldAnalysisContext? movedSibling = null;
        var siblingOffset = 0;
        void MakeParameterCycle(Il2CppType descriptor)
        {
            Assert.That(method.AppContext.Binary.TryGetTypeVirtualAddress(descriptor, out var pointer), Is.True);
            cyclicParameter = descriptor; cyclicKind = descriptor.Type; cyclicData = descriptor.Data.Dummy;
            descriptor.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY;
            descriptor.Data.Dummy = pointer;
        }
        try
        {
            switch (mutation)
            {
                case "direct-call": call.CallSemantics = CallSemantics.Direct; break;
                case "null-checked-call": call.CallSemantics = CallSemantics.NullCheckedInstance; break;
                case "target-override": call.SetOperand(0, target.DeclaringType!.DeclaringAssembly.Types.Single(type => type.Name == "OverrideState").Methods.Single(item => item.Name == "Apply")); break;
                case "receiver": call.SetOperand(2, argument); break;
                case "argument": call.SetOperand(3, new Immediate(0)); break;
                case "result": call.SetOperand(1, receiver); break;
                case "call-site": call.NativeAddress++; break;
                case "call-width": call.IntegerBitWidth = 32; break;
                case "extra-argument": call.AddOperands([new Immediate(1)]); break;
                case "return": ret.SetOperand(0, argument); break;
                case "return-site": ret.NativeAddress++; break;
                case "extra-operation": block.Instructions.Insert(0, new(-1, OpCode.Move, argument, new Immediate(0))); break;
                case "missing-call": block.Instructions.Remove(call); break;
                case "entry-operation": graph.EntryBlock.Instructions.Add(new(-1, OpCode.Nop)); break;
                case "exit-operation": graph.ExitBlock.Instructions.Add(new(-1, OpCode.Nop)); break;
                case "lost-proof": method.PutExtraData<X64ScalarVirtualDispatchProof.Proof>(X64ScalarVirtualDispatchProof.EvidenceKey, null!); break;
                case "lost-binding": method.PutExtraData<object>(ScalarVirtualDispatchRecovery.BindingKey, null!); break;
                case "lost-all-markers": method.PutExtraData<X64ScalarVirtualDispatchProof.Proof>(X64ScalarVirtualDispatchProof.EvidenceKey, null!);
                    method.PutExtraData<object>(ScalarVirtualDispatchRecovery.BindingKey, null!); call.CallSemantics = CallSemantics.Direct;
                    result.Register = new Register(null, "renamed_dispatch_result", result.Register.Version); break;
                case "result-type": result.Type = method.AppContext.SystemTypes.SystemUInt32Type; break;
                case "result-register": result.Register = new Register(null, "other_dispatch_result", result.Register.Version); break;
                case "incoming-type": receiver.Type = target.DeclaringType!.DeclaringAssembly.Types.Single(type => type.Name == "OverrideState"); break;
                case "incoming-this": receiver.IsThis = true; break;
                case "incoming-info": receiver.IsMethodInfo = true; break;
                case "incoming-copy": call.SetOperand(2, new LocalVariable("copied", receiver.Register, receiver.Type)); break;
                case "missing-parameter": method.ParameterLocals.Remove(receiver); break;
                case "duplicate-parameter": method.ParameterLocals.Add(receiver); break;
                case "extra-parameter": method.ParameterLocals.Add(new LocalVariable("extra", new Register(null, "r9"), receiver.Type)); break;
                case "forged-local": method.Locals.Add(new LocalVariable("forged", receiver.Register, receiver.Type)); break;
                case "duplicate-local": method.Locals.Add(receiver); method.Locals.Add(receiver); break;
                case "changed-slot": method.ParameterOperands[0] = new Register(null, "rdx"); break;
                case "addressed-input": block.Instructions.Insert(0, new(-1, OpCode.Move, result, new AddressOf(argument))); break;
                case "caller-flags": raw.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "caller-name": raw.nameIndex++; break;
                case "caller-impl": raw.iflags ^= (ushort)MethodImplAttributes.NoInlining; break;
                case "caller-return": method.OverrideReturnType = method.AppContext.SystemTypes.SystemUInt32Type; break;
                case "parameter-type": parameter.OverrideParameterType = method.AppContext.SystemTypes.SystemUInt32Type; break;
                case "parameter-modifier": rawParameter.NumMods = 1; break;
                case "parameter-flags": rawParameter.Attrs = (uint)ParameterAttributes.In; break;
                case "target-flags": target.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "target-slot": target.Definition.slot++; break;
                case "target-return": target.OverrideReturnType = method.AppContext.SystemTypes.SystemUInt32Type; break;
                case "target-parameter": rawTargetParameter.Type = Il2CppTypeEnum.IL2CPP_TYPE_U4; break;
                case "target-name": target.Definition.nameIndex++; break;
                case "receiver-cctor": receiverDefinition.Bitfield |= 1U << 3; break;
                case "receiver-base": proof.ReceiverType.OverrideBaseType = proof.ReceiverType; break;
                case "receiver-import": receiverDefinition.Bitfield |= 1U << 5; break;
                case "receiver-method-count": receiverDefinition.MethodCount++; break;
                case "vtable-entry": method.AppContext.Metadata.VTableMethodIndices[slotIndex] ^= 1; break;
                case "vtable-target": method.AppContext.Metadata.VTableMethodIndices[slotIndex] ^= 2; break;
                case "receiver-cache-cycle": MakeParameterCycle(method.Parameters[0].Definition!.RawType!); break;
                case "argument-cache-cycle": MakeParameterCycle(rawParameter); break;
                case "target-parameter-cache-cycle": MakeParameterCycle(rawTargetParameter); break;
                case "sink-cache-cycle": MakeParameterCycle(method.Parameters[2].Definition!.RawType!); break;
                case "earlier-sibling-cache-cycle":
                    // Retarget both native copies to the following Int32 field,
                    // then cycle an independently typed earlier reference sibling.
                    var neighbor = field!.DeclaringType.Fields.Single(item => item.Name == "Neighbor");
                    Assert.That(proof.Native.Store!.Value.MemoryDisplSize, Is.EqualTo(1));
                    var displacement = proof.Native.Store!.Value.NextIP - 1;
                    var changedStore = bytes.AsSpan().ToArray();
                    changedStore[checked((int)(displacement - method.UnderlyingPointer))] = checked((byte)neighbor.Offset);
                    method.RawBytes = new BinarySlice(changedStore);
                    nativeOffset = pe.MapVirtualAddressToRaw(displacement); nativeByte = pe.GetByteAtRawAddress((ulong)nativeOffset);
                    Assert.That(nativeByte, Is.EqualTo(field.Offset));
                    pe.BaseStream.Position = nativeOffset; pe.BaseStream.WriteByte(checked((byte)neighbor.Offset));
                    movedSibling = field.DeclaringType.Fields.Single(item => item.Name == "Reference");
                    siblingOffset = movedSibling.BackingData!.FieldOffset;
                    movedSibling.BackingData.FieldOffset = field.Offset;
                    Assert.That(ReferenceEquals(movedSibling.BackingData.Field.RawFieldType,
                        neighbor.BackingData!.Field.RawFieldType), Is.False);
                    MakeParameterCycle(movedSibling.BackingData.Field.RawFieldType!); break;
                case "cached-native": var changed = bytes.AsSpan().ToArray(); changed[0] ^= 1; method.RawBytes = new BinarySlice(changed); break;
                case "current-native": nativeOffset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer); nativeByte = pe.GetByteAtRawAddress((ulong)nativeOffset);
                    pe.BaseStream.Position = nativeOffset; pe.BaseStream.WriteByte((byte)(nativeByte ^ 1)); break;
                case "null-helper-native": nativeOffset = pe.MapVirtualAddressToRaw(proof.Native.NullCall.NearBranchTarget);
                    nativeByte = pe.GetByteAtRawAddress((ulong)nativeOffset); pe.BaseStream.Position = nativeOffset;
                    pe.BaseStream.WriteByte((byte)(nativeByte ^ 1)); break;
                case "interior-entry": method.AppContext.MethodsByAddress[method.UnderlyingPointer + 1] = [method]; break;
                case "store-before-call": block.Instructions.Remove(store!); block.Instructions.Insert(0, store!); break;
                case "missing-store": block.Instructions.Remove(store!); break;
                case "store-field": store!.SetOperand(0, new FieldReference(field!.DeclaringType.Fields.Single(item => item.Name == "Neighbor"), access!.Local, access.Offset)); break;
                case "store-receiver": store!.SetOperand(0, new FieldReference(field!, receiver, access!.Offset)); break;
                case "store-value": store!.SetOperand(1, argument); break;
                case "store-site": store!.NativeAddress++; break;
                case "store-width": store!.IntegerBitWidth = 64; break;
                case "field-offset": field!.OverrideOffset = field.Offset + 4; break;
                case "field-type": field!.OverrideFieldType = method.AppContext.SystemTypes.SystemUInt32Type; break;
                case "field-readonly": field!.OverrideAttributes = field.Attributes | FieldAttributes.InitOnly; break;
                case "field-raw-flags": rawField!.Attrs ^= (uint)FieldAttributes.InitOnly; break;
                case "field-name": field!.BackingData!.Field.nameIndex++; break;
                default: Assert.Fail("Unknown mutation " + mutation); break;
            }
            Assert.That(ScalarVirtualDispatchRecovery.HasEvidence(method), Is.True, mutation);
            Assert.That(ScalarVirtualDispatchRecovery.IsValidFor(method), Is.False, mutation);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, definition), mutation);
        }
        finally
        {
            if (cyclicParameter != null) { cyclicParameter.Type = cyclicKind; cyclicParameter.Data.Dummy = cyclicData; }
            if (movedSibling != null) movedSibling.BackingData!.FieldOffset = siblingOffset;
            raw.flags = flags; raw.iflags = impl; raw.nameIndex = nameIndex;
            target.Definition.flags = targetFlags; target.Definition.slot = targetSlot; target.Definition.nameIndex = targetName;
            rawParameter.Attrs = parameterFlags; rawParameter.NumMods = parameterModifier; rawTargetParameter.Type = targetParameterKind;
            method.OverrideReturnType = null; target.OverrideReturnType = null; parameter.OverrideParameterType = null;
            receiverDefinition.Bitfield = typeBits; receiverDefinition.MethodCount = methodCount; proof.ReceiverType.OverrideBaseType = null;
            method.AppContext.Metadata.VTableMethodIndices[slotIndex] = originalVtable; method.RawBytes = bytes;
            method.ParameterLocals.Clear(); method.ParameterLocals.AddRange(parameters);
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(slots);
            method.Locals.Clear(); method.Locals.AddRange(locals);
            block.Instructions.Clear(); block.Instructions.AddRange(operations);
            graph.EntryBlock.Instructions.Clear(); graph.EntryBlock.Instructions.AddRange(entry);
            graph.ExitBlock.Instructions.Clear(); graph.ExitBlock.Instructions.AddRange(exit);
            call.SetOperands(callOperands.ToList()); call.NativeAddress = callSite; call.IntegerBitWidth = callWidth; call.CallSemantics = semantics;
            ret.SetOperands(returnOperands.ToList()); ret.NativeAddress = returnSite;
            if (store != null) { store.SetOperands(storeOperands!.ToList()); store.NativeAddress = storeSite; store.IntegerBitWidth = storeWidth!.Value; }
            result.Type = resultType; result.Register = resultRegister;
            receiver.Type = receiverType; receiver.IsThis = receiverThis; receiver.IsMethodInfo = receiverInfo;
            if (field != null) { field.OverrideOffset = null; field.OverrideFieldType = null; field.OverrideAttributes = null;
                rawField!.Attrs = fieldFlags!.Value; field.BackingData!.Field.nameIndex = fieldName!.Value; }
            method.PutExtraData(X64ScalarVirtualDispatchProof.EvidenceKey, proof);
            method.PutExtraData(ScalarVirtualDispatchRecovery.BindingKey, binding);
            method.AppContext.MethodsByAddress.Remove(method.UnderlyingPointer + 1);
            if (nativeOffset >= 0) { pe.BaseStream.Position = nativeOffset; pe.BaseStream.WriteByte(nativeByte); }
            pe.BaseStream.Position = position;
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), "Restored " + mutation);
    }
}
