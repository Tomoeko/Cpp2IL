using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ArrayElementBooleanStoreProof
{
    internal sealed record OwnerEffectShape(ulong ArrayOffset, ulong OwnerOffset,
        bool OwnerValue, ulong ElementOffset, bool ElementValue,
        bool CapturesArrayBeforeOwnerEffect);

    private static Evidence? FindOwnerEffect(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            // Authenticate raw indices and descriptors before lazy managed type
            // resolution can truncate a CLASS union or dereference a changed array.
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                !OriginalOwnerEffectSignature(method) ||
                method.DeclaringType is not { Definition: { HasCctor: false } } owner ||
                !OriginalEffectClass(owner) || !NullCheckedCall.IsReferenceClass(owner) ||
                method.IsStatic || method.IsVirtual || !method.IsVoid ||
                method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
                method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
                !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl |
                    MethodAttributes.SpecialName)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                    MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
                owner.Properties.Any(property => ReferenceEquals(property.Getter, method) ||
                    ReferenceEquals(property.Setter, method)) ||
                method.Parameters is not [var parameter] || parameter.ParameterIndex != 0 ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
                parameter.Name != parameter.DefaultName || parameter.UseOverrideDefaultValue ||
                parameter.OverrideParameterType != null || parameter.Attributes != parameter.DefaultAttributes ||
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                X64Stack28BodyProof.Read(method, 17, 128) is not { } body ||
                TryProveOwnerEffectShape(body, pe.PointerSizeBytes,
                    Il2CppArrayUtils.GetLengthOffset(pe), Il2CppArrayUtils.GetFirstItemOffset(pe)) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[14].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, body[16].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[14].IP, body[16].IP }) != null)
                return null;

            var array = UniqueEffectField(owner, shape.ArrayOffset);
            var prior = UniqueEffectField(owner, shape.OwnerOffset);
            if (array?.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY } rawArray ||
                !X64InstanceReferenceSetterProof.OriginalDescriptor(rawArray) ||
                rawArray.GetEncapsulatedType() is not { } rawElement ||
                ResolveEffectClass(app, rawElement) is not { Definition: { HasCctor: false } } element ||
                !OriginalEffectClass(element) || !NullCheckedCall.IsReferenceClass(element) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(owner.DeclaringAssembly, element.DeclaringAssembly) ||
                array.FieldType is not SzArrayTypeAnalysisContext { ElementType: var resolvedElement } ||
                !ReferenceEquals(resolvedElement, element) ||
                !OriginalBooleanEffectField(prior, app) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(EffectReference(array)) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(EffectReference(prior!), 8))
                return null;

            var stored = UniqueEffectField(element, shape.ElementOffset);
            if (!OriginalBooleanEffectField(stored, app) ||
                !ReferenceEquals(element, owner) &&
                (element.Visibility != TypeAttributes.Public || stored!.Visibility != FieldAttributes.Public) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(EffectReference(stored!), 8))
                return null;
            return new Evidence(array, stored!, shape.ElementValue, prior,
                shape.OwnerValue, shape.CapturesArrayBeforeOwnerEffect);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static OwnerEffectShape? TryProveOwnerEffectShape(
        IReadOnlyList<NativeInstruction> body, int pointerSize, uint lengthOffset, uint firstItemOffset)
    {
        if (body.Count != 17 || pointerSize != 8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub))
            return null;
        for (var index = 0; index < body.Count; index++)
            if (body[index].IsInvalid || body[index].CodeSize != CodeSize.Code64 ||
                body[index].HasLockPrefix || body[index].HasRepPrefix || body[index].HasRepnePrefix ||
                body[index].SegmentPrefix != NativeRegister.None ||
                index > 0 && body[index].IP != body[index - 1].NextIP)
                return null;

        var captureFirst = body[1].Code == Code.Mov_r64_rm64;
        var load = body[captureFirst ? 1 : 2];
        var effect = body[captureFirst ? 2 : 1];
        if (!ArrayLoad(load, out var arrayOffset) ||
            !BooleanStore(effect, out var ownerOffset, out var ownerValue) ||
            !Registers(body[3], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[4], Code.Je_rel8_64, body[14].IP) ||
            body[5].Code != Code.Cmp_r32_rm32 || body[5].Op0Kind != OpKind.Register ||
            body[5].Op0Register != NativeRegister.EDX ||
            !Memory(body[5], 1, NativeRegister.R8, NativeRegister.None, 1, lengthOffset, 4) ||
            !Branch(body[6], Code.Jae_rel8_64, body[16].IP) ||
            !Registers(body[7], Code.Movsxd_r64_rm32, NativeRegister.RAX, NativeRegister.EDX) ||
            body[8].Code != Code.Mov_r64_rm64 || body[8].Op0Kind != OpKind.Register ||
            body[8].Op0Register != NativeRegister.RCX ||
            !Memory(body[8], 1, NativeRegister.R8, NativeRegister.RAX, pointerSize, firstItemOffset, pointerSize) ||
            !Registers(body[9], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) ||
            !Branch(body[10], Code.Je_rel8_64, body[14].IP) ||
            !BooleanStore(body[11], out var elementOffset, out var elementValue) ||
            !X64Stack28BodyProof.Stack(body[12], Mnemonic.Add) ||
            body[13].Code != Code.Retnq || body[13].OpCount != 0 ||
            body[14].Code != Code.Call_rel32_64 || body[14].Op0Kind != OpKind.NearBranch64 ||
            body[14].NearBranchTarget == 0 || body[15].Code != Code.Int3 || body[15].OpCount != 0 ||
            body[16].Code != Code.Call_rel32_64 || body[16].Op0Kind != OpKind.NearBranch64 ||
            body[16].NearBranchTarget == 0)
            return null;
        return new OwnerEffectShape(arrayOffset, ownerOffset, ownerValue,
            elementOffset, elementValue, captureFirst);
    }

    private static bool OriginalOwnerEffectSignature(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.Definition is not { genericContainerIndex: { IsNull: true }, parameterCount: 1 } definition ||
            definition.declaringTypeIdx.Value < 0 || definition.declaringTypeIdx.Value >= app.Metadata.TypeDefinitionCount ||
            definition.returnTypeIdx.Value < 0 || definition.returnTypeIdx.Value >= app.Binary.AllTypes.Length ||
            definition.parameterStart.IsNull || definition.InternalParameterData is not [var parameter] ||
            parameter.typeIndex.Value < 0 || parameter.typeIndex.Value >= app.Binary.AllTypes.Length)
            return false;
        return ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) &&
            OriginalEffectScalar(definition.RawReturnType, app.SystemTypes.SystemVoidType, Il2CppTypeEnum.IL2CPP_TYPE_VOID, 0) &&
            OriginalEffectScalar(parameter.RawType, app.SystemTypes.SystemInt32Type, Il2CppTypeEnum.IL2CPP_TYPE_I4, parameter.RawType?.Attrs ?? 0);
    }

    private static bool OriginalEffectScalar(Il2CppType? raw, TypeAnalysisContext canonical,
        Il2CppTypeEnum kind, uint attributes) =>
        raw is { Data: not null, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } &&
        raw.Type == kind && raw.Datapoint == raw.Data.Dummy && raw.Attrs == attributes &&
        attributes <= ushort.MaxValue && raw.Bits == (attributes | (uint)kind << 16 | 1U << 31) &&
        canonical.Definition is { } definition && definition.TypeIndex.Value >= 0 &&
        raw.Data.Dummy == (ulong)definition.TypeIndex.Value;

    private static TypeAnalysisContext? ResolveEffectClass(ApplicationAnalysisContext app, Il2CppType? raw)
    {
        if (raw == null || !X64InstanceReferenceSetterProof.OriginalDescriptor(raw) ||
            raw.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT) ||
            raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount)
            return null;
        var definition = app.Metadata.typeDefs[(int)raw.Data.Dummy];
        if (definition.ByvalTypeIndex.Value < 0 || definition.ByvalTypeIndex.Value >= app.Binary.AllTypes.Length ||
            definition.RawType is not { } original || !X64InstanceReferenceSetterProof.OriginalDescriptor(original) ||
            original.Type != raw.Type || original.Data.Dummy != raw.Data.Dummy ||
            definition.DeclaringAssembly is not { } image ||
            app.ResolveContextForAssembly(image) is not { } assembly ||
            !ReferenceEquals(assembly.Definition?.Image, image) ||
            assembly.GetTypeByDefinition(definition) is not { } type ||
            !ReferenceEquals(type.AppContext, app) || !ReferenceEquals(type.DeclaringAssembly, assembly) ||
            !ReferenceEquals(type.Definition, definition) || assembly.Types.Count(candidate => ReferenceEquals(candidate, type)) != 1 ||
            raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT && !ReferenceEquals(type, app.SystemTypes.SystemObjectType))
            return null;
        return type;
    }

    private static bool OriginalEffectClass(TypeAnalysisContext type)
    {
        var app = type.AppContext;
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null;)
        {
            if (!visited.Add(current) || visited.Count > 32 ||
                current.Definition is not { GenericContainerIndex: { IsNull: true }, DeclaringTypeIndex: { IsNull: true },
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true } definition ||
                definition.ByvalTypeIndex.Value < 0 || definition.ByvalTypeIndex.Value >= app.Binary.AllTypes.Length ||
                ResolveEffectClass(app, definition.RawType) is not { } canonical || !ReferenceEquals(canonical, current) ||
                current.DeclaringType != null || current.IsValueType || current.IsInterface || current.IsGenericInstance ||
                current.GenericParameters.Count != 0 || current.Name != current.DefaultName ||
                current.Namespace != current.DefaultNamespace || current.Attributes != current.DefaultAttributes ||
                current.OverrideBaseType != null ||
                (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                current.Methods.Count != definition.MethodCount ||
                !current.Methods.Select(member => member.Definition).SequenceEqual(definition.Methods!) ||
                current.Methods.Any(member => member.Name != member.DefaultName ||
                    member.Attributes != member.DefaultAttributes || member.ImplAttributes != member.DefaultImplAttributes ||
                    !ReferenceEquals(member.DeclaringType, current) ||
                    !ReferenceEquals(member.Definition?.DeclaringType, definition) ||
                    !definition.HasCctor && member.Name == ".cctor" ||
                    member.Name == ".ctor" && !OriginalEffectConstructor(member)) ||
                current.Fields.Count != definition.FieldCount ||
                !current.Fields.Select(field => field.BackingData?.Field).SequenceEqual(definition.Fields!) ||
                current.Fields.Any(field => !OriginalEffectFieldDescriptor(app, field.BackingData?.Field.RawFieldType)))
                return false;
            if (ReferenceEquals(current, app.SystemTypes.SystemObjectType))
                return definition.ParentIndex.IsNull && definition.RawBaseType == null;
            if (definition.ParentIndex.IsNull || definition.ParentIndex.Value < 0 ||
                definition.ParentIndex.Value >= app.Binary.AllTypes.Length ||
                ResolveEffectClass(app, definition.RawBaseType) is not { } parent || !ReferenceEquals(current.BaseType, parent))
                return false;
            current = parent;
        }
        return false;
    }

    private static bool OriginalEffectFieldDescriptor(ApplicationAnalysisContext app, Il2CppType? raw) =>
        raw is { Data: not null, NumMods: 0, Byref: 0, Pinned: 0, ValueType: <= 1 } &&
        raw.Datapoint == raw.Data.Dummy && raw.Attrs <= ushort.MaxValue &&
        raw.Bits == (raw.Attrs | (uint)raw.Type << 16 | raw.ValueType << 31) &&
        (raw.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE) ||
            raw.Data.Dummy < (ulong)app.Metadata.TypeDefinitionCount);

    private static bool OriginalEffectConstructor(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.Definition is not { genericContainerIndex: { IsNull: true } } definition ||
            definition.returnTypeIdx.Value < 0 || definition.returnTypeIdx.Value >= app.Binary.AllTypes.Length ||
            !OriginalEffectScalar(definition.RawReturnType, app.SystemTypes.SystemVoidType, Il2CppTypeEnum.IL2CPP_TYPE_VOID, 0) ||
            definition.parameterCount != 0 && definition.parameterStart.IsNull ||
            definition.InternalParameterData is not { } parameters || parameters.Length != definition.parameterCount ||
            parameters.Any(parameter => parameter.typeIndex.Value < 0 || parameter.typeIndex.Value >= app.Binary.AllTypes.Length ||
                !OriginalEffectFieldDescriptor(app, parameter.RawType)))
            return false;
        return !method.IsStatic && !method.IsVirtual && method.IsVoid && method.OverrideReturnType == null &&
            method.GenericParameters.Count == 0 && method.Parameters.Count == parameters.Length &&
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0;
    }

    private static FieldAnalysisContext? UniqueEffectField(TypeAnalysisContext owner, ulong offset)
    {
        if (offset > int.MaxValue) return null;
        var fields = owner.Fields.Where(field => !field.IsStatic && field.Offset == (int)offset).ToArray();
        return fields is [{ } field] && ReferenceEquals(field.DeclaringType, owner) &&
            field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
            field.Offset == field.DefaultOffset && field.OverrideFieldType == null &&
            !field.UseOverrideConstantValue && field.OverrideStaticArrayInitialValue == null &&
            (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal | FieldAttributes.HasDefault |
                FieldAttributes.HasFieldMarshal | FieldAttributes.HasFieldRVA)) == 0 ? field : null;
    }

    private static bool OriginalBooleanEffectField(FieldAnalysisContext? field, ApplicationAnalysisContext app) =>
        field != null && OriginalEffectScalar(field.BackingData?.Field.RawFieldType,
            app.SystemTypes.SystemBooleanType, Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, (uint)field.DefaultAttributes) &&
        ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType);

    private static FieldReference EffectReference(FieldAnalysisContext field) =>
        new(field, new LocalVariable("proved-receiver", new ManagedRegister(null, "proved-receiver"), field.DeclaringType), field.Offset);
}
