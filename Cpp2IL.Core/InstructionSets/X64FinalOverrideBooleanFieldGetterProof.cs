using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete Boolean field leaf for a final class override of a
/// single inherited virtual slot. The new-slot interface case has a separate
/// proof because its original dispatch metadata has different authority.
/// </summary>
internal static class X64FinalOverrideBooleanFieldGetterProof
{
    internal const string EvidenceKey = "X64FinalOverrideBooleanFieldGetterProof";
    internal const string CaptureRegister = "final_override_boolean_field_capture";

    internal sealed class DispatchState(List<object?> values)
    {
        private readonly object?[] _values = values.ToArray();
        internal bool Matches(DispatchState other) => _values.SequenceEqual(other._values);
    }

    internal sealed record Proof(FieldAnalysisContext Field, ulong LoadIp, ulong ReturnIp,
        DispatchState Dispatch)
    {
        internal bool Matches(Proof other) => ReferenceEquals(Field, other.Field) &&
            LoadIp == other.LoadIp && ReturnIp == other.ReturnIp && Dispatch.Matches(other.Dispatch);
    }

    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var capture = new IsilRegister(null, CaptureRegister);
        return
        [
            new(0, OpCode.Move, capture,
                new ISIL.MemoryOperand(new IsilRegister(null, "rcx"), null, proof.Field.Offset))
                { IntegerBitWidth = 8, NativeAddress = proof.LoadIp },
            new(1, OpCode.Return, capture) { NativeAddress = proof.ReturnIp }
        ];
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext: { Binary: PE } app } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app))
            return null;
        try
        {
            if (TryDispatch(method) is not { } dispatch ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 5) is not { } body ||
                method.RawBytes.Length != 5 || body[0].IP != method.UnderlyingPointer ||
                X86DirectBooleanFieldGetterProof.TryProveShape(body) is not { } shape ||
                shape.End != body[1].NextIP ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                method.DeclaringType is not { } owner ||
                owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.FieldOffset)
                    .ToArray() is not [{ } field] ||
                field.Name != field.DefaultName ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, NumMods: 0, Byref: 0, Pinned: 0 } ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) ||
                (ulong)field.Offset + 1 > owner.Definition!.RawSizes.instance_size)
                return null;
            var receiver = new LocalVariable("proved-final-override-receiver",
                new IsilRegister(null, "rcx"), owner);
            var access = new FieldReference(field, receiver, field.Offset);
            return NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(access)
                ? new Proof(field, body[0].IP, body[1].IP, dispatch)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static DispatchState? TryDispatch(MethodAnalysisContext method)
    {
        if (method is not { Definition: { GenericContainer: null } definition,
                DeclaringType: { } owner } ||
            owner.BaseType is not { } parent ||
            method.BaseMethod is not { Definition: { GenericContainer: null } baseDefinition,
                DeclaringType: { } baseOwner } baseMethod ||
            !ReferenceEquals(parent, baseOwner) || !OrdinaryType(owner) || !OrdinaryType(parent) ||
            !method.IsVirtual || !method.IsFinal || method.IsNewSlot || method.IsStatic || method.IsAbstract ||
            !baseMethod.IsVirtual || baseMethod.IsFinal || !baseMethod.IsNewSlot || baseMethod.IsStatic ||
            baseMethod.BaseMethod != null || method.Overrides.Count != 0 || baseMethod.Overrides.Count != 0 ||
            method.Name != baseMethod.Name || method.Name != method.DefaultName ||
            baseMethod.Name != baseMethod.DefaultName ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            baseMethod.Attributes != baseMethod.DefaultAttributes ||
            baseMethod.ImplAttributes != baseMethod.DefaultImplAttributes ||
            method.OverrideReturnType != null || baseMethod.OverrideReturnType != null ||
            method.GenericParameters.Count != 0 || baseMethod.GenericParameters.Count != 0 ||
            method.Parameters.Count != 0 || baseMethod.Parameters.Count != 0 ||
            definition.parameterCount != 0 || baseDefinition.parameterCount != 0 ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            (baseDefinition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(baseDefinition.DeclaringType, parent.Definition) ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(baseMethod.ReturnType, method.ReturnType) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            baseDefinition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            definition.slot == ushort.MaxValue || definition.slot != baseDefinition.slot ||
            owner.Definition?.VTable is not { } ownTable || definition.slot >= ownTable.Length ||
            ownTable[definition.slot] is not { Type: MetadataUsageType.MethodDef } ownEntry ||
            !ReferenceEquals(ownEntry.AsMethod(), definition) ||
            parent.Definition?.VTable is not { } parentTable || definition.slot >= parentTable.Length ||
            !(baseMethod.IsAbstract
                ? parentTable[definition.slot] == null
                : parentTable[definition.slot] is { Type: MetadataUsageType.MethodDef } baseEntry &&
                  ReferenceEquals(baseEntry.AsMethod(), baseDefinition)) ||
            !OriginalProperty(method) || !OriginalProperty(baseMethod))
            return null;

        var values = new List<object?>();
        var visited = new HashSet<TypeAnalysisContext>();
        var reachedObject = false;
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || !OrdinaryType(type))
                return null;
            CaptureType(type, values);
            reachedObject |= ReferenceEquals(type, method.AppContext.SystemTypes.SystemObjectType);
        }
        if (!reachedObject)
            return null;
        values.Add(method.BaseMethod);
        values.Add(baseMethod.BaseMethod);
        values.Add(method.Overrides.Count);
        values.Add(baseMethod.Overrides.Count);
        return new DispatchState(values);
    }

    private static bool OriginalProperty(MethodAnalysisContext method)
    {
        var owner = method.DeclaringType!;
        return owner.Properties.Where(property => ReferenceEquals(property.Getter, method))
            .ToArray() is [{ } property] && property.Setter == null &&
            property.Definition is { } original &&
            ReferenceEquals(original.Getter, method.Definition) &&
            property.Name == property.DefaultName && method.Name == "get_" + property.Name &&
            property.Attributes == property.DefaultAttributes &&
            property.OverridePropertyType == null && !property.IsStatic &&
            ReferenceEquals(property.PropertyType, method.ReturnType) &&
            original.RawPropertyType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool OrdinaryType(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null, HasCctor: false, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { NumMods: 0, Byref: 0, Pinned: 0 } raw } definition &&
        (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
         raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
         ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType)) &&
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance &&
        type.GenericParameters.Count == 0 && type.InterfaceContexts.Count == 0 &&
        definition.InterfacesCount == 0 && definition.InterfaceOffsetsCount == 0 &&
        definition.RawInterfaces.Length == 0 && definition.InterfaceOffsets.Length == 0 &&
        type.Attributes == type.DefaultAttributes &&
        (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout &&
        type.Name == type.DefaultName && type.OverrideNamespace == null &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        !type.Methods.Any(candidate => candidate.Name == ".cctor") &&
        type.Fields.Count == definition.FieldCount &&
        type.Fields.All(field => field.BackingData?.Field.RawFieldType is
                { NumMods: 0, Byref: 0, Pinned: 0 } fieldType &&
            SimpleStoredField(fieldType.Type) && field.Name == field.DefaultName &&
            field.Attributes == field.DefaultAttributes && field.Offset == field.DefaultOffset &&
            field.OverrideFieldType == null) &&
        type.Methods.Count == definition.MethodCount &&
        type.Methods.All(candidate => candidate.Definition != null);

    private static bool SimpleStoredField(Il2CppTypeEnum type) => type is
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
        Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
        Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
        Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
        Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8 or
        Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U or
        Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
        Il2CppTypeEnum.IL2CPP_TYPE_CLASS;

    private static void CaptureType(TypeAnalysisContext type, List<object?> values)
    {
        var definition = type.Definition!;
        values.Add(type);
        values.Add(type.Name);
        values.Add(type.Namespace);
        values.Add(type.Attributes);
        values.Add(type.BaseType);
        values.Add(definition.NameIndex);
        values.Add(definition.NamespaceIndex);
        values.Add(definition.Token);
        values.Add(definition.RawType.Type);
        values.Add(definition.RawType.NumMods);
        values.Add(definition.RawType.Byref);
        values.Add(definition.RawType.Pinned);
        values.Add(definition.Bitfield);
        values.Add(definition.RawSizes.instance_size);
        values.Add(definition.ParentIndex);
        values.Add(definition.InterfacesStart);
        values.Add(definition.InterfacesCount);
        values.Add(definition.InterfaceOffsetsStart);
        values.Add(definition.InterfaceOffsetsCount);
        values.Add(definition.VtableStart);
        values.Add(definition.VtableCount);
        values.Add(type.Fields.Count);
        foreach (var field in type.Fields)
        {
            var original = field.BackingData!.Field;
            var raw = original.RawFieldType!;
            values.Add(field);
            values.Add(field.Name);
            values.Add(field.Attributes);
            values.Add(field.Offset);
            values.Add(original.nameIndex);
            values.Add(original.token);
            values.Add(original.typeIndex);
            values.Add(raw.Type);
            values.Add(raw.NumMods);
            values.Add(raw.Byref);
            values.Add(raw.Pinned);
        }
        values.Add(type.Properties.Count);
        foreach (var property in type.Properties)
        {
            values.Add(property);
            values.Add(property.Name);
            values.Add(property.Attributes);
            values.Add(property.Getter);
            values.Add(property.Setter);
            values.Add(property.Definition?.token);
            values.Add(property.Definition?.attrs);
            values.Add(property.Definition?.get);
            values.Add(property.Definition?.set);
        }
        values.Add(type.Methods.Count);
        foreach (var member in type.Methods)
            CaptureMethod(member, values);
        values.Add(definition.VTable?.Length);
        if (definition.VTable != null)
            foreach (var entry in definition.VTable)
            {
                values.Add(entry?.Type);
                values.Add(entry is { Type: MetadataUsageType.MethodDef } ? entry.AsMethod() : null);
            }
    }

    private static void CaptureMethod(MethodAnalysisContext method, List<object?> values)
    {
        values.Add(method);
        values.Add(method.Name);
        values.Add(method.Attributes);
        values.Add(method.ImplAttributes);
        values.Add(method.ReturnType.FullName);
        values.Add(method.ReturnType.Type);
        values.Add(method.BaseMethod);
        values.Add(method.Definition!.flags);
        values.Add(method.Definition.iflags);
        values.Add(method.Definition.slot);
        values.Add(method.Definition.nameIndex);
        values.Add(method.Definition.token);
        values.Add(method.Definition.parameterCount);
        values.Add(method.Definition.returnTypeIdx);
        values.Add(method.Definition.declaringTypeIdx);
        values.Add(method.Definition.RawReturnType?.Type);
        values.Add(method.Definition.RawReturnType?.NumMods);
        values.Add(method.Definition.RawReturnType?.Byref);
        values.Add(method.Definition.RawReturnType?.Pinned);
    }
}
