using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates one complete low32 read of an original eight-byte instance field.</summary>
internal static class X64WideFieldLow32ReadProof
{
    internal const string EvidenceKey = "X64WideFieldLow32ReadProof";
    internal const string CaptureRegister = "wide_field_low32_capture";
    internal const string ResultRegister = "wide_field_low32_result";
    internal sealed record Shape(NativeInstruction Load, NativeInstruction Return, int Offset);

    // Scalars, rather than mutable definitions, retain the original declaration
    // and layout facts through analysis and final emission.
    internal sealed class MetadataState
    {
        private readonly object[] _values;
        internal MetadataState(List<object> values) => _values = values.ToArray();
        internal bool Matches(MetadataState other) => _values.SequenceEqual(other._values);
    }

    internal sealed record Proof(Shape Native, FieldAnalysisContext Field, MetadataState Metadata)
    {
        internal bool Matches(Proof other) => Native == other.Native && ReferenceEquals(Field, other.Field) &&
            Metadata.Matches(other.Metadata);
    }

    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var capture = new IsilRegister(null, CaptureRegister);
        var result = new IsilRegister(null, ResultRegister);
        var signed = ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type);
        // The managed field retains its full I8/U8 type. The independent MOV32
        // proof permits the explicit low32 conversion, not retyping the field.
        // Stripped player metadata can omit authored volatile modifiers; this
        // projection does not establish concurrent or volatile memory fidelity.
        return
        [
            new(0, OpCode.Move, capture, new ISIL.MemoryOperand(new IsilRegister(null, "rcx"), null, proof.Native.Offset))
                { NativeAddress = proof.Native.Load.IP },
            new(1, OpCode.IntegerExtend, result, capture, new Immediate(32), new Immediate(32), new Immediate(signed ? 1 : 0))
                { NativeAddress = proof.Native.Load.IP },
            new(2, OpCode.Return, result) { NativeAddress = proof.Native.Return.IP },
        ];
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext: { Binary: PE } app, DeclaringType: { Definition: { } } owner,
                Definition: { parameterCount: 0 } definition } || method.IsStatic || method.IsVirtual ||
            method.Parameters.Count != 0 || (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !X64ArrayLengthReadProof.HasUnchangedMethod(method) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) &&
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemUInt32Type) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4,
                NumMods: 0, Byref: 0, Pinned: 0 } rawReturn || rawReturn.Type != method.ReturnType.Type)
            return null;
        try
        {
            var body = X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) ??
                       X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32);
            if (body == null || TryProveShape(body) is not { } shape ||
                owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.Offset).ToArray() is not [var field] ||
                !ReferenceEquals(field.DeclaringType, owner) ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt64Type) &&
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemUInt64Type) ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawField || rawField.Type != field.FieldType.Type ||
                field.Name != field.DefaultName || !ReferenceEquals(field.BackingData.Field.DeclaringType, owner.Definition) ||
                field.Offset < 16 || (ulong)field.Offset + 8 > owner.Definition!.RawSizes.instance_size ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field,
                    new LocalVariable("receiver", new IsilRegister(null, "rcx"), owner), field.Offset), 64) ||
                Snapshot(method) is not { } metadata)
                return null;
            return new Proof(shape, field, metadata);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (2 or 3) || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            body.Count == 3 && body[0] is not
                { Code: Code.Nopw or Code.Nopd, OpCount: 0, FlowControl: FlowControl.Next })
            return null;
        var load = body[^2];
        var ret = body[^1];
        return load.Code == Code.Mov_r32_rm32 && load.OpCount == 2 && load.Op0Kind == OpKind.Register &&
               load.Op0Register == NativeRegister.EAX && load.Op1Kind == OpKind.Memory &&
               load.MemoryBase == NativeRegister.RCX && load.MemoryIndex == NativeRegister.None &&
               load.MemoryIndexScale == 1 && load.MemorySize.GetSize() == 4 &&
               load.MemoryDisplacement64 is >= 16 and < 4096 && ret.Code == Code.Retnq && ret.OpCount == 0
            ? new Shape(load, ret, checked((int)load.MemoryDisplacement64)) : null;
    }

    private static MetadataState? Snapshot(MethodAnalysisContext method)
    {
        var definition = method.Definition!;
        var values = new List<object>
        {
            method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx, definition.declaringTypeIdx, definition.parameterStart, definition.parameterCount,
        };
        CaptureType(definition.RawReturnType!, values);
        var visited = new HashSet<TypeAnalysisContext>();
        var reachedObject = false;
        for (var type = method.DeclaringType; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || type.IsValueType || type.IsInterface || type.IsGenericInstance ||
                type.GenericParameters.Count != 0 || type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
                type.Attributes != type.DefaultAttributes || !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
                (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                type.Definition is not { GenericContainer: null, PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true, RawType: { NumMods: 0, Byref: 0, Pinned: 0 } } owner ||
                type.Fields.Count != owner.FieldCount)
                return null;
            reachedObject |= ReferenceEquals(type, method.AppContext.SystemTypes.SystemObjectType);
            values.AddRange([type, type.Name, type.Namespace, type.Attributes, owner.NameIndex, owner.NamespaceIndex,
                owner.Token, owner.Bitfield, owner.ByvalTypeIndex, owner.ParentIndex, owner.RawSizes.instance_size,
                owner.FieldCount]);
            CaptureType(owner.RawType, values);
            foreach (var field in type.Fields)
            {
                if (field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                    field.Offset != field.DefaultOffset || field.OverrideFieldType != null ||
                    field.BackingData?.Field.RawFieldType is not { } raw)
                    return null;
                values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType,
                    field.BackingData.Field.nameIndex, field.BackingData.Field.token, field.BackingData.Field.typeIndex]);
                CaptureType(raw, values);
            }
        }
        return reachedObject ? new MetadataState(values) : null;
    }

    private static void CaptureType(Il2CppType type, List<object> values) => values.AddRange(
        [type.Bits, type.Datapoint, type.Data.Dummy, type.Attrs, type.Type, type.NumMods, type.Byref, type.Pinned, type.ValueType]);
}
