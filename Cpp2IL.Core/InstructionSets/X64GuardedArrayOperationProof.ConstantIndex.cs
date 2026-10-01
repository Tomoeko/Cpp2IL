using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedArrayOperationProof
{
    // A zero index has no native sign-extension producer. Admit this closed
    // terminal shape separately, then share the ordinary array/call provenance
    // and typed graph validators with dynamic-index operations.
    private static NativeEvidence? TryConstantTail(IReadOnlyList<NativeInstruction> body,
        Func<ulong, bool> nullHelper, Func<ulong, bool> boundsHelper)
    {
        if (body.Count != 14 || !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Mov_r64_rm64 || body[1].Op0Register != NativeRegister.RDX ||
            body[1].Op1Kind != OpKind.Memory || body[1].MemoryBase != NativeRegister.RCX ||
            body[1].MemoryIndex != NativeRegister.None || body[1].MemoryDisplacement64 is < 16 or > int.MaxValue ||
            !NullTest(body[2]) || body[2].Op0Register != NativeRegister.RDX ||
            !ConstantBranch(body[3], Mnemonic.Je, body[10].IP) ||
            body[4].Code != Code.Cmp_rm32_imm8 || body[4].Immediate8 != 0 ||
            !Memory(body[4], 0, 0x18, 4, indexed: false) || body[4].MemoryBase != NativeRegister.RDX ||
            !ConstantBranch(body[5], Mnemonic.Jbe, body[12].IP) ||
            body[6].Code != Code.Mov_r64_rm64 || body[6].Op0Register != NativeRegister.RDX ||
            !Memory(body[6], 1, 0x20, 8, indexed: false) || body[6].MemoryBase != NativeRegister.RDX ||
            body[7].Code != Code.Xor_r32_rm32 || body[7].Op0Kind != OpKind.Register ||
            body[7].Op1Kind != OpKind.Register || body[7].Op0Register != NativeRegister.R8D ||
            body[7].Op1Register != NativeRegister.R8D ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) || !IsTailInvocation(body[9]) ||
            body[9].NearBranchTarget >= body[0].IP && body[9].NearBranchTarget < body[^1].NextIP ||
            body[10].Code != Code.Call_rel32_64 || body[11].Code != Code.Int3 ||
            body[12].Code != Code.Call_rel32_64 || body[13].Code != Code.Int3 ||
            !nullHelper(body[10].NearBranchTarget) || !boundsHelper(body[12].NearBranchTarget))
            return null;
        return new NativeEvidence(9, 10, 12,
            [new NativeSite(6, 2, 4, -1, NativeRegister.RDX, NativeRegister.None,
                NativeRegister.None, false, 8, null, ConstantIndex: 0)], [1, 6, 9]);
    }

    private static bool ConstantBranch(NativeInstruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.FlowControl == FlowControl.ConditionalBranch && instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;

    private static bool ConstantMetadata(MethodAnalysisContext caller,
        IReadOnlyList<NativeInstruction> body, NativeEvidence native)
    {
        var app = caller.AppContext;
        if (native.Sites is not [{ ConstantIndex: 0 }] || caller.IsStatic || caller.IsVirtual ||
            caller.Parameters.Count != 0 || !caller.IsVoid || caller.DeclaringType is not { } owner ||
            !ConstantClass(owner) || !ConstantSignature(caller, null) ||
            !app.MethodsByAddress.TryGetValue(caller.UnderlyingPointer, out var callers) ||
            callers is not [var originalCaller] || !ReferenceEquals(originalCaller, caller) ||
            !app.MethodsByAddress.TryGetValue(body[native.SuccessEnd].NearBranchTarget, out var targets) ||
            targets is not [var target] || !ReferenceEquals(target.DeclaringType, owner) ||
            target.IsStatic || target.IsVirtual || !target.IsVoid || target.Parameters.Count != 1)
            return false;

        var offset = checked((int)body[1].MemoryDisplacement64);
        // Count all original offset matches before checking type or eligibility.
        if (owner.Fields.Where(field => field.Offset == offset && !field.IsStatic).ToArray() is not [var field] ||
            field.BackingData?.Field is not { } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ValidTypeIndex(app, definition.typeIndex.Value) ||
            definition.RawFieldType is not { } raw || !ReferenceDescriptor(raw) ||
            raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY || raw.Data.Dummy == 0 ||
            raw.GetEncapsulatedType() is not { } elementRaw ||
            !app.Binary.TryGetTypeVirtualAddress(elementRaw, out var elementAddress) ||
            elementAddress != raw.Data.Dummy || ResolveConstantClass(app, elementRaw) is not { } element ||
            !ConstantClass(element) || !ConstantSignature(target, element) ||
            field.FieldType is not SzArrayTypeAnalysisContext array ||
            !ReferenceEquals(array.ElementType, element))
            return false;
        return true;
    }

    private static bool ValidTypeIndex(ApplicationAnalysisContext app, int index) =>
        index >= 0 && index < app.Binary.AllTypes.Length;

    private static bool CoherentDescriptor(Il2CppType raw) => raw.Data != null &&
        raw.Datapoint == raw.Data.Dummy && raw.Attrs == (raw.Bits & 0xFFFF) &&
        raw.Type == (Il2CppTypeEnum)((raw.Bits >> 16) & 0xFF) &&
        raw.NumMods == ((raw.Bits >> 24) & 0x1F) && raw.Byref == ((raw.Bits >> 29) & 1) &&
        raw.Pinned == ((raw.Bits >> 30) & 1) && raw.ValueType == (raw.Bits >> 31);

    private static bool ReferenceDescriptor(Il2CppType raw) => CoherentDescriptor(raw) &&
        raw.NumMods == 0 && raw.Byref == 0 && raw.Pinned == 0 && raw.ValueType == 0;

    private static TypeAnalysisContext? ResolveConstantClass(ApplicationAnalysisContext app, Il2CppType? raw)
    {
        if (raw == null || !ReferenceDescriptor(raw)) return null;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT)
        {
            var systemObject = app.SystemTypes.SystemObjectType;
            var definition = systemObject.Definition;
            return definition != null && ValidTypeIndex(app, definition.ByvalTypeIndex.Value) &&
                   ReferenceDescriptor(definition.RawType) && definition.RawType.Type == raw.Type &&
                   definition.TypeIndex.Value >= 0 && raw.Data.Dummy == (ulong)definition.TypeIndex.Value &&
                   definition.RawType.Data.Dummy == raw.Data.Dummy ? systemObject : null;
        }
        if (raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS || raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount)
            return null;
        var original = app.Metadata.typeDefs[(int)raw.Data.Dummy];
        if (original.DeclaringAssembly is not { } image || app.ResolveContextForAssembly(image) is not { } assembly ||
            !ReferenceEquals(assembly.Definition?.Image, image) || assembly.GetTypeByDefinition(original) is not { } type ||
            !ReferenceEquals(type.Definition, original) || !ReferenceEquals(type.AppContext, app) ||
            !ReferenceEquals(type.DeclaringAssembly, assembly) ||
            assembly.Types.Count(candidate => ReferenceEquals(candidate, type)) != 1)
            return null;
        return type;
    }

    private static bool ConstantClass(TypeAnalysisContext type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null;)
        {
            if (!visited.Add(current) || visited.Count > 32 || current.Definition is not
                    { GenericContainerIndex: { IsNull: true }, DeclaringTypeIndex: { IsNull: true },
                        HasCctor: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true } definition ||
                !ValidTypeIndex(type.AppContext, definition.ByvalTypeIndex.Value) ||
                !definition.ParentIndex.IsNull && !ValidTypeIndex(type.AppContext, definition.ParentIndex.Value) ||
                !ReferenceEquals(ResolveConstantClass(type.AppContext, definition.RawType), current) ||
                current.DeclaringType != null || current.IsValueType || current.IsInterface || current.IsGenericInstance ||
                current.GenericParameters.Count != 0 || current.Name != current.DefaultName ||
                current.Namespace != current.DefaultNamespace || current.Attributes != current.DefaultAttributes ||
                current.OverrideBaseType != null || (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                !ConstantMembers(current))
                return false;
            if (ReferenceEquals(current, type.AppContext.SystemTypes.SystemObjectType))
                return definition.ParentIndex.IsNull && definition.RawBaseType == null;
            if (ResolveConstantClass(type.AppContext, definition.RawBaseType) is not { } parent ||
                !ReferenceEquals(current.BaseType, parent)) return false;
            current = parent;
        }
        return false;
    }

    private static bool ConstantMembers(TypeAnalysisContext type)
    {
        var definition = type.Definition!;
        if (!(definition.Methods ?? []).SequenceEqual(type.Methods.Select(method => method.Definition)) ||
            !(definition.Fields ?? []).SequenceEqual(type.Fields.Select(field => field.BackingData?.Field)) ||
            !(definition.Properties ?? []).SequenceEqual(type.Properties.Select(property => property.Definition)) ||
            !(definition.Events ?? []).SequenceEqual(type.Events.Select(member => member.Definition)) ||
            type.Methods.Any(method => method.Name == ".cctor" || method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
                method.Name == ".ctor" && !ConstantConstructor(method) ||
                method.OverrideReturnType != null || !ReferenceEquals(method.DeclaringType, type) ||
                method.Definition == null || !ValidTypeIndex(type.AppContext, method.Definition.returnTypeIdx.Value) ||
                method.Definition.RawReturnType is not { } raw || !RetainedDescriptor(type.AppContext, raw) ||
                method.Parameters.Count != method.Definition.parameterCount ||
                method.Definition.InternalParameterData is not { } originals || originals.Length != method.Parameters.Count ||
                method.Parameters.Where((parameter, index) => !ReferenceEquals(parameter.Definition, originals[index]) ||
                    !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != index ||
                    parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                    parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue || parameter.Definition == null ||
                    !ValidTypeIndex(type.AppContext, parameter.Definition.typeIndex.Value) ||
                    parameter.Definition.RawType is not { } parameterRaw ||
                    !RetainedDescriptor(type.AppContext, parameterRaw)).Any()) ||
            type.Properties.Any(property => property.Name != property.DefaultName ||
                property.Attributes != property.DefaultAttributes || property.OverridePropertyType != null ||
                !ReferenceEquals(property.DeclaringType, type) || property.Definition == null ||
                !ReferenceEquals(property.Getter?.Definition, property.Definition.Getter) ||
                !ReferenceEquals(property.Setter?.Definition, property.Definition.Setter)))
            return false;
        foreach (var field in type.Fields)
        {
            if (field.BackingData?.Field is not { } original || !ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(original.DeclaringType, definition) || !ValidTypeIndex(type.AppContext, original.typeIndex.Value) ||
                original.RawFieldType is not { } raw || !CoherentDescriptor(raw) ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                field.Offset != field.DefaultOffset || field.OverrideFieldType != null || field.UseOverrideConstantValue)
                return false;
            if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
                !ReferenceEquals(ResolveConstantClass(type.AppContext, raw), field.FieldType)) return false;
            if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY &&
                (!ReferenceDescriptor(raw) || raw.GetEncapsulatedType() is not { } element ||
                 !CoherentDescriptor(element) || !type.AppContext.Binary.TryGetTypeVirtualAddress(element, out var address) ||
                 address != raw.Data.Dummy ||
                 element.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
                    ResolveConstantClass(type.AppContext, element) == null)) return false;
        }
        return true;
    }

    private static bool ConstantConstructor(MethodAnalysisContext method) =>
        method.Definition is { genericContainerIndex: { IsNull: true } } definition &&
        ValidTypeIndex(method.AppContext, definition.returnTypeIdx.Value) &&
        definition.RawReturnType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 1 } raw &&
        CoherentDescriptor(raw) && !method.IsStatic && !method.IsVirtual && method.IsVoid && method.GenericParameters.Count == 0 &&
        (method.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) ==
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0;

    // Uncalled framework siblings may legitimately have byref parameters. They
    // need coherent original descriptors and bounded identities, not the ABI
    // eligibility required of the selected caller and callee.
    private static bool RetainedDescriptor(ApplicationAnalysisContext app, Il2CppType raw) =>
        CoherentDescriptor(raw) && (raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS => raw.ValueType == 0 &&
                raw.Data.Dummy < (ulong)app.Metadata.TypeDefinitionCount,
            Il2CppTypeEnum.IL2CPP_TYPE_OBJECT => raw.ValueType == 0 &&
                app.SystemTypes.SystemObjectType.Definition is { } definition &&
                definition.TypeIndex.Value >= 0 && raw.Data.Dummy == (ulong)definition.TypeIndex.Value,
            _ => true,
        });

    private static bool ConstantSignature(MethodAnalysisContext method, TypeAnalysisContext? parameterType)
    {
        var app = method.AppContext;
        if (method.Definition is not { genericContainerIndex: { IsNull: true } } definition ||
            !ValidTypeIndex(app, definition.returnTypeIdx.Value) ||
            definition.declaringTypeIdx.Value < 0 || definition.declaringTypeIdx.Value >= app.Metadata.TypeDefinitionCount ||
            !ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) ||
            definition.RawReturnType is not { } rawReturn || !CoherentDescriptor(rawReturn) ||
            rawReturn.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID || rawReturn.Attrs != 0 ||
            rawReturn.NumMods != 0 || rawReturn.Byref != 0 || rawReturn.Pinned != 0 || rawReturn.ValueType != 1 ||
            definition.parameterCount != (parameterType == null ? 0 : 1) ||
            definition.InternalParameterData is not { } parameters || parameters.Length != definition.parameterCount ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null)
            return false;
        if (parameterType == null) return method.Parameters.Count == 0;
        return parameters is [var original] && ValidTypeIndex(app, original.typeIndex.Value) &&
               ReferenceEquals(ResolveConstantClass(app, original.RawType), parameterType) &&
               method.Parameters is [var parameter] && ReferenceEquals(parameter.Definition, original) &&
               ReferenceEquals(parameter.DeclaringMethod, method) && parameter.ParameterIndex == 0 &&
               !parameter.IsRef && parameter.OverrideParameterType == null &&
               parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes &&
               ReferenceEquals(parameter.ParameterType, parameterType);
    }

    private static X64SmallAggregateFieldGetterProof.InputState? CaptureConstantInput(MethodAnalysisContext caller,
        IReadOnlyList<NativeInstruction> body, NativeEvidence native)
    {
        if (!ConstantMetadata(caller, body, native) || caller.AppContext.Binary is not PE pe) return null;
        var target = caller.AppContext.MethodsByAddress[body[native.SuccessEnd].NearBranchTarget].Single();
        var field = caller.DeclaringType!.Fields.Single(candidate => !candidate.IsStatic &&
            candidate.Offset == (int)body[1].MemoryDisplacement64);
        var rawArray = field.BackingData!.Field.RawFieldType!;
        var element = ((SzArrayTypeAnalysisContext)field.FieldType).ElementType;
        var values = new List<object>();
        if (!CaptureConstantMethod(caller, values) || !CaptureConstantMethod(target, values)) return null;
        var seen = new HashSet<TypeAnalysisContext>();
        foreach (var start in new[] { caller.DeclaringType!, element })
            for (var type = start; type != null; type = type.BaseType)
            {
                if (!seen.Add(type)) break;
                var definition = type.Definition!;
                var assembly = type.DeclaringAssembly;
                if (assembly.Definition == null || assembly.Name != assembly.DefaultName ||
                    assembly.Version != assembly.DefaultVersion || assembly.Flags != assembly.DefaultFlags ||
                    assembly.HashAlgorithm != assembly.DefaultHashAlgorithm ||
                    (assembly.Culture ?? "") != (assembly.DefaultCulture ?? "") ||
                    !(assembly.PublicKey ?? []).SequenceEqual(assembly.DefaultPublicKey ?? []) ||
                    !(assembly.PublicKeyToken ?? []).SequenceEqual(assembly.DefaultPublicKeyToken ?? [])) return null;
                values.AddRange([type, definition, type.Name, type.Namespace, type.Attributes,
                    (object?)type.BaseType ?? DBNull.Value, assembly, assembly.Definition, assembly.Name, assembly.Version,
                    assembly.Flags, assembly.HashAlgorithm, (object?)assembly.Culture ?? DBNull.Value,
                    assembly.Definition.ImageIndex, assembly.Definition.Token, assembly.Definition.ReferencedAssemblyStart,
                    assembly.Definition.ReferencedAssemblyCount, definition.NameIndex, definition.NamespaceIndex,
                    definition.Token, definition.Flags, definition.Bitfield, definition.ByvalTypeIndex,
                    definition.ParentIndex, definition.GenericContainerIndex, definition.DeclaringTypeIndex,
                    definition.FirstFieldIdx, definition.FieldCount, definition.FirstMethodIdx, definition.MethodCount,
                    definition.RawSizes.instance_size, definition.RawSizes.native_size, definition.RawSizes.static_fields_size,
                    definition.RawSizes.thread_static_fields_size, type.Fields.Count, type.Methods.Count,
                    type.Properties.Count, type.Events.Count]);
                foreach (var value in assembly.PublicKey ?? []) values.Add(value);
                foreach (var value in assembly.PublicKeyToken ?? []) values.Add(value);
                foreach (var reference in assembly.Definition.ReferencedAssemblies) values.Add(reference);
                X64SmallAggregateFieldGetterProof.CaptureRawType(definition.RawType, values);
                if (definition.RawBaseType is { } rawBase) X64SmallAggregateFieldGetterProof.CaptureRawType(rawBase, values);
                foreach (var member in type.Fields)
                {
                    if (member.BackingData?.Field is not { } original || !ValidTypeIndex(caller.AppContext, original.typeIndex.Value) ||
                        original.RawFieldType is not { } raw || !CoherentDescriptor(raw) ||
                        !ReferenceEquals(original.DeclaringType, definition) || member.Name != member.DefaultName ||
                        member.Attributes != member.DefaultAttributes || member.Offset != member.DefaultOffset ||
                        member.OverrideFieldType != null || member.UseOverrideConstantValue) return null;
                    values.AddRange([member, original, member.Name, member.Attributes, member.Offset,
                        original.nameIndex, original.token, original.typeIndex]);
                    X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
                }
                foreach (var member in type.Methods)
                {
                    if (!CaptureConstantMethod(member, values)) return null;
                }
                foreach (var property in type.Properties)
                {
                    var original = property.Definition!;
                    values.AddRange([property, original, property.Name, property.Attributes, original.nameIndex, original.token,
                        original.attrs, original.get, original.set, (object?)property.Getter ?? DBNull.Value,
                        (object?)property.Setter ?? DBNull.Value]);
                }
                foreach (var member in type.Events) values.AddRange([member, member.Definition!]);
            }
        X64SmallAggregateFieldGetterProof.CaptureRawType(rawArray.GetEncapsulatedType(), values);
        var offset = pe.MapVirtualAddressToRaw(body[0].IP, false);
        var length = checked((int)(body[^1].NextIP - body[0].IP));
        return new(values, pe.GetRawBinaryContent().Slice(checked((int)offset), length).ToArray());
    }

    private static bool CaptureConstantMethod(MethodAnalysisContext method, List<object> values)
    {
        if (method.Definition is not { } definition || definition.RawReturnType is not { } rawReturn ||
            !CoherentDescriptor(rawReturn)) return false;
        values.AddRange([method, definition, method.Name, method.Attributes, method.ImplAttributes, method.UnderlyingPointer,
            (object?)method.OverrideReturnType ?? DBNull.Value, definition.nameIndex, definition.token, definition.flags,
            definition.iflags, definition.declaringTypeIdx, definition.returnTypeIdx, definition.parameterStart,
            definition.parameterCount, definition.genericContainerIndex, definition.slot]);
        X64SmallAggregateFieldGetterProof.CaptureRawType(rawReturn, values);
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Definition is not { } original || original.RawType is not { } raw || !CoherentDescriptor(raw)) return false;
            values.AddRange([parameter, original, parameter.ParameterIndex, parameter.Name, parameter.Attributes,
                (object?)parameter.OverrideParameterType ?? DBNull.Value, original.nameIndex, original.token, original.typeIndex]);
            X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
        }
        return true;
    }
}
