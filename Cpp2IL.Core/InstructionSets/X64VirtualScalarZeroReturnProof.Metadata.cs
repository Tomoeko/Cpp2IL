using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64VirtualScalarZeroReturnProof
{
    private static readonly X64CallingConventionResolver CallingConventions = new();

    private static bool CompleteAliases(MethodAnalysisContext method,
        X64GenericMethodTableProof.Evidence tables, out MethodAnalysisContext[] aliases)
    {
        aliases = [];
        var app = method.AppContext;
        var pointer = method.UnderlyingPointer;
        if (pointer == 0 || Contains(tables.MethodPointers) || Contains(tables.Invokers) || Contains(tables.AdjustorThunks) ||
            !app.MethodsByAddress.TryGetValue(pointer, out var cached) || cached.Count == 0 ||
            cached.Any(alias => alias == null || !ReferenceEquals(alias.AppContext, app) || alias.UnderlyingPointer != pointer) ||
            cached.Distinct().Count() != cached.Count) return false;
        var original = new HashSet<MethodAnalysisContext>();
        foreach (var definition in app.Metadata.methodDefs)
        {
            if (definition.MethodPointer != pointer) continue;
            if (app.ResolveContextForMethod(definition) is not { } context ||
                !ReferenceEquals(context.Definition, definition) || !original.Add(context)) return false;
        }
        if (!original.Contains(method) || !original.SetEquals(cached)) return false;
        aliases = original.OrderBy(alias => alias.Definition!.MethodIndex.Value).ToArray();
        return true;

        bool Contains(ReadOnlySpan<ulong> pointers)
        {
            foreach (var value in pointers) if (value == pointer) return true;
            return false;
        }
    }

    private static bool CaptureSignature(MethodAnalysisContext method, List<object> inputs)
    {
        var app = method.AppContext;
        if (method is ConcreteGenericMethodAnalysisContext || !method.IsVirtual || method.IsStatic ||
            method.IsAbstract || method.Name is ".ctor" or ".cctor" || method.GenericParameters.Count != 0 ||
            method.Definition is not { GenericContainer: null, IsUnmanagedCallersOnly: false,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } raw } definition ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            owner.IsInterface || owner.IsValueType || owner.IsGenericInstance ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !(raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4 && ReferenceEquals(method.ReturnType, app.SystemTypes.SystemSingleType) ||
              raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_R8 && ReferenceEquals(method.ReturnType, app.SystemTypes.SystemDoubleType)) ||
            CallingConventions.ReturnsViaHiddenBuffer(method) || CallingConventions.ReturnRegister(method).Name != "xmm0" ||
            !CaptureMethod(method, inputs)) return false;
        // This leaf never reads arguments. Still retain the complete positional
        // ABI, including this and the hidden MethodInfo argument, for every alias.
        var arguments = CallingConventions.ResolveForManaged(method);
        if (arguments.Length != method.Parameters.Count + 2 || arguments[0] is not Cpp2IL.Core.ISIL.Register { Name: "rcx" }) return false;
        foreach (var argument in arguments) inputs.Add(argument);
        foreach (var parameter in method.Parameters)
            if (parameter.IsRef || parameter.Definition?.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } argumentType ||
                !OrdinaryArgument(argumentType) ||
                (parameter.Attributes & (ParameterAttributes.Out | ParameterAttributes.HasFieldMarshal | ParameterAttributes.HasDefault)) != 0)
                return false;
        return true;

        bool OrdinaryArgument(Il2CppType argument) => argument.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
            Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
            Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
            Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8 => argument.ValueType == 1,
            Il2CppTypeEnum.IL2CPP_TYPE_STRING => argument.ValueType == 0,
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT =>
                X64OriginalReferenceClassProof.ResolveClass(app, argument) != null,
            _ => false
        };
    }

    private static bool CaptureMethod(MethodAnalysisContext method, List<object> inputs)
    {
        var app = method.AppContext;
        if (method.Definition is not { RawReturnType: { } raw } definition ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            method.DeclaringType?.Definition is not { } owner || !ReferenceEquals(definition.DeclaringType, owner) ||
            method.Name != method.DefaultName || method.OverrideName != null || method.OverrideAttributes != null ||
            method.OverrideImplAttributes != null || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes || method.OverrideReturnType != null ||
            !CaptureDescriptor(app, raw, inputs) || !CaptureResolvedType(method.ReturnType, inputs) ||
            definition.InternalParameterData is not { } parameters || parameters.Length != method.Parameters.Count ||
            method.Parameters.Count != definition.parameterCount) return false;
        if (definition.GenericContainer == null)
        {
            if (method.GenericParameters.Count != 0) return false;
        }
        else
        {
            if (OriginalGenericDeclarationIdentityProof.TryIdentify(method) is not { } generic) return false;
            inputs.Add(generic.Definition); inputs.Add(generic.Container); inputs.Add(generic.Table);
            foreach (var parameter in generic.Parameters)
            {
                inputs.Add(parameter.Origin); inputs.Add(parameter.Context);
                foreach (var constraint in parameter.ConstraintIndices) inputs.Add(constraint);
                foreach (var constraint in parameter.ConstraintContexts) inputs.Add(constraint);
            }
        }
        inputs.AddRange([method, definition, method.Name, method.Attributes, method.ImplAttributes,
            definition.MethodIndex.Value, definition.nameIndex, definition.declaringTypeIdx.Value,
            definition.returnTypeIdx.Value, definition.parameterStart.Value, definition.genericContainerIndex.Value,
            definition.token, definition.flags, definition.iflags, definition.slot, definition.parameterCount]);
        for (var ordinal = 0; ordinal < parameters.Length; ordinal++)
        {
            var parameter = method.Parameters[ordinal];
            var original = parameters[ordinal];
            if (parameter == null || !ReferenceEquals(parameter.Definition, original) ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != ordinal ||
                !X64OriginalReferenceClassProof.OriginalParameter(app, definition, ordinal, original) ||
                parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
                original.RawType is not { } parameterRaw || !CaptureDescriptor(app, parameterRaw, inputs) ||
                !CaptureResolvedType(parameter.ParameterType, inputs)) return false;
            inputs.AddRange([parameter, original, parameter.Name, parameter.Attributes,
                original.nameIndex, original.token, original.typeIndex.Value]);
        }
        return true;
    }

    private static bool CaptureDescriptor(ApplicationAnalysisContext app, Il2CppType descriptor, List<object> inputs) =>
        X64ScalarStaticConstructorProof.CaptureDescriptor(app, descriptor, inputs);

    // Wrappers are freshly allocated by the resolver. Their element identity,
    // shape and original descriptor are stable; the wrapper object itself is not.
    private static bool CaptureResolvedType(TypeAnalysisContext type, List<object> inputs, int depth = 0)
    {
        if (depth >= 32) return false;
        inputs.Add(type.GetType()); inputs.Add(type.Type);
        if (type is WrappedTypeAnalysisContext wrapped)
        {
            if (wrapped is ArrayTypeAnalysisContext array) inputs.Add(array.Rank);
            return CaptureResolvedType(wrapped.ElementType, inputs, depth + 1);
        }
        if (type is GenericInstanceTypeAnalysisContext instance)
        {
            var app = instance.AppContext;
            if (!instance.HasUnchangedOriginalRawType || instance.OriginalRawType is not { } raw ||
                !CaptureDescriptor(app, raw, inputs)) return false;
            var original = raw.GetGenericClass();
            if (!ReferenceEquals(instance.GenericType.Definition, original.TypeDefinition) ||
                !ReferenceEquals(app.ResolveContextForType(original.TypeDefinition), instance.GenericType) ||
                original.Context.method_inst != 0 || original.Context.ClassInst is not { pointerCount: > 0 and <= 32 } instantiation ||
                instantiation.pointerCount != (ulong)instance.GenericArguments.Count ||
                !CaptureNative(raw.Data.GenericClass, 32) || !CaptureNative(original.Context.class_inst, 16) ||
                !CaptureNative(instantiation.pointerStart, checked((int)instantiation.pointerCount * 8)) ||
                !CaptureResolvedType(instance.GenericType, inputs, depth + 1)) return false;
            var arguments = instantiation.Types;
            for (var ordinal = 0; ordinal < arguments.Length; ordinal++)
            {
                var actual = new List<object>(); var expected = new List<object>();
                if (!CaptureDescriptor(app, arguments[ordinal], inputs) ||
                    !CaptureResolvedType(instance.GenericArguments[ordinal], actual, depth + 1) ||
                    !CaptureResolvedType(app.ResolveIl2CppType(arguments[ordinal]), expected, depth + 1) ||
                    !actual.SequenceEqual(expected)) return false;
                inputs.AddRange(actual);
            }
            return true;

            bool CaptureNative(ulong address, int length)
            {
                if (address == 0 || address > ulong.MaxValue - (ulong)length) return false;
                var offset = app.Binary.MapVirtualAddressToRaw(address, false);
                var bytes = app.Binary.GetRawBinaryContent();
                if (offset < 0 || offset > bytes.Length - length ||
                    app.Binary.MapVirtualAddressToRaw(address + (ulong)length - 1, false) != offset + length - 1) return false;
                inputs.Add(address); inputs.Add(Convert.ToBase64String(bytes.Slice(checked((int)offset), length).ToArray()));
                return true;
            }
        }
        if (type is GenericParameterTypeAnalysisContext parameter)
        {
            if (OriginalGenericDeclarationIdentityProof.TryIdentify(parameter.Owner) is not { } declaration ||
                parameter.Index < 0 || parameter.Index >= declaration.Parameters.Length ||
                !ReferenceEquals(declaration.Parameters[parameter.Index].Context, parameter)) return false;
            inputs.Add(parameter); inputs.Add(parameter.Owner); inputs.Add(parameter.Index);
            inputs.Add(parameter.Name); inputs.Add(parameter.Attributes);
            return true;
        }
        if (type.Definition is not { } definition ||
            !ReferenceEquals(type.AppContext.ResolveContextForType(definition), type) ||
            !X64OriginalReferenceClassProof.OriginalType(type.AppContext, definition) ||
            type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes) return false;
        inputs.AddRange([type, definition, type.Name, type.Namespace, type.Attributes,
            Convert.ToBase64String(type.AppContext.Metadata.ReadClassArrayAtRawAddr<byte>(
                type.AppContext.Metadata.metadataHeader.typeDefinitions.Offset + (long)definition.TypeIndex.Value * 88, 88))]);
        return true;
    }

    private static bool CaptureType(TypeAnalysisContext type, List<object> inputs)
    {
        var app = type.AppContext;
        if (type.Definition is not { GenericContainer: null } definition || type.IsGenericInstance || type.IsValueType ||
            type.GenericParameters.Count != 0 || !X64OriginalReferenceClassProof.OriginalType(app, definition) ||
            !ReferenceEquals(app.ResolveContextForType(definition), type) ||
            !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, definition.RawType), type) ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(type.DeclaringAssembly) ||
            type.Name != type.DefaultName || type.OverrideName != null || type.OverrideNamespace != null || type.OverrideAttributes != null ||
            type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            type.OverrideBaseType != null || !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            !CaptureDescriptor(app, definition.RawType, inputs) ||
            !(definition.Methods ?? []).SequenceEqual(type.Methods.Select(method => method.Definition)) ||
            !(definition.Properties ?? []).SequenceEqual(type.Properties.Select(property => property.Definition))) return false;
        if (definition.ParentIndex.IsNull)
        {
            if (type.BaseType != null) return false;
        }
        else if (definition.RawBaseType is not { } parent || !CaptureDescriptor(app, parent, inputs) ||
                 !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, parent), type.BaseType)) return false;
        if (definition.DeclaringTypeIndex.IsNull)
        {
            if (type.DeclaringType != null) return false;
        }
        else if (!X64OriginalReferenceClassProof.ValidTypeIndex(app, definition.DeclaringTypeIndex.Value) ||
                 app.Binary.GetType(definition.DeclaringTypeIndex) is not { } enclosing ||
                 !CaptureDescriptor(app, enclosing, inputs) ||
                 !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, enclosing), type.DeclaringType)) return false;
        inputs.AddRange([type, definition, type.Name, type.Namespace, type.Attributes, type.DeclaringAssembly,
            type.BaseType ?? (object)"no-base", type.DeclaringType ?? (object)"no-enclosing-type",
            definition.ByvalTypeIndex.Value, definition.ParentIndex.Value, definition.DeclaringTypeIndex.Value,
            definition.Flags, definition.Bitfield, definition.Token,
            Convert.ToBase64String(app.Metadata.ReadClassArrayAtRawAddr<byte>(
                app.Metadata.metadataHeader.typeDefinitions.Offset + (long)definition.TypeIndex.Value * 88, 88))]);
        foreach (var method in type.Methods) if (method == null || !CaptureMethod(method, inputs)) return false;
        for (var ordinal = 0; ordinal < type.Properties.Count; ordinal++)
        {
            var property = type.Properties[ordinal];
            if (property?.Definition is not { } original || !OriginalProperty(type, ordinal, original) ||
                !ReferenceEquals(property.DeclaringType, type) || property.Name != property.DefaultName ||
                property.Attributes != property.DefaultAttributes || property.OverridePropertyType != null ||
                !CaptureResolvedType(property.PropertyType, inputs) ||
                !Accessor(original.get.Value, property.Getter) || !Accessor(original.set.Value, property.Setter)) return false;
            inputs.AddRange([property, original, property.Name, property.Attributes,
                property.Getter ?? (object)"no-getter", property.Setter ?? (object)"no-setter",
                original.nameIndex, original.get.Value, original.set.Value, original.attrs, original.token]);
        }
        return true;

        bool Accessor(int index, MethodAnalysisContext? method) => index == -1 ? method == null :
            index >= 0 && index < type.Methods.Count && ReferenceEquals(type.Methods[index], method);
    }

    private static bool OriginalProperty(TypeAnalysisContext type, int ordinal, Il2CppPropertyDefinition property)
    {
        var section = type.AppContext.Metadata.metadataHeader.properties;
        var index = (long)type.Definition!.FirstPropertyId.Value + ordinal;
        var words = new uint[] { unchecked((uint)property.nameIndex), unchecked((uint)property.get.Value),
            unchecked((uint)property.set.Value), property.attrs, property.token };
        return property.PropertyIndex == index && ReferenceEquals(property.DeclaringType, type.Definition) &&
            index >= 0 && index < section.Size / 20 && section.Size % 20 == 0 &&
            type.AppContext.Metadata.ReadClassArrayAtRawAddr<uint>(section.Offset + index * 20, 5).SequenceEqual(words);
    }
}
