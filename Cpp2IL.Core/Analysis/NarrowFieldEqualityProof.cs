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

    internal static bool HasUnchangedFieldLayout(FieldReference reference, int width)
        => HasUnchangedFieldLayout(reference, width, false);

    internal static bool HasUnchangedSingleFieldLayout(FieldReference reference)
        => reference.Field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4 &&
           HasUnchangedFieldLayout(reference, 32, false, false, true);

    private static bool HasUnchangedFieldLayout(FieldReference reference, int width,
        bool referenceField, bool allowFieldlessConstructedBase = false,
        bool singleField = false)
    {
        var field = reference.Field;
        var owner = field.DeclaringType;
        if (field.IsStatic || field.Attributes != field.DefaultAttributes ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
            field.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            // Resolving a metadata array type can create a new wrapper each time. The
            // override records an actual change; object identity does not.
            field.OverrideFieldType != null ||
            !(referenceField ||
              (singleField && field.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4) ||
              HasExactStorageWidth(field.FieldType, width) ||
              width == owner.AppContext.Binary.PointerSizeBytes * 8 &&
              field.FieldType.Type is (Il2CppTypeEnum.IL2CPP_TYPE_I or
                  Il2CppTypeEnum.IL2CPP_TYPE_U)) ||
            field.Offset < 2 * owner.AppContext.Binary.PointerSizeBytes || field.Offset != field.DefaultOffset || reference.Offset != field.Offset ||
            !ReferenceEquals(reference.Local.Type, owner) || owner.IsValueType || owner.IsEnumType ||
            owner is GenericInstanceTypeAnalysisContext || owner.GenericParameters.Count != 0 ||
            owner.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            owner.Attributes != owner.DefaultAttributes || (owner.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
            return false;

        // A default layout is not permission to accept inconsistent or overlapping offsets.
        // Include base fields and reject unknown extents instead of guessing their storage size.
        var visited = new HashSet<TypeAnalysisContext>();
        var sawConstructedBase = false;
        var reachedObject = false;
        for (var type = owner; type != null;)
        {
            if (!visited.Add(type))
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
            foreach (var other in type.Fields.Where(f => !f.IsStatic && (f.Attributes & FieldAttributes.Literal) == 0))
            {
                if (ReferenceEquals(other, field))
                    continue;
                var size = StorageSize(other.FieldType, owner.AppContext.Binary.PointerSizeBytes);
                if (other.Attributes != other.DefaultAttributes || other.OverrideFieldType != null ||
                    other.Offset < 0 || other.Offset != other.DefaultOffset || size <= 0 ||
                    StorageRangesOverlap(field.Offset, width / 8, other.Offset, size))
                    return false;
            }
            type = type.BaseType;
        }
        return !allowFieldlessConstructedBase || (sawConstructedBase && reachedObject);
    }

    private static bool HasUnchangedFieldlessGenericBase(GenericInstanceTypeAnalysisContext constructed)
    {
        var definition = constructed.GenericType;
        return constructed.GenericArguments.Count != 0 &&
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
               definition.Fields.All(field => field.IsStatic ||
                   (field.Attributes & FieldAttributes.Literal) != 0);
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
