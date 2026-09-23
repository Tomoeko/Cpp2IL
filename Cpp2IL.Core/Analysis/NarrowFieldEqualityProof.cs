using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Proves a captured byte-sized field read is used only as a zero/nonzero comparison.
/// This does not model partial registers, signed ordering, arrays, or unknown native memory.
/// </summary>
internal static class NarrowFieldEqualityProof
{
    public static void Validate(MethodAnalysisContext context)
    {
        if (!context.ControlFlowGraph!.Instructions.Any(i => i.IntegerBitWidth == 8 &&
                i.OpCode is OpCode.Move or OpCode.CheckEqual or OpCode.CheckNotEqual))
            return;
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } || context.AppContext.UnityVersion.ToString() != "2021.3.35f1")
            throw new DecompilerException("Native byte field equality requires the supported Windows x64 Unity profile");
        Validate(context.ControlFlowGraph, HasUnchangedByteFieldLayout);
    }

    internal static void Validate(ISILControlFlowGraph graph, Func<FieldReference, bool> isExactByteField)
    {
        var instructions = graph.Instructions;
        var narrow = instructions.Where(i => i.IntegerBitWidth == 8).ToArray();
        if (narrow.Length == 0)
            return;
        var mutable = OperandEffects.LocalsWithMutableStorage(instructions);
        foreach (var instruction in narrow)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable captured, FieldReference field] })
            {
                if (!isExactByteField(field) || !ReferenceEquals(captured.Type, field.Field.FieldType) ||
                    mutable.Contains(captured) || instructions.Count(i => ReferenceEquals(i.Destination, captured)) != 1)
                    throw new DecompilerException("Native byte capture requires an unchanged, uniquely assigned byte-sized instance field; absent modifier counts do not prove nonvolatile semantics");
                continue;
            }
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) ||
                instruction.Operands is not [_, LocalVariable value, Immediate { Value: 0 }])
                throw new DecompilerException("Native byte comparison supports only proved field equality against zero");

            var definitions = narrow.Where(i => ReferenceEquals(i.Destination, value)).ToArray();
            if (definitions is not [{ OpCode: OpCode.Move, Operands: [_, FieldReference] } capture] ||
                !graph.Blocks.Any(block => block.Instructions.IndexOf(capture) is var definitionIndex && definitionIndex >= 0 &&
                    block.Instructions.IndexOf(instruction) > definitionIndex))
                throw new DecompilerException("Native byte equality requires a preceding field capture in the same block");
        }
    }

    // This proves storage width/layout only. Exact-target controls demonstrate that NumMods=0
    // can hide modreq(IsVolatile). The lifter admits only a direct byte-memory CMP with zero;
    // separate loads, barrier calls and register TEST forms retain their own unresolved effects.
    internal static bool HasUnchangedByteFieldLayout(FieldReference reference)
    {
        var field = reference.Field;
        var owner = field.DeclaringType;
        if (field.IsStatic || field.Attributes != field.DefaultAttributes ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
            field.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(field.FieldType, field.DefaultFieldType) ||
            field.FieldType.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1) ||
            field.Offset < 2 * owner.AppContext.Binary.PointerSizeBytes || field.Offset != field.DefaultOffset || reference.Offset != field.Offset ||
            !ReferenceEquals(reference.Local.Type, owner) || owner.IsValueType || owner.IsEnumType ||
            owner is GenericInstanceTypeAnalysisContext || owner.GenericParameters.Count != 0 ||
            owner.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            owner.Attributes != owner.DefaultAttributes || (owner.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
            return false;

        // A default layout is not permission to accept inconsistent or overlapping offsets.
        // Include base fields and reject unknown extents instead of guessing their storage size.
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (type is GenericInstanceTypeAnalysisContext || type.GenericParameters.Count != 0 ||
                (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return false;
            foreach (var other in type.Fields.Where(f => !f.IsStatic && (f.Attributes & FieldAttributes.Literal) == 0))
            {
                if (ReferenceEquals(other, field))
                    continue;
                var size = StorageSize(other.FieldType, owner.AppContext.Binary.PointerSizeBytes);
                if (other.Attributes != other.DefaultAttributes || !ReferenceEquals(other.FieldType, other.DefaultFieldType) ||
                    other.Offset < 0 || other.Offset != other.DefaultOffset || size <= 0 ||
                    other.Offset <= field.Offset && field.Offset - (long)other.Offset < size)
                    return false;
            }
        }
        return true;
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
