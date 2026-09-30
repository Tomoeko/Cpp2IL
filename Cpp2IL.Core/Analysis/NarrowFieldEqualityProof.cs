using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Proves a captured byte- or word-sized field read is used only as a zero/nonzero comparison.
/// This does not model partial registers, signed ordering, arrays, or unknown native memory.
/// </summary>
internal static class NarrowFieldEqualityProof
{
    public static void Validate(MethodAnalysisContext context)
    {
        if (!context.ControlFlowGraph!.Instructions.Any(i => i.IntegerBitWidth is 8 or 16 &&
                i.OpCode is OpCode.Move or OpCode.CheckEqual or OpCode.CheckNotEqual))
            return;
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1")
            throw new DecompilerException("Native narrow field equality requires the supported Windows x64 Unity profile");
        FieldAnalysisContext? provedDirectGetterField = null;
        var checkedDirectGetter = false;
        Validate(context.ControlFlowGraph, (field, width) =>
        {
            if (HasUnchangedFieldLayout(field, width))
                return true;
            if (width != 8 || !HasUnchangedByteFieldLayoutWithFieldlessConstructedBase(field))
                return false;
            if (!checkedDirectGetter)
            {
                context.EnsureRawBytes();
                provedDirectGetterField = X86DirectBooleanFieldGetterProof.Find(
                    context, X86Utils.Iterate(context).ToArray());
                checkedDirectGetter = true;
            }
            return ReferenceEquals(provedDirectGetterField, field.Field);
        });
    }

    internal static void Validate(ISILControlFlowGraph graph, Func<FieldReference, bool> isExactByteField)
        => Validate(graph, (field, width) => width == 8 && isExactByteField(field));

    internal static void Validate(ISILControlFlowGraph graph, Func<FieldReference, int, bool> isExactField)
    {
        var instructions = graph.Instructions;
        var narrow = instructions.Where(i => i.IntegerBitWidth is 8 or 16).ToArray();
        if (narrow.Length == 0)
            return;
        var mutable = OperandEffects.LocalsWithMutableStorage(instructions);
        foreach (var instruction in narrow)
        {
            var widthName = instruction.IntegerBitWidth == 8 ? "byte" : "word";
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable captured, FieldReference field] })
            {
                if (!isExactField(field, instruction.IntegerBitWidth) || !ReferenceEquals(captured.Type, field.Field.FieldType) ||
                    mutable.Contains(captured) || instructions.Count(i => ReferenceEquals(i.Destination, captured)) != 1)
                    throw new DecompilerException($"Native {widthName} capture requires an unchanged, uniquely assigned {widthName}-sized instance field; absent modifier counts do not prove nonvolatile semantics");
                continue;
            }
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) ||
                instruction.Operands is not [_, LocalVariable value, Immediate { Value: 0 }])
                throw new DecompilerException($"Native {widthName} comparison supports only proved field equality against zero");

            var definitions = narrow.Where(i => ReferenceEquals(i.Destination, value)).ToArray();
            if (definitions is not [{ OpCode: OpCode.Move, Operands: [_, FieldReference] } capture] ||
                capture.IntegerBitWidth != instruction.IntegerBitWidth ||
                !graph.Blocks.Any(block => block.Instructions.IndexOf(capture) is var definitionIndex && definitionIndex >= 0 &&
                    block.Instructions.IndexOf(instruction) > definitionIndex))
                throw new DecompilerException($"Native {widthName} equality requires a preceding field capture of the same width in the same block");
        }
    }

    // This proves storage width/layout only. Exact-target controls demonstrate that NumMods=0
    // can hide modreq(IsVolatile). The lifter admits only a direct byte/word-memory CMP with zero;
    // separate loads, barrier calls and register TEST forms retain their own unresolved effects.
    internal static bool HasUnchangedByteFieldLayout(FieldReference reference)
        => HasUnchangedFieldLayout(reference, 8);

    // A constructed ancestor has no projected Fields or BaseType in the analysis model.
    // Only this direct-getter proof may walk a fieldless generic definition's unchanged,
    // non-generic base chain. Other narrow-field proofs retain their existing gate.
    internal static bool HasUnchangedByteFieldLayoutWithFieldlessConstructedBase(FieldReference reference)
        => HasUnchangedFieldLayout(reference, 8, false, true);

    internal static bool HasUnchangedReferenceFieldLayout(FieldReference reference)
    {
        var field = reference.Field;
        return !field.FieldType.IsValueType &&
               HasUnchangedFieldLayout(reference,
                   field.DeclaringType.AppContext.Binary.PointerSizeBytes * 8, true);
    }

    // Constructed instances do not project their inherited fields in the model.
    // This path is only for a separately proved complete Boolean[] operation whose
    // immediate generic base definition is fieldless and whose remaining chain
    // and sibling fields have exact, nonoverlapping metadata offsets.
    internal static bool HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(
        FieldReference reference)
    {
        var field = reference.Field;
        return !field.FieldType.IsValueType &&
               HasUnchangedFieldLayout(reference,
                   field.DeclaringType.AppContext.Binary.PointerSizeBytes * 8, true, true);
    }

    internal static bool HasUnchangedFieldLayout(FieldReference reference, int width)
        => HasUnchangedFieldLayout(reference, width, false);

    internal static bool HasUnchangedEnum32FieldLayout(FieldReference reference)
        => Enum32StorageProof.IsUnchanged(reference.Field.FieldType) &&
           HasUnchangedFieldLayout(reference, 32, false, enumField: true);

    internal static bool HasUnchangedSingleFieldLayout(FieldReference reference)
        => reference.Field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4 &&
           HasUnchangedFieldLayout(reference, 32, false, false, true);

    internal static bool HasUnchangedFloatingFieldLayout(FieldReference reference, int width)
        => (width == 32 && reference.Field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4 ||
            width == 64 && reference.Field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R8) &&
           HasUnchangedFieldLayout(reference, width, false, false, true);

    private static bool HasUnchangedFieldLayout(FieldReference reference, int width,
        bool referenceField, bool allowFieldlessConstructedBase = false,
        bool floatingField = false, bool enumField = false)
    {
        var field = reference.Field;
        var owner = field.DeclaringType;
        var receiver = reference.Local.Type;
        if (width <= 0 || field.IsStatic || field.Attributes != field.DefaultAttributes ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
            field.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            // Resolving a metadata array type can create a new wrapper each time. The
            // override records an actual change; object identity does not.
            field.OverrideFieldType != null ||
            !(referenceField ||
              (floatingField && (width == 32 && field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4 ||
                                 width == 64 && field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R8)) ||
              enumField && width == 32 && Enum32StorageProof.IsUnchanged(field.FieldType) ||
              HasExactStorageWidth(field.FieldType, width) ||
              width == owner.AppContext.Binary.PointerSizeBytes * 8 &&
              field.FieldType.Type is (Il2CppTypeEnum.IL2CPP_TYPE_I or
                  Il2CppTypeEnum.IL2CPP_TYPE_U)) ||
            field.Offset < 2 * owner.AppContext.Binary.PointerSizeBytes || field.Offset != field.DefaultOffset || reference.Offset != field.Offset ||
            (!ReferenceEquals(receiver, owner) &&
             !NullCheckedCall.HasUnchangedReferenceBase(receiver, owner)) ||
            owner.IsValueType || owner.IsEnumType ||
            owner is GenericInstanceTypeAnalysisContext || owner.GenericParameters.Count != 0 ||
            owner.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true } definition ||
            !ReferenceEquals(field.BackingData?.Field.DeclaringType, definition) ||
            (ulong)field.Offset + (ulong)(width / 8) > definition.RawSizes.instance_size ||
            owner.Attributes != owner.DefaultAttributes || (owner.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
            return false;

        // A default layout is not permission to accept inconsistent or overlapping offsets.
        // Include base fields. An unchanged field starting after the accessed
        // span cannot overlap it; unknown earlier extents still fail closed.
        var visited = new HashSet<TypeAnalysisContext>();
        var sawConstructedBase = false;
        var reachedObject = false;
        var reachedOwner = false;
        for (var type = receiver; type != null;)
        {
            if (!visited.Add(type))
                return false;
            // Every derived layout must preserve the inherited field's original offset
            // without overlapping storage.
            if (ReferenceEquals(type, owner))
                reachedOwner = true;
            else if (!reachedOwner &&
                     (type.Attributes != type.DefaultAttributes ||
                      !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
                      type.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                          RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0,
                              Byref: 0, Pinned: 0 } }))
                return false;
            if (type is GenericInstanceTypeAnalysisContext constructed)
            {
                sawConstructedBase = true;
                if (!allowFieldlessConstructedBase ||
                    !HasUnchangedFieldlessGenericBase(constructed))
                    return false;
                // GenericInstanceTypeAnalysisContext.BaseType and Fields are empty for
                // metadata-backed instances. The generic definition has no instance
                // fields, so its unchanged non-generic base can be checked directly.
                type = constructed.GenericType.BaseType;
                continue;
            }
            if (type.GenericParameters.Count != 0 ||
                (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return false;
            reachedObject |= ReferenceEquals(type, owner.AppContext.SystemTypes.SystemObjectType);
            if (allowFieldlessConstructedBase && type != owner &&
                (type.Attributes != type.DefaultAttributes ||
                 !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
                 (!ReferenceEquals(type, owner.AppContext.SystemTypes.SystemObjectType) &&
                  type.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true })))
                return false;
            foreach (var other in type.Fields)
            {
                if (ReferenceEquals(other, field))
                    continue;
                // A changed instance/static classification must not remove a
                // neighbor from the overlap proof before its attributes are checked.
                if (other.Attributes != other.DefaultAttributes)
                    return false;
                if (other.IsStatic || (other.Attributes & FieldAttributes.Literal) != 0)
                    continue;
                if (!ReferenceEquals(other.DeclaringType, type) ||
                    !ReferenceEquals(other.BackingData?.Field.DeclaringType, type.Definition) ||
                    other.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
                    (other.Attributes & FieldAttributes.HasFieldMarshal) != 0 ||
                    other.Name != other.DefaultName || other.OverrideFieldType != null ||
                    other.Offset < 2 * owner.AppContext.Binary.PointerSizeBytes || other.Offset != other.DefaultOffset ||
                    !HasNonoverlappingStorage(field.Offset, width / 8, other.Offset,
                        () => StorageSize(other.FieldType, owner.AppContext.Binary.PointerSizeBytes)))
                    return false;
            }
            type = type.BaseType;
        }
        return reachedOwner && (!allowFieldlessConstructedBase || (sawConstructedBase && reachedObject));
    }

    private static bool HasUnchangedFieldlessGenericBase(GenericInstanceTypeAnalysisContext constructed)
    {
        var definition = constructed.GenericType;
        return constructed.HasUnchangedOriginalRawType &&
               constructed.OriginalRawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST,
                   NumMods: 0, Byref: 0, Pinned: 0 } &&
               constructed.GenericArguments.Count != 0 &&
               constructed.GenericArguments.Count == definition.GenericParameters.Count &&
               constructed.GenericArguments.All(argument => argument.Type is not
                   (Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR)) &&
               !definition.IsValueType && !definition.IsInterface &&
               definition.Definition is { HasCctor: false, PackingSizeIsDefault: true,
                   ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               definition.Attributes == definition.DefaultAttributes &&
               (definition.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout &&
               ReferenceEquals(definition.BaseType, definition.DefaultBaseType) &&
               definition.BaseType != null &&
               definition.Fields.All(field => field.Attributes == field.DefaultAttributes &&
                   (field.IsStatic || (field.Attributes & FieldAttributes.Literal) != 0));
    }

    internal static bool HasExactStorageWidth(TypeAnalysisContext type, int width) => width switch
    {
        8 => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1,
        16 => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or Il2CppTypeEnum.IL2CPP_TYPE_CHAR,
        32 => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4,
        64 => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8,
        _ => false,
    };

    internal static bool StorageRangesOverlap(long firstOffset, long firstSize, long secondOffset, long secondSize)
        => firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;

    internal static bool HasNonoverlappingStorage(long offset, long size, long otherOffset,
        Func<long> getOtherSize)
    {
        if (offset < 0 || size <= 0 || offset > long.MaxValue - size || otherOffset < 0)
            return false;
        // A field's storage extends forward from its independently authenticated
        // start. Do not invent the size of a later constructed value-type field.
        if (otherOffset >= offset + size)
            return true;
        var otherSize = getOtherSize();
        return otherSize > 0 && otherOffset <= long.MaxValue - otherSize &&
               !StorageRangesOverlap(offset, size, otherOffset, otherSize);
    }

    private static long StorageSize(TypeAnalysisContext type, int pointerSize) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
        Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
        Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or Il2CppTypeEnum.IL2CPP_TYPE_R8 => 8,
        _ when type.IsEnumType => StorageSize(type.EnumUnderlyingType!, pointerSize),
        _ when !type.IsValueType => pointerSize,
        _ => TypeSizes.UnboxedSize(type, pointerSize),
    };
}
