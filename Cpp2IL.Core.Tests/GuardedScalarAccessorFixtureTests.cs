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
public class GuardedScalarAccessorFixtureTests
{
    [Test]
    public void ExactPrivateScalarReadsKeepAccessibleDispatchNullBehaviorAndCurrentEvidence()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_GUARDED_SCALAR_ACCESSOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GUARDED_SCALAR_ACCESSOR_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var methods = app.GetAssemblyByName("GuardedScalarAccessorFixture")!.Types.SelectMany(type => type.Methods).ToArray();
            Assert.That(methods, Has.Length.EqualTo(7));
            var proved = 0;
            foreach (var method in methods)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, method.Name);
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), method.Name);
                if (method.Name == ".ctor")
                    continue;
                var proof = X64GuardedScalarAccessorProof.GetEvidence(method);
                Assert.That(proof, Is.Not.Null, method.Name);
                Assert.That(proof!.Value.Visibility, Is.EqualTo(FieldAttributes.Private));
                Assert.That(proof.Getter.Visibility, Is.EqualTo(MethodAttributes.Public));
                Assert.That(proof.Getter.IsVirtual, Is.False);
                var sibling = proof.Getter.DeclaringType!.Fields.Single(field => field.Name == "ArrayNeighbor");
                Assert.That(sibling.BackingData!.Field.RawFieldType!.Type, Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY));
                var firstWrapper = sibling.FieldType;
                var secondWrapper = sibling.FieldType;
                Assert.That(secondWrapper, Is.Not.SameAs(firstWrapper));
                Assert.That(secondWrapper.FullName, Is.EqualTo(firstWrapper.FullName));
                Assert.That(proof.Matches(X64GuardedScalarAccessorProof.Find(method)!), Is.True,
                    "Re-resolved untouched array siblings must retain the original scalar proof.");
                Assert.That(proof.IsDirect, Is.EqualTo(method.Name.StartsWith("get_", StringComparison.Ordinal)));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt),
                    Is.EqualTo(proof.IsDirect ? 0 : 1));
                Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(1));
                foreach (var mutation in new[] { "field-offset", "field-type", "field-raw-kind", "field-raw-flags", "field-name-index",
                    "owner-cctor", "owner-extent", "owner-layout", "owner-nesting", "owner-raw-nesting",
                    "sibling-array-type", "sibling-array-kind", "sibling-array-data", "sibling-array-raw-flags",
                    "sibling-element-kind", "sibling-element-modifier", "sibling-element-byref", "sibling-element-pinned", "sibling-element-cache-cycle", "method-flags", "method-impl-flags", "method-name-index",
                    "return-type", "return-modifier", "getter-flags", "getter-return", "getter-cctor", "property-name",
                    "property-type", "property-flags", "property-index", "cached-native", "getter-cached-native", "current-native",
                    "getter-current-native", "interior-entry", "missing-proof", "missing-binding", "lost-markers",
                    "incoming-copy", "missing-incoming", "duplicate-incoming", "forged-incoming", "slot-name", "slot-number",
                    "slot-version", "metadata-slot", "incoming-type", "incoming-this", "incoming-methodinfo", "read-site",
                    "read-width", "read-offset", "capture-type", "capture-version", "capture-negative-version", "duplicate-capture-slot", "result-type", "result-negative-version", "return-copy", "return-site", "extra-effect",
                    "entry-marker", "exit-marker", "detached-block", "changed-edge", "reordered-body" })
                    RejectMutation(method, definition, mutation);
                if (!proof.IsDirect)
                    foreach (var mutation in new[] { "source-type", "source-offset", "source-visibility", "source-cctor", "source-extent",
                        "target-visibility", "target-nesting", "call-target", "call-receiver", "call-result", "call-semantics", "call-site", "call-metadata", "call-extra-argument" })
                        RejectMutation(method, definition, mutation);
                if (proof.Value.FieldType.IsEnumType)
                    foreach (var mutation in new[] { "enum-underlying", "enum-cctor", "enum-backing-type", "enum-raw-kind", "enum-constant" })
                        RejectMutation(method, definition, mutation);
                proved++;
            }
            Assert.That(proved, Is.EqualTo(4));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        const string bindingKey = "GuardedScalarAccessorRecovery";
        var proof = X64GuardedScalarAccessorProof.GetEvidence(method)!;
        var graph = method.ControlFlowGraph!;
        var body = graph.Blocks.Single(block => block != graph.EntryBlock && block != graph.ExitBlock);
        var operations = body.Instructions.ToArray();
        var read = operations.Single(instruction => instruction.OpCode == OpCode.Move);
        var ret = operations.Single(instruction => instruction.OpCode == OpCode.Return);
        var call = operations.SingleOrDefault(instruction => instruction.OpCode == OpCode.Call);
        var access = (FieldReference)read.Operands[1];
        var incoming = access.Local;
        var capture = (LocalVariable)read.Operands[0];
        var result = (LocalVariable)ret.Operands[0];
        var field = proof.Value;
        var owner = field.DeclaringType;
        var getter = proof.Getter;
        var property = proof.Property;
        var enumType = field.FieldType.IsEnumType ? field.FieldType : null;
        var backing = enumType?.Fields.Single(item => !item.IsStatic);
        var constant = enumType?.Fields.First(item => item.IsStatic && (item.Attributes & FieldAttributes.Literal) != 0);
        var sibling = proof.Getter.DeclaringType!.Fields.Single(item => item.Name == "ArrayNeighbor");
        var rawSibling = sibling.BackingData!.Field.RawFieldType!;
        var siblingKind = rawSibling.Type; var siblingData = rawSibling.Data.Dummy; var siblingFlags = rawSibling.Attrs;
        var element = rawSibling.GetEncapsulatedType();
        var elementKind = element.Type; var elementModifier = element.NumMods; var elementByref = element.Byref;
        var elementPinned = element.Pinned; var elementData = element.Data.Dummy;
        var rawField = field.BackingData!.Field.RawFieldType!;
        var rawBacking = backing?.BackingData!.Field.RawFieldType;
        var rawKind = rawField.Type; var rawFlags = rawField.Attrs;
        var fieldName = field.BackingData.Field.nameIndex;
        var ownerBits = owner.Definition!.Bitfield; var ownerFlags = owner.Definition.Flags;
        var ownerDeclaringType = owner.DeclaringType; var rawOwnerDeclaringType = owner.Definition.DeclaringTypeIndex;
        var methodFlags = method.Definition!.flags; var methodImpl = method.Definition.iflags;
        var methodName = method.Definition.nameIndex; var modifier = method.Definition.RawReturnType!.NumMods;
        var getterFlags = getter.Definition!.flags; var getterBits = getter.DeclaringType!.Definition!.Bitfield;
        var propertyFlags = property.Definition!.attrs; var propertyIndex = property.Definition.get;
        var propertyName = property.Name;
        var enumBits = enumType?.Definition!.Bitfield; var enumKind = enumType?.Definition!.RawType.Type;
        var backingKind = rawBacking?.Type;
        var source = proof.Source; var sourceOwner = source?.DeclaringType;
        var sourceBits = sourceOwner?.Definition!.Bitfield;
        var rawBytes = method.RawBytes; var getterBytes = getter.RawBytes;
        var parameters = method.ParameterLocals.ToArray(); var slots = method.ParameterOperands.ToArray();
        var locals = method.Locals.ToArray(); var blocks = graph.Blocks.ToArray(); var successors = body.Successors.ToArray();
        var entryOps = graph.EntryBlock.Instructions.ToArray(); var exitOps = graph.ExitBlock.Instructions.ToArray();
        var readOperands = read.Operands.ToArray(); var readSite = read.NativeAddress; var readWidth = read.IntegerBitWidth;
        var returnOperands = ret.Operands.ToArray(); var returnSite = ret.NativeAddress;
        var callOperands = call?.Operands.ToArray(); var callSite = call?.NativeAddress; var semantics = call?.CallSemantics;
        var captureRegister = capture.Register; var captureType = capture.Type; var incomingType = incoming.Type;
        var resultRegister = result.Register; var resultType = result.Type;
        var targetDeclaringType = getter.DeclaringType!.DeclaringType; var targetAttributes = getter.DeclaringType.OverrideAttributes;
        var incomingThis = incoming.IsThis; var incomingInfo = incoming.IsMethodInfo; var accessOffset = access.Offset;
        var binding = method.GetExtraData<object>(bindingKey)!;
        var pe = (PE)method.AppContext.Binary;
        var position = pe.BaseStream.Position;
        long changedOffset = -1; byte originalByte = 0;
        long sizeOffset = -1; byte[] sizeBytes = [];
        try
        {
            switch (mutation)
            {
                case "sibling-array-type": sibling.OverrideFieldType = new SzArrayTypeAnalysisContext(method.AppContext.SystemTypes.SystemInt64Type); break;
                case "sibling-array-kind": rawSibling.Type = Il2CppTypeEnum.IL2CPP_TYPE_ARRAY; break;
                case "sibling-array-data": rawSibling.Data.Dummy = 0; break;
                case "sibling-array-raw-flags": rawSibling.Attrs ^= (uint)FieldAttributes.InitOnly; break;
                case "sibling-element-kind": element.Type = Il2CppTypeEnum.IL2CPP_TYPE_U4; break;
                case "sibling-element-modifier": element.NumMods = 1; break;
                case "sibling-element-byref": element.Byref = 1; break;
                case "sibling-element-pinned": element.Pinned = 1; break;
                case "sibling-element-cache-cycle": element.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY;
                    element.Data.Dummy = rawSibling.Data.Type; break;
                case "field-offset": field.OverrideOffset = field.Offset + 1; break;
                case "field-type": field.OverrideFieldType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "field-raw-kind": rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_I8; break;
                case "field-raw-flags": rawField.Attrs ^= (uint)FieldAttributes.InitOnly; break;
                case "field-name-index": field.BackingData.Field.nameIndex++; break;
                case "owner-cctor": owner.Definition.Bitfield |= 1U << 3; break;
                case "owner-extent": ChangeSize(owner, field.Offset); break;
                case "owner-layout": owner.Definition.Flags ^= (uint)TypeAttributes.BeforeFieldInit; break;
                case "owner-nesting": owner.DeclaringType = method.AppContext.SystemTypes.SystemObjectType; break;
                case "owner-raw-nesting": owner.Definition.DeclaringTypeIndex = owner.Definition.ByvalTypeIndex; break;
                case "method-flags": method.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "method-impl-flags": method.Definition.iflags ^= (ushort)MethodImplAttributes.NoInlining; break;
                case "method-name-index": method.Definition.nameIndex++; break;
                case "return-type": method.OverrideReturnType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "return-modifier": method.Definition.RawReturnType.NumMods = 1; break;
                case "getter-flags": getter.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "getter-return": getter.OverrideReturnType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "getter-cctor": getter.DeclaringType.Definition!.Bitfield |= 1U << 3; break;
                case "property-name": property.Name += "Changed"; break;
                case "property-type": property.OverridePropertyType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "property-flags": property.Definition.attrs ^= (uint)PropertyAttributes.SpecialName; break;
                case "property-index": property.Definition.get = property.Definition.set; break;
                case "cached-native": var bytes = rawBytes.AsSpan().ToArray(); bytes[0] ^= 1; method.RawBytes = new BinarySlice(bytes); break;
                case "getter-cached-native": var changed = getterBytes.AsSpan().ToArray(); changed[0] ^= 1; getter.RawBytes = new BinarySlice(changed); break;
                case "current-native": ChangeNative(method.UnderlyingPointer); break;
                case "getter-current-native": ChangeNative(getter.UnderlyingPointer); break;
                case "interior-entry": method.AppContext.MethodsByAddress[getter.UnderlyingPointer + 1] = [method]; break;
                case "missing-proof": method.PutExtraData<X64GuardedScalarAccessorProof.Proof>(X64GuardedScalarAccessorProof.EvidenceKey, null!); break;
                case "missing-binding": method.PutExtraData<object>(bindingKey, null!); break;
                case "lost-markers": method.PutExtraData<X64GuardedScalarAccessorProof.Proof>(X64GuardedScalarAccessorProof.EvidenceKey, null!);
                    method.PutExtraData<object>(bindingKey, null!); capture.Register = new Register(null, "renamed", capture.Register.Version); break;
                case "incoming-copy": access.Local = new LocalVariable("copy", incoming.Register, incoming.Type); break;
                case "missing-incoming": method.ParameterLocals.Clear(); break;
                case "duplicate-incoming": method.ParameterLocals.Add(incoming); break;
                case "forged-incoming": method.Locals.Add(new LocalVariable("forged", incoming.Register, incoming.Type)); break;
                case "slot-name": method.ParameterOperands[0] = new Register(((Register)slots[0]).Number, "rdx"); break;
                case "slot-number": method.ParameterOperands[0] = new Register(0, "rcx"); break;
                case "slot-version": method.ParameterOperands[0] = ((Register)slots[0]).Copy(1); break;
                case "metadata-slot": method.ParameterOperands[1] = slots[0]; break;
                case "incoming-type": incoming.Type = field.FieldType; break;
                case "incoming-this": incoming.IsThis = false; break;
                case "incoming-methodinfo": incoming.IsMethodInfo = true; break;
                case "read-site": read.NativeAddress++; break;
                case "read-width": read.IntegerBitWidth = 32; break;
                case "read-offset": access.Offset++; break;
                case "capture-type": capture.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "capture-version": capture.Register.Version++; break;
                case "capture-negative-version": capture.Register.Version = -1; break;
                case "duplicate-capture-slot": method.Locals.Add(new LocalVariable("competing", capture.Register.Copy(99), capture.Type)); break;
                case "result-type": result.Type = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "result-negative-version": result.Register.Version = -1; break;
                case "return-copy": ret.SetOperand(0, new LocalVariable("copy", result.Register, result.Type)); break;
                case "return-site": ret.NativeAddress++; break;
                case "extra-effect": body.Instructions.Insert(1, new Instruction(999, OpCode.Move, access, capture)); break;
                case "entry-marker": body.Instructions.Remove(read); graph.EntryBlock.Instructions.Add(read); break;
                case "exit-marker": graph.ExitBlock.Instructions.Add(read); break;
                case "detached-block": graph.Blocks.Add(new Block { Instructions = [read] }); break;
                case "changed-edge": body.Successors.Add(graph.ExitBlock); break;
                case "reordered-body": body.Instructions.Remove(ret); body.Instructions.Insert(0, ret); break;
                case "source-type": source!.OverrideFieldType = method.AppContext.SystemTypes.SystemObjectType; break;
                case "source-offset": source!.OverrideOffset = source.Offset + 8; break;
                case "source-visibility": source!.OverrideAttributes = FieldAttributes.Private; break;
                case "source-cctor": sourceOwner!.Definition!.Bitfield |= 1U << 3; break;
                case "source-extent": ChangeSize(sourceOwner!, source!.Offset + 7); break;
                case "target-visibility": getter.DeclaringType.OverrideAttributes = (getter.DeclaringType.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPrivate; break;
                case "target-nesting": getter.DeclaringType.DeclaringType = method.DeclaringType; break;
                case "call-target": call!.SetOperand(0, method); break;
                case "call-receiver": call!.SetOperand(2, new LocalVariable("copy", capture.Register, capture.Type)); break;
                case "call-result": call!.SetOperand(1, new LocalVariable("copy", result.Register, result.Type)); break;
                case "call-semantics": call!.CallSemantics = CallSemantics.Direct; break;
                case "call-site": call!.NativeAddress++; break;
                case "call-metadata": call!.SetOperands(call.Operands.Take(3).Concat<IOperand>([new Immediate(1)]).ToList()); break;
                case "call-extra-argument": call!.AddOperands([new Immediate(0), new Immediate(1)]); break;
                case "enum-underlying": enumType!.OverrideEnumUnderlyingType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "enum-cctor": enumType!.Definition!.Bitfield |= 1U << 3; break;
                case "enum-backing-type": rawBacking!.Type = Il2CppTypeEnum.IL2CPP_TYPE_I8; break;
                case "enum-raw-kind": enumType!.Definition!.RawType.Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS; break;
                case "enum-constant": constant!.UseOverrideConstantValue = true; constant.OverrideConstantValue = 123; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Guarded scalar accessor"), mutation);
        }
        finally
        {
            sibling.OverrideFieldType = null; rawSibling.Type = siblingKind; rawSibling.Data.Dummy = siblingData;
            rawSibling.Attrs = siblingFlags; element.Type = elementKind; element.NumMods = elementModifier;
            element.Byref = elementByref; element.Pinned = elementPinned; element.Data.Dummy = elementData;
            field.OverrideOffset = null; field.OverrideFieldType = null;
            rawField.Type = rawKind; rawField.Attrs = rawFlags; field.BackingData.Field.nameIndex = fieldName;
            owner.Definition.Bitfield = ownerBits; owner.Definition.Flags = ownerFlags;
            owner.DeclaringType = ownerDeclaringType; owner.Definition.DeclaringTypeIndex = rawOwnerDeclaringType;
            method.Definition.flags = methodFlags; method.Definition.iflags = methodImpl;
            method.Definition.nameIndex = methodName; method.Definition.RawReturnType.NumMods = modifier;
            method.OverrideReturnType = null; getter.Definition.flags = getterFlags; getter.OverrideReturnType = null;
            getter.DeclaringType.Definition!.Bitfield = getterBits;
            property.Name = propertyName; property.OverridePropertyType = null;
            property.Definition.attrs = propertyFlags; property.Definition.get = propertyIndex;
            if (enumType != null) { enumType.OverrideEnumUnderlyingType = null; enumType.Definition!.Bitfield = enumBits!.Value;
                enumType.Definition.RawType.Type = enumKind!.Value; rawBacking!.Type = backingKind!.Value;
                constant!.UseOverrideConstantValue = false; constant.OverrideConstantValue = null; }
            if (source != null) { source.OverrideFieldType = null; source.OverrideOffset = null; source.OverrideAttributes = null;
                sourceOwner!.Definition!.Bitfield = sourceBits!.Value; }
            method.RawBytes = rawBytes; getter.RawBytes = getterBytes;
            method.ParameterLocals.Clear(); method.ParameterLocals.AddRange(parameters);
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(slots);
            method.Locals.Clear(); method.Locals.AddRange(locals);
            graph.Blocks.Clear(); graph.Blocks.AddRange(blocks); body.Instructions.Clear(); body.Instructions.AddRange(operations);
            body.Successors.Clear(); body.Successors.AddRange(successors);
            graph.EntryBlock.Instructions.Clear(); graph.EntryBlock.Instructions.AddRange(entryOps);
            graph.ExitBlock.Instructions.Clear(); graph.ExitBlock.Instructions.AddRange(exitOps);
            read.SetOperands(readOperands.ToList()); read.NativeAddress = readSite; read.IntegerBitWidth = readWidth;
            ret.SetOperands(returnOperands.ToList()); ret.NativeAddress = returnSite;
            if (call != null) { call.SetOperands(callOperands!.ToList()); call.NativeAddress = callSite; call.CallSemantics = semantics!.Value; }
            capture.Register = captureRegister; capture.Type = captureType;
            result.Register = resultRegister; result.Type = resultType;
            getter.DeclaringType.DeclaringType = targetDeclaringType; getter.DeclaringType.OverrideAttributes = targetAttributes;
            incoming.Type = incomingType; incoming.IsThis = incomingThis; incoming.IsMethodInfo = incomingInfo;
            access.Local = incoming; access.Offset = accessOffset;
            method.PutExtraData(X64GuardedScalarAccessorProof.EvidenceKey, proof); method.PutExtraData(bindingKey, binding);
            method.AppContext.MethodsByAddress.Remove(getter.UnderlyingPointer + 1);
            if (changedOffset >= 0) { pe.BaseStream.Position = changedOffset; pe.BaseStream.WriteByte(originalByte); }
            pe.BaseStream.Position = position;
            if (sizeOffset >= 0) { pe.BaseStream.Position = sizeOffset; pe.BaseStream.Write(sizeBytes); pe.BaseStream.Position = position; }
        }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition), "Restored " + mutation);
        void ChangeNative(ulong address)
        {
            changedOffset = pe.MapVirtualAddressToRaw(address); originalByte = pe.GetByteAtRawAddress((ulong)changedOffset);
            pe.BaseStream.Position = changedOffset; pe.BaseStream.WriteByte((byte)(originalByte ^ 1));
        }
        void ChangeSize(TypeAnalysisContext type, int value)
        {
            sizeOffset = pe.MapVirtualAddressToRaw(pe.TypeDefinitionSizePointers[type.Definition!.TypeIndex.Value]);
            sizeBytes = new byte[4]; pe.BaseStream.Position = sizeOffset; pe.BaseStream.ReadExactly(sizeBytes);
            pe.BaseStream.Position = sizeOffset; pe.BaseStream.Write(BitConverter.GetBytes(value));
        }
    }
}
