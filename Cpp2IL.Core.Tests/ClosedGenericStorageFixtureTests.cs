using System;
using System.Collections.Generic;
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
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ClosedGenericStorageFixtureTests
{
    [Test]
    public void OriginalClosedPrefixesEnableCompleteConstructorsAndKeepTheirWitnessesAtEmission()
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
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("ClosedGenericStorageFixture")!;
            var methods = assembly.Types.SelectMany(type => type.Methods).ToArray();
            Assert.That(methods, Has.Length.EqualTo(6));
            CheckArrayElementIdentity(app, assembly);
            CheckEnumBackingRecursion(app);
            foreach (var owner in assembly.Types.Where(type => type.Name is "IntState" or "LongState"))
            {
                var prefix = owner.Fields.Single(field => field.Name == "Prefix");
                var value = owner.Fields.Single(field => field.Name == "Value");
                var instance = (GenericInstanceTypeAnalysisContext)prefix.FieldType;
                var definition = instance.GenericType;
                var fields = definition.Fields.ToArray();
                // Count and membership alone do not authenticate sequential layout order.
                definition.Fields.Reverse();
                Assert.That(TypeSizes.UnboxedSize(instance, 8), Is.Zero, "original field order before first witness");
                definition.Fields.Clear(); definition.Fields.AddRange(fields);
                var expectedSize = owner.Name == "IntState" ? 8 : 16;
                Assert.That(TypeSizes.UnboxedSize(instance, 8), Is.EqualTo(expectedSize));
                Assert.That(value.Offset, Is.EqualTo(prefix.Offset + expectedSize));
                Assert.That(ClosedGenericValueLayoutProof.Find(instance)!.Offsets,
                    Is.EqualTo(owner.Name == "IntState" ? new long[] { 0, 4 } : new long[] { 0, 8 }));
                Assert.That(GenericInstanceFieldLayout.FindFieldAtOffset(instance, 0), Is.Null);
                Assert.That(GenericInstanceFieldLayout.FindFieldAtOffset(instance, 16), Is.Null,
                    "A storage witness does not invent a boxed receiver for field projection.");
                Assert.That(GenericInstanceFieldLayout.FindFieldAtOffset(definition, 16), Is.Null,
                    "Open VAR storage cannot be treated as a pointer.");
                Assert.That(TypeSizes.UnboxedSize(new GenericInstanceTypeAnalysisContext(definition, instance.GenericArguments), 8), Is.Zero,
                    "An invented wrapper cannot establish player-backed storage.");
                var constructor = owner.Methods.Single(method => method.Name == ".ctor");
                constructor.EnsureRawBytes();
                Assert.That(X64FoldedInt32ConstructorProof.Find(constructor, X86Utils.Iterate(constructor).ToArray()), Is.Not.Null);
                constructor.Analyze();
                Assert.That(constructor.AnalysisWarnings, Is.Empty);
                Assert.That(ClosedGenericStorageRecovery.HasEvidence(constructor), Is.True);
                Assert.That(ClosedGenericStorageRecovery.IsValidFor(constructor), Is.True);
                var output = constructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(constructor, output));
                Assert.That(output.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
                foreach (var mutation in new[]
                {
                    "argument-reference", "argument-removed", "argument-extra", "argument-kind", "argument-modifier", "argument-self-cycle",
                    "root-kind", "root-data", "root-modifier", "layout-explicit", "layout-pack", "layout-size", "layout-cctor",
                    "layout-order", "layout-added-field", "layout-field-type", "layout-field-marshal", "layout-field-offset",
                    "layout-field-name", "layout-field-self-cycle", "layout-var-index", "parameter-position", "parameter-owner", "parameter-flags", "parameter-count",
                    "layout-parent", "layout-parent-cycle", "prefix-overlap", "prefix-type", "native-cache", "native-current",
                    "generic-current", "root-current", "argument-table-current", "base-native-cache", "base-native-current", "call-native-site", "store-native-site", "return-native-site", "scalar-width", "scalar-offset", "extra-effect", "removed-store",
                    "store-before-base", "receiver-copy", "argument-copy", "receiver-register", "argument-register", "receiver-type",
                    "argument-type", "incoming-removed", "incoming-duplicate", "slot-receiver", "slot-argument", "entry-marker",
                    "exit-marker", "entry-predecessor", "exit-successor", "lost-binding", "method-flags", "method-name", "return-value", "changed-edge", "detached-block",
                })
                    Reject(constructor, instance, prefix, output, mutation);
            }
            foreach (var method in methods.Where(method => method.Name != ".ctor"))
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, method.GetExtraData<MethodDefinition>("AsmResolverMethod")!));
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void CheckArrayElementIdentity(ApplicationAnalysisContext app, AssemblyAnalysisContext assembly)
    {
        var original = new InjectedTypeAnalysisContext(assembly, "Neutral", "Element", app.SystemTypes.SystemObjectType, TypeAttributes.Public);
        var other = new InjectedTypeAnalysisContext(app.SystemTypes.SystemObjectType.DeclaringAssembly, "Neutral", "Element",
            app.SystemTypes.SystemObjectType, TypeAttributes.Public);
        var canonical = new SzArrayTypeAnalysisContext(original);
        var sameName = new SzArrayTypeAnalysisContext(other);
        Assert.That(canonical.FullName, Is.EqualTo(sameName.FullName));
        Assert.That(GenericInstanceFieldLayout.SameReferenceArgument(new SzArrayTypeAnalysisContext(original), canonical), Is.True);
        Assert.That(GenericInstanceFieldLayout.SameReferenceArgument(sameName, canonical), Is.False,
            "Equal array names do not establish the original element assembly/type identity.");
        Assert.That(GenericInstanceFieldLayout.SameReferenceArgument(new ArrayTypeAnalysisContext(original, 2),
            new ArrayTypeAnalysisContext(original, 3)), Is.False);
        Assert.That(GenericInstanceFieldLayout.SameReferenceArgument(new SzArrayTypeAnalysisContext(new SzArrayTypeAnalysisContext(other)),
            new SzArrayTypeAnalysisContext(canonical)), Is.False);
    }

    private static void CheckEnumBackingRecursion(ApplicationAnalysisContext app)
    {
        var type = app.AllTypes.First(candidate => candidate.Definition is
            { IsEnumType: true, EnumUnderlyingType.Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 } &&
            candidate.Fields.Count(field => !field.IsStatic) == 1);
        var backing = type.Fields.Single(field => !field.IsStatic);
        var underlying = type.Definition!.EnumUnderlyingType!;
        var raw = backing.BackingData!.Field.RawFieldType!;
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.EqualTo((4L, 4L)));
        var underlyingKind = underlying.Type;
        var backingKind = raw.Type;
        var backingData = raw.Data.Dummy;
        var modifier = raw.NumMods;
        try
        {
            underlying.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
            Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.Null, "unsupported enum underlying before resolution");
            underlying.Type = underlyingKind;
            raw.Type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE;
            raw.Data.Dummy = type.Definition.RawType.Data.Dummy;
            Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.Null, "self-referential enum backing before resolution");
            raw.Type = backingKind; raw.Data.Dummy = backingData;
            backing.OverrideFieldType = type;
            Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.Null, "self-referential backing override");
            backing.OverrideFieldType = null;
            raw.NumMods = 1;
            Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.Null, "modified enum backing");
        }
        finally
        {
            underlying.Type = underlyingKind; raw.Type = backingKind; raw.Data.Dummy = backingData;
            raw.NumMods = modifier; backing.OverrideFieldType = null;
        }
        Assert.That(GenericInstanceFieldLayout.GetSizeAndAlignment(type, 8), Is.EqualTo((4L, 4L)));
    }

    private static void Reject(MethodAnalysisContext method, GenericInstanceTypeAnalysisContext instance,
        FieldAnalysisContext prefix, MethodDefinition output, string mutation)
    {
        var owner = instance.GenericType;
        var definition = owner.Definition!;
        var container = definition.GenericContainer!;
        var parameter = container.GenericParameters.Single();
        var item = owner.Fields[0];
        var rawItem = item.BackingData!.Field.RawFieldType!;
        var rawRoot = instance.OriginalRawType!;
        var generic = rawRoot.GetGenericClass();
        var argumentPointer = generic.Context.ClassInst!.Pointers.Single();
        var rawArgument = instance.AppContext.Binary.GetIl2CppTypeFromPointer(argumentPointer);
        var rawParent = definition.RawBaseType!;
        var graph = method.ControlFlowGraph!;
        var operations = graph.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        var store = operations[1];
        var access = (FieldReference)store.Operands[0];
        var receiver = access.Local;
        var argument = (LocalVariable)store.Operands[1];
        var binding = method.GetExtraData<object>("ClosedGenericStorageRecovery")!;
        var restore = new Stack<Action>();
        void Change(Action apply, Action undo) { restore.Push(undo); apply(); }
        void Write(ulong address, byte[] bytes)
        {
            var pe = (PE)method.AppContext.Binary;
            var offset = pe.MapVirtualAddressToRaw(address);
            var previous = pe.GetRawBinaryContent().Slice(checked((int)offset), bytes.Length).ToArray();
            void Put(byte[] data)
            {
                var position = pe.BaseStream.Position;
                try { pe.BaseStream.Position = offset; pe.BaseStream.Write(data, 0, data.Length); }
                finally { pe.BaseStream.Position = position; }
            }
            Change(() => Put(bytes), () => Put(previous));
        }
        var arguments = instance.GenericArguments.ToArray();
        var fields = owner.Fields.ToArray();
        var parameterLocals = method.ParameterLocals.ToArray();
        var operands = method.ParameterOperands.ToArray();
        var blocks = graph.Blocks.ToArray();
        var blockInstructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var successors = blocks.Select(block => block.Successors.ToArray()).ToArray();
        var predecessors = blocks.Select(block => block.Predecessors.ToArray()).ToArray();
        restore.Push(() =>
        {
            instance.GenericArguments.Clear(); instance.GenericArguments.AddRange(arguments);
            owner.Fields.Clear(); owner.Fields.AddRange(fields);
            method.ParameterLocals.Clear(); method.ParameterLocals.AddRange(parameterLocals);
            method.ParameterOperands.Clear(); method.ParameterOperands.AddRange(operands);
            graph.Blocks.Clear(); graph.Blocks.AddRange(blocks);
            for (var i = 0; i < blocks.Length; i++)
            {
                blocks[i].Instructions.Clear(); blocks[i].Instructions.AddRange(blockInstructions[i]);
                blocks[i].Successors.Clear(); blocks[i].Successors.AddRange(successors[i]);
                blocks[i].Predecessors.Clear(); blocks[i].Predecessors.AddRange(predecessors[i]);
            }
        });
        try
        {
            switch (mutation)
            {
                case "argument-reference": instance.GenericArguments[0] = method.AppContext.SystemTypes.SystemObjectType; break;
                case "argument-removed": instance.GenericArguments.Clear(); break;
                case "argument-extra": instance.GenericArguments.Add(arguments[0]); break;
                case "argument-kind": { var old = rawArgument.Type; Change(() => rawArgument.Type = Il2CppTypeEnum.IL2CPP_TYPE_R8, () => rawArgument.Type = old); break; }
                case "argument-modifier": { var old = rawArgument.NumMods; Change(() => rawArgument.NumMods = 1, () => rawArgument.NumMods = old); break; }
                case "argument-self-cycle": { var kind = rawArgument.Type; var data = rawArgument.Data.Dummy; Change(() => { rawArgument.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY; rawArgument.Data.Dummy = argumentPointer; }, () => { rawArgument.Type = kind; rawArgument.Data.Dummy = data; }); break; }
                case "root-kind": { var old = rawRoot.Type; Change(() => rawRoot.Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, () => rawRoot.Type = old); break; }
                case "root-data": { var old = rawRoot.Data.Dummy; Change(() => rawRoot.Data.Dummy += 8, () => rawRoot.Data.Dummy = old); break; }
                case "root-modifier": { var old = rawRoot.NumMods; Change(() => rawRoot.NumMods = 1, () => rawRoot.NumMods = old); break; }
                case "layout-explicit": Change(() => owner.OverrideAttributes = owner.DefaultAttributes | TypeAttributes.ExplicitLayout, () => owner.OverrideAttributes = null); break;
                case "layout-pack": { var old = definition.Bitfield; Change(() => definition.Bitfield &= ~(1U << 10), () => definition.Bitfield = old); break; }
                case "layout-size": { var old = definition.Bitfield; Change(() => definition.Bitfield &= ~(1U << 11), () => definition.Bitfield = old); break; }
                case "layout-cctor": { var old = definition.Bitfield; Change(() => definition.Bitfield |= 1U << 3, () => definition.Bitfield = old); break; }
                case "layout-order": owner.Fields.Reverse(); break;
                case "layout-added-field": owner.Fields.Add(item); break;
                case "layout-field-type": Change(() => item.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type, () => item.OverrideFieldType = null); break;
                case "layout-field-marshal": Change(() => item.OverrideAttributes = item.DefaultAttributes | FieldAttributes.HasFieldMarshal, () => item.OverrideAttributes = null); break;
                case "layout-field-offset": Change(() => item.OverrideOffset = 4, () => item.OverrideOffset = null); break;
                case "layout-field-name": Change(() => item.OverrideName = item.DefaultName + "Changed", () => item.OverrideName = null); break;
                case "layout-field-self-cycle": { var kind = rawItem.Type; var data = rawItem.Data.Dummy; Assert.That(method.AppContext.Binary.TryGetTypeVirtualAddress(rawItem, out var pointer), Is.True); Change(() => { rawItem.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY; rawItem.Data.Dummy = pointer; }, () => { rawItem.Type = kind; rawItem.Data.Dummy = data; }); break; }
                case "layout-var-index": { var old = rawItem.Data.Dummy; Change(() => rawItem.Data.Dummy = ulong.MaxValue, () => rawItem.Data.Dummy = old); break; }
                case "parameter-position": { var old = parameter.genericParameterIndexInOwner; Change(() => parameter.genericParameterIndexInOwner = 1, () => parameter.genericParameterIndexInOwner = old); break; }
                case "parameter-owner": { var old = parameter.ownerIndex; Change(() => parameter.ownerIndex = default, () => parameter.ownerIndex = old); break; }
                case "parameter-flags": { var old = parameter.flags; Change(() => parameter.flags = (ushort)GenericParameterAttributes.ReferenceTypeConstraint, () => parameter.flags = old); break; }
                case "parameter-count": { var old = container.genericParameterCount; Change(() => container.genericParameterCount = 2, () => container.genericParameterCount = old); break; }
                case "layout-parent": Change(() => owner.OverrideBaseType = method.AppContext.SystemTypes.SystemObjectType, () => owner.OverrideBaseType = null); break;
                case "layout-parent-cycle": { var old = rawParent.Type; Change(() => rawParent.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, () => rawParent.Type = old); break; }
                case "prefix-overlap": Change(() => prefix.OverrideOffset = access.Offset, () => prefix.OverrideOffset = null); break;
                case "prefix-type": Change(() => prefix.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type, () => prefix.OverrideFieldType = null); break;
                case "native-cache": { var old = method.RawBytes; Change(() => method.RawBytes = new BinarySlice(old.AsSpan().ToArray().Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray()), () => method.RawBytes = old); break; }
                case "native-current": Write(method.UnderlyingPointer, [0xcc]); break;
                case "generic-current": Write(rawRoot.Data.GenericClass, new byte[8]); break;
                case "root-current": Assert.That(method.AppContext.Binary.TryGetTypeVirtualAddress(rawRoot, out var rawAddress), Is.True); Write(rawAddress + 8, new byte[4]); break;
                case "argument-table-current": Write(generic.Context.ClassInst!.pointerStart, new byte[8]); break;
                case "base-native-cache": { var callee = (MethodAnalysisContext)operations[0].Operands[0]; var old = callee.RawBytes; Change(() => callee.RawBytes = new BinarySlice(old.AsSpan().ToArray().Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray()), () => callee.RawBytes = old); break; }
                case "base-native-current": Write(((MethodAnalysisContext)operations[0].Operands[0]).UnderlyingPointer, [0xcc]); break;
                case "call-native-site": { var old = operations[0].NativeAddress; Change(() => operations[0].NativeAddress++, () => operations[0].NativeAddress = old); break; }
                case "store-native-site": { var old = operations[1].NativeAddress; Change(() => operations[1].NativeAddress++, () => operations[1].NativeAddress = old); break; }
                case "return-native-site": { var old = operations[2].NativeAddress; Change(() => operations[2].NativeAddress++, () => operations[2].NativeAddress = old); break; }
                case "scalar-width": Change(() => store.IntegerBitWidth = 64, () => store.IntegerBitWidth = 0); break;
                case "scalar-offset": { var old = access.Offset; Change(() => access.Offset++, () => access.Offset = old); break; }
                case "extra-effect": graph.FindBlockByInstruction(store)!.Instructions.Insert(0, new Instruction(50, OpCode.UnresolvedValue, argument)); break;
                case "removed-store": graph.FindBlockByInstruction(store)!.Instructions.Remove(store); break;
                case "store-before-base": { var first = graph.FindBlockByInstruction(operations[0])!; graph.FindBlockByInstruction(store)!.Instructions.Remove(store); first.Instructions.Insert(0, store); break; }
                case "receiver-copy": Change(() => access.Local = new LocalVariable("copy", receiver.Register, receiver.Type), () => access.Local = receiver); break;
                case "argument-copy": Change(() => store.SetOperand(1, new LocalVariable("copy", argument.Register, argument.Type)), () => store.SetOperand(1, argument)); break;
                case "receiver-register": { var old = receiver.Register; Change(() => receiver.Register = old.Copy(1), () => receiver.Register = old); break; }
                case "argument-register": { var old = argument.Register; Change(() => argument.Register = old.Copy(1), () => argument.Register = old); break; }
                case "receiver-type": { var old = receiver.Type; Change(() => receiver.Type = method.AppContext.SystemTypes.SystemObjectType, () => receiver.Type = old); break; }
                case "argument-type": { var old = argument.Type; Change(() => argument.Type = method.AppContext.SystemTypes.SystemInt64Type, () => argument.Type = old); break; }
                case "incoming-removed": method.ParameterLocals.Clear(); break;
                case "incoming-duplicate": method.ParameterLocals.Add(argument); break;
                case "slot-receiver": method.ParameterOperands[0] = new Register(null, "rdx"); break;
                case "slot-argument": method.ParameterOperands[1] = new Register(null, "rcx"); break;
                case "entry-marker": graph.FindBlockByInstruction(store)!.Instructions.Remove(store); graph.EntryBlock.Instructions.Add(store); break;
                case "exit-marker": graph.FindBlockByInstruction(store)!.Instructions.Remove(store); graph.ExitBlock.Instructions.Add(store); break;
                case "entry-predecessor": graph.EntryBlock.Predecessors.Add(graph.ExitBlock); break;
                case "exit-successor": graph.ExitBlock.Successors.Add(graph.EntryBlock); break;
                case "lost-binding": Change(() => method.PutExtraData<object>("ClosedGenericStorageRecovery", null!), () => method.PutExtraData("ClosedGenericStorageRecovery", binding)); break;
                case "method-flags": { var old = method.Definition!.flags; Change(() => method.Definition.flags ^= (ushort)MethodAttributes.HideBySig, () => method.Definition.flags = old); break; }
                case "method-name": Change(() => method.OverrideName = ".ctorChanged", () => method.OverrideName = null); break;
                case "return-value": Change(() => operations[2].AddOperands([argument]), () => operations[2].SetOperands()); break;
                case "changed-edge": graph.EntryBlock.Successors.Clear(); break;
                case "detached-block": graph.Blocks.Add(new Block { Instructions = [new Instruction(51, OpCode.Return)] }); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(ClosedGenericStorageRecovery.IsValidFor(method), Is.False, mutation);
            Assert.That(() => IlGenerator.GenerateIl(method, output), Throws.TypeOf<DecompilerException>().With.Message.Contains("Closed generic storage"), mutation);
        }
        finally { while (restore.Count != 0) restore.Pop()(); }
        Assert.That(ClosedGenericStorageRecovery.IsValidFor(method), Is.True, mutation + " restored");
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output), mutation + " restored");
    }
}
