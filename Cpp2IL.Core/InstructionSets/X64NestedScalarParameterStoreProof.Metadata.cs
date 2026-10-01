using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64NestedScalarParameterStoreProof
{
    private sealed record BoundFields(FieldAnalysisContext Source, FieldAnalysisContext Value,
        TypeAnalysisContext[] Closure, byte[][] Layouts);

    private static BoundFields? BindMetadata(MethodAnalysisContext method, Shape shape)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { } owner ||
            !X64OriginalReferenceClassProof.TryGetNestedOrdinaryClosure(owner, out var ownerClosure) ||
            method.Definition is not { genericContainerIndex: { IsNull: true }, parameterCount: 1 } definition ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !X64OriginalReferenceClassProof.ValidTypeIndex(app, definition.returnTypeIdx.Value) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0,
                Byref: 0, Pinned: 0, ValueType: 1 } rawReturn ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, rawReturn) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) || method.BaseMethod != null || method.Overrides.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            definition.InternalParameterData is not [var originalParameter] ||
            !X64OriginalReferenceClassProof.OriginalParameter(app, definition, 0, originalParameter) ||
            !X64OriginalReferenceClassProof.ValidTypeIndex(app, originalParameter.typeIndex.Value) ||
            originalParameter.RawType is not { } rawParameter || Scalar(app, rawParameter, shape) is not { } scalar ||
            method.Parameters is not [var parameter] || !ReferenceEquals(parameter.Definition, originalParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
            !ReferenceEquals(parameter.ParameterType, scalar) || !ReferenceEquals(parameter.DefaultParameterType, scalar) ||
            new X64CallingConventionResolver().ResolveForManaged(method) is not
                [Register { Name: "rcx" }, Register { Name: var valueRegister }, Register { Name: "r8" }] ||
            valueRegister != (shape.Floating ? "xmm1" : "rdx"))
            return null;

        var sources = owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.SourceOffset).ToArray();
        if (sources is not [var source] || !OrdinaryField(source, false) ||
            X64OriginalReferenceClassProof.ResolveClass(app, source.BackingData!.Field.RawFieldType) is not { } target ||
            !ReferenceEquals(source.FieldType, target) ||
            !X64OriginalReferenceClassProof.TryGetNestedOrdinaryClosure(target, out var targetClosure) ||
            !X64ReferenceArrayScalarResetProof.AccessibleElement(owner, target) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(source,
                new LocalVariable("store-owner", new Register(null, "store-owner"), owner), shape.SourceOffset)))
            return null;
        var values = target.Fields.Where(field => !field.IsStatic && field.Offset == shape.ValueOffset).ToArray();
        if (values is not [var value] || !OrdinaryField(value, true) ||
            !ReferenceEquals(value.DeclaringType, target) ||
            !ReferenceEquals(value.FieldType, scalar) ||
            Scalar(app, value.BackingData!.Field.RawFieldType!, shape) is not { } fieldScalar ||
            !ReferenceEquals(fieldScalar, scalar) ||
            !ReferenceEquals(owner, target) && value.Visibility != FieldAttributes.Public)
            return null;
        var reference = new FieldReference(value,
            new LocalVariable("store-target", new Register(null, "store-target"), target), shape.ValueOffset);
        if (!(shape.Floating ? NarrowFieldEqualityProof.HasUnchangedSingleFieldLayout(reference) :
                NarrowFieldEqualityProof.HasUnchangedFieldLayout(reference, shape.Width)))
            return null;
        var closure = ownerClosure.Concat(targetClosure).Distinct().ToArray();
        var layouts = new List<byte[]>();
        foreach (var type in closure)
        {
            if (!X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(type, out var layout)) return null;
            layouts.Add(layout);
        }
        return new BoundFields(source, value, closure, layouts.ToArray());
    }

    private static TypeAnalysisContext? Scalar(ApplicationAnalysisContext app, Il2CppType raw, Shape shape)
    {
        if (!X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0 || raw.ValueType != 1)
            return null;
        return (shape.Width, shape.Floating, raw.Type) switch
        {
            (8, false, Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) => app.SystemTypes.SystemBooleanType,
            (32, false, Il2CppTypeEnum.IL2CPP_TYPE_I4) => app.SystemTypes.SystemInt32Type,
            (32, false, Il2CppTypeEnum.IL2CPP_TYPE_U4) => app.SystemTypes.SystemUInt32Type,
            (32, true, Il2CppTypeEnum.IL2CPP_TYPE_R4) => app.SystemTypes.SystemSingleType,
            _ => null
        };
    }

    private static bool OrdinaryField(FieldAnalysisContext field, bool writable) =>
        field.BackingData?.Field is { RawFieldType: { } raw } && !field.IsStatic &&
        field.BackingData.Attributes == (FieldAttributes)raw.Attrs &&
        field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes && field.Offset == field.DefaultOffset &&
        field.OverrideFieldType == null && !field.UseOverrideConstantValue && field.StaticArrayInitialValue.Length == 0 &&
        raw.NumMods == 0 && raw.Byref == 0 && raw.Pinned == 0 &&
        (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA | FieldAttributes.HasFieldMarshal)) == 0 &&
        (!writable || (field.Attributes & FieldAttributes.InitOnly) == 0);

    private static void CaptureBinding(MethodAnalysisContext method, BoundFields fields, List<object> values)
    {
        X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
        values.AddRange([fields.Source, fields.Value, fields.Closure.Length]);
        for (var index = 0; index < fields.Closure.Length; index++)
        {
            var type = fields.Closure[index];
            foreach (var item in fields.Layouts[index]) values.Add(item);
            X64SmallAggregateFieldGetterProof.CaptureType(type, values);
            var definition = type.Definition!;
            values.AddRange([definition.FirstPropertyId, definition.PropertyCount, definition.FirstEventId,
                definition.EventCount, definition.NestedTypesStart, definition.NestedTypeCount, type.NestedTypes.Count]);
            foreach (var nested in type.NestedTypes) values.Add(nested);
            if (definition.RawBaseType is { } parent) X64SmallAggregateFieldGetterProof.CaptureRawType(parent, values);
            foreach (var member in type.Methods)
            {
                X64SmallAggregateFieldGetterProof.CaptureMethod(member, values);
                X64SmallAggregateFieldGetterProof.CaptureRawType(member.Definition!.RawReturnType!, values);
                foreach (var parameter in member.Parameters)
                {
                    values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterIndex,
                        parameter.Definition!, parameter.Definition!.nameIndex, parameter.Definition.token,
                        parameter.Definition.typeIndex, parameter.OverrideParameterType ?? (object)DBNull.Value,
                        parameter.UseOverrideDefaultValue]);
                    X64SmallAggregateFieldGetterProof.CaptureRawType(parameter.Definition.RawType!, values);
                }
            }
            foreach (var property in type.Properties)
                values.AddRange([property, property.Definition!, property.Name, property.Attributes,
                    (object?)property.Getter ?? DBNull.Value, (object?)property.Setter ?? DBNull.Value]);
            foreach (var member in type.Events)
                values.AddRange([member, member.Definition!, member.Name, member.Attributes,
                    (object?)member.Adder ?? DBNull.Value, (object?)member.Remover ?? DBNull.Value,
                    (object?)member.Invoker ?? DBNull.Value]);
        }
    }
}
