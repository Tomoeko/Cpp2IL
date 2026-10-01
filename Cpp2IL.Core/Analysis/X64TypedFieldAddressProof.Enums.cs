using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64TypedFieldAddressProof
{
    private static bool IsSignedEnum32(TypeAnalysisContext type)
    {
        // Check raw base and storage descriptors before resolving managed types.
        // A missing descriptor must decline this route, rather than creating
        // a managed type from incomplete player metadata.
        if (type.Definition is not
                { IsEnumType: true, HasCctor: false, RawType: { Data: not null }, DeclaringTypeIndex: { IsNull: true },
                    RawBaseType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Data: not null,
                        NumMods: 0, Byref: 0, Pinned: 0 },
                    EnumUnderlyingType: { Data: not null, Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } } ||
            type.DeclaringType != null || type.Methods.Count != 0 || !CompleteProjection(type) ||
            type.Fields.Any(field => field.BackingData?.Field.RawFieldType is not { Data: not null }) ||
            !Enum32StorageProof.IsUnchanged(type) ||
            !ReferenceEquals(type.EnumUnderlyingType, type.AppContext.SystemTypes.SystemInt32Type))
            return false;
        foreach (var field in type.Fields)
        {
            var raw = field.BackingData!.Field.RawFieldType!;
            if (!ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(field.BackingData.Field.DeclaringType, type.Definition) ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                (FieldAttributes)raw.Attrs != field.Attributes || field.Offset != field.DefaultOffset ||
                field.OverrideFieldType != null || field.UseOverrideConstantValue ||
                raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0)
                return false;
            if (field.IsStatic &&
                ((field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasDefault)) !=
                    (FieldAttributes.Literal | FieldAttributes.HasDefault) ||
                 !ReferenceEquals(field.FieldType, type) || field.DefaultConstantValue is not int))
                return false;
        }
        return true;
    }

    internal static bool HasUnusedEnumResult(MethodAnalysisContext caller, MethodAnalysisContext target,
        Instruction call, bool projected)
    {
        if (!caller.IsVoid || caller.Parameters.Count != 0 || call.IntegerBitWidth != 0 ||
            !IsSignedEnum32(target.ReturnType) ||
            call.Operands[0] is not MethodAnalysisContext bound || !ReferenceEquals(bound, target))
            return false;
        if (call.OpCode == OpCode.CallVoid)
            return projected;
        if (projected || call.OpCode != OpCode.Call || call.Operands.Count < 3 ||
            call.Operands[1] is not LocalVariable result || !ReferenceEquals(result.Type, target.ReturnType) ||
            caller.ParameterLocals.Contains(result) || result.IsThis || result.IsMethodInfo)
            return false;
        var operations = caller.ControlFlowGraph!.Instructions.ToArray();
        return operations.Count(operation => ReferenceEquals(operation.Destination, result)) == 1 &&
               operations.SelectMany(OperandEffects.ReadLocals).All(local => !ReferenceEquals(local, result));
    }

    private static void CaptureSignedEnum32(TypeAnalysisContext type, List<object> facts)
    {
        if (!IsSignedEnum32(type)) throw new InvalidOperationException("Incomplete signed enum declaration");
        X64SmallAggregateFieldGetterProof.CaptureType(type, facts);
        X64SmallAggregateFieldGetterProof.CaptureRawType(type.Definition!.RawBaseType!, facts);
        facts.Add(type.EnumUnderlyingType!);
        X64SmallAggregateFieldGetterProof.CaptureRawType(type.Definition!.EnumUnderlyingType!, facts);
        foreach (var field in type.Fields.Where(field => field.IsStatic))
        {
            var value = field.BackingData!.Field.DefaultValue!;
            facts.Add(value.fieldIndex);
            facts.Add(value.typeIndex);
            facts.Add(value.dataIndex);
            facts.Add(field.ConstantValue!);
        }
    }
}
