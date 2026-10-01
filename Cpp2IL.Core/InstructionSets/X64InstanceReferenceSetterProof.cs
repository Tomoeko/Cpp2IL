using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a frame-free instance property setter that stores its sole reference
/// argument and tail-transfers to the installed GC card marker. The native body
/// may be folded with other setters; each application method is bound to its
/// own unchanged property and field metadata before emitting managed IL.
/// </summary>
internal static class X64InstanceReferenceSetterProof
{
    internal sealed record Evidence(FieldAnalysisContext Field);
    internal sealed record Shape(int FieldOffset, ulong BarrierTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
                !PossibleSetter(method))
                return null;
            method.EnsureRawBytes();
            return Find(method, X86Utils.Iterate(method).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            return FindCore(method, decoded);
        }
        catch (Exception exception) when (exception is ArgumentException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static Evidence? FindCore(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !PossibleSetter(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;

        method.EnsureRawBytes();
        if (method.RawBytes.Length < 12 || decoded.Count < 3 ||
            !decoded.SequenceEqual(X86Utils.Iterate(method)) ||
            !TrySelectReachableLeaf(method, decoded, pe, unwind, out var leaf) ||
            TryProveShape(leaf) is not { } shape ||
            !FileBackedExecutablePrefix(method, pe, unwind, 12) ||
            X86CallerExceptionRegionProof.Check(method, leaf, new HashSet<ulong>()) != null ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.BarrierTarget) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
            bindings.Distinct().Count() != bindings.Count ||
            bindings.Count > 64 ||
            Enumerable.Range(1, 11).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return null;

        var evidence = BindMetadata(method, shape.FieldOffset);
        if (evidence == null)
            return null;

        // Reference-assembly methods can fold to the same machine code. They do
        // not supply this application's field identity. Require every alias in
        // the selected assembly to bind independently to this exact store.
        return bindings.Where(candidate => ReferenceEquals(
                candidate.DeclaringType?.DeclaringAssembly,
                method.DeclaringType?.DeclaringAssembly))
            .All(candidate => candidate.UnderlyingPointer == method.UnderlyingPointer &&
                BindMetadata(candidate, shape.FieldOffset) != null)
            ? evidence : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 3 || body[0].Length != 4 || body[1].Length != 3 ||
            body[2].Length != 5)
            return null;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }
        if (body[0].Code != Code.Add_rm64_imm8 ||
            body[0].Op0Kind != OpKind.Register || body[0].Op0Register != Register.RCX ||
            body[0].Op1Kind != OpKind.Immediate8to64 ||
            body[0].GetImmediate(1) is < 16 or > 127 ||
            body[1].Code != Code.Mov_rm64_r64 ||
            body[1].Op0Kind != OpKind.Memory || body[1].MemoryBase != Register.RCX ||
            body[1].MemoryIndex != Register.None || body[1].MemoryDisplacement64 != 0 ||
            body[1].MemorySize.GetSize() != 8 ||
            body[1].Op1Kind != OpKind.Register || body[1].Op1Register != Register.RDX ||
            body[2].Code != Code.Jmp_rel32_64 ||
            body[2].Op0Kind != OpKind.NearBranch64 || body[2].NearBranchTarget == 0)
            return null;
        return new Shape((int)body[0].GetImmediate(1), body[2].NearBranchTarget);
    }

    private static bool TrySelectReachableLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded, PE pe, X64UnwindProof.Index unwind,
        out IReadOnlyList<NativeInstruction> leaf)
    {
        leaf = Array.Empty<NativeInstruction>();
        var start = method.UnderlyingPointer;
        if (decoded[0].IP != start || decoded[2].NextIP - start != 12 ||
            unwind.ClassifySpan(start, decoded[2].NextIP).Kind !=
                X64UnwindProof.SpanKind.NoEntry)
            return false;

        if (method.RawBytes.Length == 12 && decoded.Count == 3)
        {
            leaf = decoded;
            return true;
        }

        // A method-pointer size estimate can include the next, unrelated
        // function. Trap padding and that function's own unwind entry bound
        // the reachable tail-jump leaf without treating later code as ours.
        var index = 3;
        var paddingEnd = decoded[2].NextIP;
        while (index < decoded.Count && decoded[index].Code == Code.Int3)
        {
            if (decoded[index].IP != paddingEnd || decoded[index].Length != 1 ||
                paddingEnd - decoded[2].NextIP >= 15)
                return false;
            paddingEnd = decoded[index].NextIP;
            index++;
        }
        if (index == 3 || index == decoded.Count || decoded[index].IP != paddingEnd ||
            decoded[index].IsInvalid || decoded[index].NextIP <= paddingEnd ||
            paddingEnd - start >= (ulong)method.RawBytes.Length ||
            !FileBackedExecutablePrefix(method, pe, unwind,
                checked((int)(paddingEnd - start))) ||
            !X64NativePaddingProof.HasInt3Padding(pe, decoded[2].NextIP, paddingEnd) ||
            unwind.ClassifySpan(start, paddingEnd).Kind != X64UnwindProof.SpanKind.NoEntry ||
            unwind.ClassifySpan(paddingEnd, decoded[index].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree,
                    Start: var nextStart, RootStart: var nextRoot } ||
            nextStart != paddingEnd || nextRoot != paddingEnd ||
            Enumerable.Range(1, checked((int)(paddingEnd - start - 1))).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return false;

        leaf = [decoded[0], decoded[1], decoded[2]];
        return true;
    }

    private static Evidence? BindMetadata(MethodAnalysisContext method, int offset)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainerIndex: { IsNull: true },
                HasCctor: false, PackingSizeIsDefault: true,
                ClassSizeIsDefault: true } } owner ||
            !OriginalClass(owner) ||
            !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
            !OriginalSignatureIndices(method) ||
            method.Definition is not { genericContainerIndex: { IsNull: true }, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } rawReturn } definition ||
            !OriginalVoid(rawReturn) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [{ RawType: { } rawType } rawValue] ||
            ResolveReferenceType(app, rawType) is not { } valueType ||
            rawType.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
                (!OriginalClass(valueType) || !AccessibleValueType(owner, valueType, method.Visibility)) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.GenericParameters.Count != 0 || method.Parameters is not [var value] ||
            method.Name != method.DefaultName ||
            !method.Name.StartsWith("set_", StringComparison.Ordinal) ||
            (method.Attributes & MethodAttributes.SpecialName) == 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall |
                                      MethodImplAttributes.Synchronized)) != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !ReferenceEquals(value.Definition, rawValue) ||
            !ReferenceEquals(value.DeclaringMethod, method) ||
            value.ParameterIndex != 0 || value.IsRef ||
            value.Name != value.DefaultName || value.UseOverrideDefaultValue ||
            value.Attributes != value.DefaultAttributes ||
            value.OverrideParameterType != null ||
            !ReferenceEquals(value.ParameterType, valueType))
            return null;

        if (offset < 2 * app.Binary.PointerSizeBytes ||
            offset + app.Binary.PointerSizeBytes > owner.Definition.RawSizes.instance_size ||
            owner.Methods.Count != owner.Definition.MethodCount ||
            !owner.Methods.Select(member => member.Definition).SequenceEqual(owner.Definition.Methods!) ||
            owner.Properties.Count != owner.Definition.PropertyCount ||
            !owner.Properties.Select(member => member.Definition).SequenceEqual(owner.Definition.Properties!) ||
            owner.Fields.Count != owner.Definition.FieldCount ||
            !owner.Fields.Select(field => field.BackingData?.Field).SequenceEqual(owner.Definition.Fields!) ||
            owner.Fields.Any(field => field.BackingData?.Field.RawFieldType is not { Data: not null }))
            return null;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Setter, method)).ToArray();
        // A matching getter is recovered separately; its presence does not
        // change this setter's native store or field binding.
        if (properties is not [{ } property] ||
            property.Definition is not { } rawProperty ||
            !ReferenceEquals(property.DeclaringType, owner) ||
            !ReferenceEquals(rawProperty.DeclaringType, owner.Definition) ||
            rawProperty.set.Value < 0 || rawProperty.set.Value >= owner.Definition.MethodCount ||
            !rawProperty.get.IsNull && (rawProperty.get.Value < 0 || rawProperty.get.Value >= owner.Definition.MethodCount) ||
            !ReferenceEquals(rawProperty.Setter, definition) ||
            !ReferenceEquals(property.Getter?.Definition, rawProperty.Getter) ||
            property.Getter is { } getter && !OriginalGetter(getter, property.Name, valueType) ||
            property.Name != property.DefaultName ||
            method.Name != "set_" + property.Name ||
            property.Attributes != property.DefaultAttributes ||
            property.OverridePropertyType != null ||
            property.IsStatic || !ReferenceEquals(property.PropertyType, valueType) ||
            ResolveReferenceType(app, rawProperty.RawPropertyType) is not { } propertyType ||
            !ReferenceEquals(propertyType, valueType))
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == offset &&
            ReferenceEquals(ResolveReferenceType(app, field.BackingData?.Field.RawFieldType), valueType) &&
            ReferenceEquals(field.FieldType, valueType)).ToArray();
        if (fields is not [{ } stored] ||
            !ReferenceEquals(stored.DeclaringType, owner) ||
            stored.Name != stored.DefaultName ||
            stored.Attributes != stored.DefaultAttributes ||
            stored.OverrideFieldType != null || stored.Offset != stored.DefaultOffset ||
            stored.UseOverrideConstantValue ||
            stored.StaticArrayInitialValue.Length != 0 ||
            (stored.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal |
                                  FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal |
                                  FieldAttributes.HasFieldRVA)) != 0 ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new ISIL.FieldReference(stored,
                    new ISIL.LocalVariable("proved-owner", new ISIL.Register(null, "rcx"),
                        owner), offset)))
            return null;
        return new Evidence(stored);
    }

    // Validate the union and decoded flags before a context's lazy type resolver
    // can dereference them. CLASS, OBJECT and STRING share reference storage, but
    // CLASS additionally names an original, canonical metadata definition.
    internal static bool OriginalDescriptor(Il2CppType raw) =>
        raw.Data != null && raw.Datapoint == raw.Data.Dummy &&
        raw.NumMods == 0 && raw.Byref == 0 && raw.Pinned == 0 && raw.ValueType == 0 &&
        raw.Attrs <= ushort.MaxValue && raw.Bits == (raw.Attrs | ((uint)raw.Type << 16));

    // In this metadata profile VOID carries the value-type flag. It is not a
    // reference descriptor, even though it has no managed value or storage.
    private static bool OriginalVoid(Il2CppType raw) =>
        raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VOID && raw.Data != null &&
        raw.Datapoint == raw.Data.Dummy && raw.Attrs == 0 &&
        raw.NumMods == 0 && raw.Byref == 0 && raw.Pinned == 0 && raw.ValueType == 1 &&
        raw.Bits == ((uint)raw.Type << 16 | 1U << 31);

    private static TypeAnalysisContext? ResolveReferenceType(ApplicationAnalysisContext app, Il2CppType? raw)
    {
        if (raw == null || !OriginalDescriptor(raw)) return null;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)
            return ResolveBuiltinReference(app.SystemTypes.SystemObjectType, raw);
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_STRING)
            return ResolveBuiltinReference(app.SystemTypes.SystemStringType, raw);
        if (raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS || raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount)
            return null;
        var definition = app.Metadata.typeDefs[(int)raw.Data.Dummy];
        if (definition.DeclaringAssembly is not { } image ||
            app.ResolveContextForAssembly(image) is not { } assembly ||
            !ReferenceEquals(assembly.Definition?.Image, image) ||
            assembly.GetTypeByDefinition(definition) is not { } type ||
            !ReferenceEquals(type.AppContext, app) || !ReferenceEquals(type.Definition, definition) ||
            !ReferenceEquals(type.DeclaringAssembly, assembly) ||
            assembly.Types.Count(candidate => ReferenceEquals(candidate, type)) != 1)
            return null;
        return type;
    }

    private static TypeAnalysisContext? ResolveBuiltinReference(TypeAnalysisContext type, Il2CppType raw)
    {
        if (type.Definition is not { } definition ||
            definition.ByvalTypeIndex.Value < 0 ||
            definition.ByvalTypeIndex.Value >= type.AppContext.Binary.AllTypes.Length ||
            definition.RawType is not { } original || !OriginalDescriptor(original) ||
            original.Type != raw.Type || original.Data.Dummy != raw.Data.Dummy ||
            definition.TypeIndex.Value < 0 || original.Data.Dummy != (ulong)definition.TypeIndex.Value)
            return null;
        return type;
    }

    private static bool OriginalClass(TypeAnalysisContext type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null;)
        {
            if (!visited.Add(current) || visited.Count > 32 ||
                current.Definition is not { GenericContainerIndex: { IsNull: true }, PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true, DeclaringTypeIndex: { IsNull: true } } definition ||
                definition.ByvalTypeIndex.Value < 0 || definition.ByvalTypeIndex.Value >= type.AppContext.Binary.AllTypes.Length ||
                !definition.ParentIndex.IsNull && (definition.ParentIndex.Value < 0 ||
                    definition.ParentIndex.Value >= type.AppContext.Binary.AllTypes.Length) ||
                ResolveReferenceType(type.AppContext, definition.RawType) is not { } canonical ||
                !ReferenceEquals(canonical, current) || current.DeclaringType != null ||
                current.IsValueType || current.IsInterface || current.IsGenericInstance ||
                current.GenericParameters.Count != 0 || current.Name != current.DefaultName ||
                current.Namespace != current.DefaultNamespace || current.Attributes != current.DefaultAttributes ||
                current.OverrideBaseType != null ||
                (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return false;
            if (ReferenceEquals(current, type.AppContext.SystemTypes.SystemObjectType))
                return definition.ParentIndex.IsNull && definition.RawBaseType == null;
            if (ResolveReferenceType(type.AppContext, definition.RawBaseType) is not { } parent ||
                ReferenceEquals(parent, type.AppContext.SystemTypes.SystemStringType) ||
                !ReferenceEquals(current.BaseType, parent))
                return false;
            current = parent;
        }
        return false;
    }

    private static bool AccessibleValueType(TypeAnalysisContext owner, TypeAnalysisContext value, MethodAttributes visibility) =>
        X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(owner.DeclaringAssembly, value.DeclaringAssembly) &&
        (value.Visibility == TypeAttributes.Public || value.Visibility == TypeAttributes.NotPublic &&
            ReferenceEquals(owner.DeclaringAssembly, value.DeclaringAssembly)) &&
        !(owner.Visibility == TypeAttributes.Public && visibility == MethodAttributes.Public && value.Visibility != TypeAttributes.Public);

    private static bool OriginalGetter(MethodAnalysisContext getter, string propertyName, TypeAnalysisContext valueType) =>
        OriginalSignatureIndices(getter) &&
        getter.Definition is { genericContainerIndex: { IsNull: true }, parameterCount: 0, InternalParameterData: [] } definition &&
        ReferenceEquals(ResolveReferenceType(getter.AppContext, definition.RawReturnType), valueType) &&
        getter.Name == "get_" + propertyName && getter.Name == getter.DefaultName &&
        getter.Attributes == getter.DefaultAttributes && getter.ImplAttributes == getter.DefaultImplAttributes &&
        (getter.Attributes & MethodAttributes.SpecialName) != 0 && !getter.IsStatic && !getter.IsVirtual &&
        getter.Parameters.Count == 0 && getter.GenericParameters.Count == 0 && getter.OverrideReturnType == null &&
        ReferenceEquals(getter.ReturnType, valueType);

    private static bool PossibleSetter(MethodAnalysisContext method) =>
        method.Name.StartsWith("set_", StringComparison.Ordinal) &&
        !method.IsStatic && !method.IsVirtual && method.Parameters.Count == 1 &&
        (method.Attributes & MethodAttributes.SpecialName) != 0 &&
        OriginalSignatureIndices(method) &&
        method.Definition is { genericContainerIndex: { IsNull: true }, parameterCount: 1,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        definition.InternalParameterData is [{ RawType:
            { Data: not null, NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                    Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_CLASS } }] &&
        method.UnderlyingPointer is not (0 or ulong.MaxValue);

    private static bool OriginalSignatureIndices(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        var types = app.Binary.AllTypes;
        if (method.Definition is not { } definition ||
            definition.returnTypeIdx.Value < 0 || definition.returnTypeIdx.Value >= types.Length ||
            definition.declaringTypeIdx.Value < 0 ||
            definition.declaringTypeIdx.Value >= app.Metadata.TypeDefinitionCount ||
            definition.parameterCount != 0 && definition.parameterStart.IsNull)
            return false;
        // The metadata API bounds the parameter table. Resolve each raw index
        // before accessing lazy parameter contexts; an invalid table span is
        // declined by Find's narrow bounds-exception guard.
        return definition.InternalParameterData is { } parameters &&
            parameters.Length == definition.parameterCount && parameters.All(parameter =>
                parameter.typeIndex.Value >= 0 && parameter.typeIndex.Value < types.Length);
    }

    private static bool FileBackedExecutablePrefix(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, int length)
    {
        var start = method.UnderlyingPointer;
        if (length < 12 || method.RawBytes.Length < length ||
            start < unwind.ImageBase || start - unwind.ImageBase > uint.MaxValue - (uint)length + 1)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(start + (ulong)length - 1, false);
        var raw = pe.GetRawBinaryContent();
        return first >= 0 && last == first + length - 1 &&
               first <= raw.Length - length &&
               method.RawBytes.AsSpan().Slice(0, length)
                   .SequenceEqual(raw.Slice((int)first, length)) &&
               Enumerable.Range(0, length).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                       unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                       first + offset);
    }
}
