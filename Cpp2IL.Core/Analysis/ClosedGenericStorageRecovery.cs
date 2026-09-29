using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>Retains the closed prefix layouts consumed by the complete folded constructor proof.</summary>
internal static class ClosedGenericStorageRecovery
{
    private const string BindingKey = "ClosedGenericStorageRecovery";
    private sealed record Binding(X64FoldedInt32ConstructorProof.Evidence Native, Instruction[] Operations,
        LocalVariable Receiver, LocalVariable Argument, Register ReceiverRegister, Register ArgumentRegister,
        FieldReference Store, GenericInstanceTypeAnalysisContext[] Prefixes, object[] Facts, byte[] NativeBytes);

    internal static void MarkIfConsumed(MethodAnalysisContext method, FieldAnalysisContext store)
    {
        if (store.DeclaringType.Fields.Any(field => !field.IsStatic && field.Offset < store.Offset &&
            field.FieldType is GenericInstanceTypeAnalysisContext { IsValueType: true } prefix &&
            ClosedGenericValueLayoutProof.WasProved(prefix)))
            NativeRecoveryProofTracker.Mark(method, BindingKey);
    }

    internal static void Run(MethodAnalysisContext method)
    {
        if (method.Name != ".ctor" || method.DeclaringType is not { } owner ||
            !owner.Fields.Any(field => field.BackingData?.Field.RawFieldType?.Type ==
                LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST))
            return;
        if (TryBind(method) is not { } binding)
            return;
        NativeRecoveryProofTracker.Mark(method, BindingKey);
        method.PutExtraData(BindingKey, binding);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, BindingKey);

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            return method.GetExtraData<Binding>(BindingKey) is { } saved &&
                Capture(method, saved.Native.BaseConstructor).SequenceEqual(saved.Facts) && TryBind(method) is { } current &&
                ReferenceEquals(saved.Native.State, current.Native.State) &&
                ReferenceEquals(saved.Native.BaseConstructor, current.Native.BaseConstructor) &&
                saved.Native.NativeEnd == current.Native.NativeEnd &&
                current.Operations.SequenceEqual(saved.Operations) &&
                ReferenceEquals(current.Receiver, saved.Receiver) && ReferenceEquals(current.Argument, saved.Argument) &&
                current.ReceiverRegister == saved.ReceiverRegister && current.ArgumentRegister == saved.ArgumentRegister &&
                ReferenceEquals(current.Store, saved.Store) && current.Prefixes.SequenceEqual(saved.Prefixes) &&
                current.Facts.SequenceEqual(saved.Facts) && current.NativeBytes.SequenceEqual(saved.NativeBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or NullReferenceException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static Binding? TryBind(MethodAnalysisContext method)
    {
        try
        {
            if (method.ControlFlowGraph is not { } graph || graph.EntryBlock.Instructions.Count != 0 ||
                graph.ExitBlock.Instructions.Count != 0 ||
                graph.EntryBlock.Predecessors.Count != 0 || graph.ExitBlock.Successors.Count != 0 ||
                graph.Blocks.Distinct().Count() != graph.Blocks.Count ||
                !graph.Blocks.Contains(graph.EntryBlock) || !graph.Blocks.Contains(graph.ExitBlock) ||
                graph.Blocks.Where(block => block != graph.EntryBlock && block != graph.ExitBlock)
                    .SelectMany(block => block.Instructions).ToArray() is not { } emitted ||
                !emitted.SequenceEqual(graph.Instructions) ||
                emitted.Any(instruction => instruction.OpCode == OpCode.Nop &&
                    (instruction.Operands.Count != 0 || instruction.IntegerBitWidth != 0 ||
                     instruction.CallSemantics != CallSemantics.Direct)) ||
                emitted.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray() is not
                [{ OpCode: OpCode.CallVoid, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [MethodAnalysisContext baseConstructor, LocalVariable receiver] },
                 { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [FieldReference store, LocalVariable argument] },
                 { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct, Operands.Count: 0 }] active ||
                !ReferenceEquals(store.Local, receiver) || !Incoming(method, receiver, 0, "rcx", true) ||
                !Incoming(method, argument, 1, "rdx", false) ||
                !ReferenceEquals(receiver.Type, method.DeclaringType) ||
                !ReferenceEquals(argument.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
                OperandEffects.LocalsWithMutableStorage(emitted).Count != 0 ||
                emitted.Any(instruction => instruction.ImplicitDefinition != null) ||
                X64FoldedInt32ConstructorProof.Find(method, X86Utils.Iterate(method).ToArray()) is not { } native ||
                !ReferenceEquals(native.State, store.Field) || !ReferenceEquals(native.BaseConstructor, baseConstructor) ||
                X86Utils.Iterate(method).Take(12).ToArray() is not { Length: 12 } nativeBody ||
                active[0].NativeAddress != nativeBody[6].IP || active[1].NativeAddress != nativeBody[7].IP ||
                active[2].NativeAddress != nativeBody[11].IP ||
                store.Offset != store.Field.Offset ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(store, 32))
                return null;
            var bodies = graph.Blocks.Where(block => block != graph.EntryBlock && block != graph.ExitBlock).ToArray();
            var previous = graph.EntryBlock;
            foreach (var block in bodies)
            {
                if (previous.Successors is not [var next] || !ReferenceEquals(next, block) ||
                    block.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, previous))
                    return null;
                previous = block;
            }
            if (previous.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock) ||
                graph.ExitBlock.Predecessors is not [var final] || !ReferenceEquals(final, previous))
                return null;
            var prefixes = store.Field.DeclaringType.Fields.Where(field => !field.IsStatic &&
                    field.Offset < store.Offset && field.FieldType is GenericInstanceTypeAnalysisContext { IsValueType: true })
                .Select(field => (GenericInstanceTypeAnalysisContext)field.FieldType).ToArray();
            if (prefixes.Length == 0 || prefixes.Any(prefix => ClosedGenericValueLayoutProof.Find(prefix) == null))
                return null;
            return new Binding(native, active, receiver, argument, receiver.Register, argument.Register,
                store, prefixes, Capture(method, native.BaseConstructor), method.RawBytes.AsSpan().Slice(0, checked((int)(native.NativeEnd - method.UnderlyingPointer))).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool Incoming(MethodAnalysisContext method, LocalVariable local, int slot, string name, bool receiver) =>
        method.ParameterOperands.Count > slot && method.ParameterOperands[slot] is Register original &&
        original == new Register(null, name) && local.Register == original &&
        local.IsThis == receiver && !local.IsMethodInfo && !local.IsReturn &&
        method.ParameterLocals.Count(item => ReferenceEquals(item, local)) == 1 &&
        method.ParameterLocals.Count(item => item.Register.Number == original.Number) == 1 &&
        method.Locals.Count(item => ReferenceEquals(item, local)) <= 1 &&
        !method.Locals.Any(item => item.Register.Number == original.Number && !ReferenceEquals(item, local)) &&
        !method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local));

    private static object[] Capture(MethodAnalysisContext method, MethodAnalysisContext baseConstructor)
    {
        var values = new List<object>();
        var raw = method.Definition!;
        values.Add(method.UnderlyingPointer); values.Add(method.Name); values.Add(method.Attributes); values.Add(method.ImplAttributes);
        values.Add(method.OverrideReturnType ?? (object)false); values.Add(raw.nameIndex); values.Add(raw.flags); values.Add(raw.iflags); values.Add(raw.slot);
        values.Add(raw.token); values.Add(raw.declaringTypeIdx); values.Add(raw.returnTypeIdx);
        values.Add(raw.parameterStart); values.Add(raw.parameterCount); values.Add(raw.genericContainerIndex);
        var owner = method.DeclaringType!;
        var definition = owner.Definition!;
        values.Add(owner); values.Add(owner.Name); values.Add(owner.Namespace); values.Add(owner.Attributes);
        values.Add(owner.OverrideBaseType ?? (object)false); values.Add(owner.DeclaringType ?? (object)false);
        values.Add(definition.Flags); values.Add(definition.Bitfield); values.Add(definition.DeclaringTypeIndex);
        values.Add(definition.ParentIndex); AddRaw(definition.RawType); AddRaw(definition.RawBaseType!);
        values.Add(definition.RawSizes.instance_size); values.Add(definition.RawSizes.native_size);
        foreach (var field in owner.Fields)
        {
            var original = field.BackingData!.Field;
            values.Add(field); values.Add(field.Name); values.Add(field.Attributes); values.Add(field.Offset);
            values.Add(field.OverrideFieldType ?? (object)false); values.Add(original.nameIndex); values.Add(original.token); values.Add(original.typeIndex);
            AddRaw(original.RawFieldType!);
        }
        foreach (var parameter in method.Parameters)
        {
            values.Add(parameter); values.Add(parameter.Name); values.Add(parameter.Attributes);
            values.Add(parameter.OverrideParameterType ?? (object)false);
            AddRaw(parameter.Definition!.RawType!);
        }
        AddRaw(raw.RawReturnType!);
        var baseRaw = baseConstructor.Definition!;
        values.Add(baseConstructor); values.Add(baseConstructor.Name); values.Add(baseConstructor.Attributes);
        values.Add(baseConstructor.ImplAttributes); values.Add(baseConstructor.UnderlyingPointer);
        values.Add(baseConstructor.OverrideReturnType ?? (object)false);
        values.Add(baseRaw.nameIndex); values.Add(baseRaw.flags); values.Add(baseRaw.iflags); values.Add(baseRaw.declaringTypeIdx);
        values.Add(baseRaw.returnTypeIdx); values.Add(baseRaw.parameterStart); values.Add(baseRaw.parameterCount);
        AddRaw(baseRaw.RawReturnType!);
        return values.ToArray();

        void AddRaw(LibCpp2IL.BinaryStructures.Il2CppType type)
        {
            values.Add(type.Datapoint); values.Add(type.Bits); values.Add(type.Data.Dummy); values.Add(type.Attrs);
            values.Add(type.Type); values.Add(type.NumMods); values.Add(type.Byref); values.Add(type.Pinned); values.Add(type.ValueType);
        }
    }
}
