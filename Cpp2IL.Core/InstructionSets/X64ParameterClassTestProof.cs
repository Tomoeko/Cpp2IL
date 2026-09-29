using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates a complete object-parameter class test. The unsigned depth
/// comparison and parent-table lookup either preserve the input reference or
/// produce null. A reachable exceptional cast helper is never admitted.
/// </summary>
internal static class X64ParameterClassTestProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Shape(ulong OnceFlag, ulong TypeInfoSlot,
        ulong MetadataInitializer);

    internal static TypeAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not
                    { } unwind || !OrdinaryMethod(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings is not [var bound] ||
                !ReferenceEquals(bound, method) || decoded.Count == 0 ||
                decoded[0].IP != method.UnderlyingPointer)
                return null;

            var start = method.UnderlyingPointer;
            var region = unwind.ClassifySpan(start, start + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != start || region.RootStart != start ||
                region.End <= start || region.End - start is < 100 or > 160 ||
                unwind.ClassifySpan(start, region.End).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame) ||
                app.MethodsByAddress.Keys.Any(address => address > start &&
                    address < region.End))
                return null;

            var inRegion = decoded.TakeWhile(instruction => instruction.IP <
                region.End).ToArray();
            var body = inRegion.TakeWhile(instruction => instruction.Code !=
                Code.Int3).ToArray();
            if (TryProveShape(body) is not { } shape ||
                body[^1].NextIP > region.End ||
                inRegion.Skip(body.Length).Any(instruction => instruction.Code !=
                    Code.Int3) || !X64NativePaddingProof.HasInt3Padding(pe,
                    body[^1].NextIP, region.End) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null)
                return null;

            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            var length = checked((int)(body[^1].NextIP - start));
            if (method.RawBytes.Length < length ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, method.RawBytes.AsSpan().Slice(0, length), start) ||
                !X86Utils.Iterate(method).Take(body.Length).SequenceEqual(body))
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Find must authenticate the complete native caller before this binding can
    // authorize emission. Keep metadata and helper mutations separately testable.
    internal static TypeAnalysisContext? BindProvedShape(MethodAnalysisContext method,
        Shape shape)
    {
        try
        {
            if (!OrdinaryMethod(method))
                return null;
            var target = BindTypeInfoTarget(method, shape);
            return ReferenceEquals(target, method.ReturnType) ? target : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Both reference and Boolean results test this player-derived target. A
    // Boolean signature cannot identify the TypeInfo used by the native body.
    internal static TypeAnalysisContext? BindTypeInfoTarget(MethodAnalysisContext method,
        Shape shape)
    {
        var app = method.AppContext;
        if (app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            shape.TypeInfoSlot <= shape.OnceFlag && shape.OnceFlag - shape.TypeInfoSlot < 8 ||
            !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.OnceFlag) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.TypeInfoSlot, 8) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, shape.TypeInfoSlot, 8) ||
            shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, pe, unwind, shape.MetadataInitializer))
            return null;

        var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot);
        if (usage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
            app.ResolveIl2CppType(usage.AsType()) is not { } target ||
            method.DeclaringType is not { } owner ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                owner.DeclaringAssembly, target.DeclaringAssembly) ||
            !StableClassHierarchy(target))
            return null;
        return target;
    }

    internal static bool OrdinaryMethod(MethodAnalysisContext method,
        bool booleanResult = false)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !X64ClassCastLookupProof.PublicOrdinaryClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: var returnKind,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            returnKind != (booleanResult ? Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN :
                Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [{ RawType:
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, NumMods: 0,
                    Byref: 0, Pinned: 0 } }] ||
            !method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters is not [var value] ||
            !ReferenceEquals(value.DeclaringMethod, method) || value.ParameterIndex != 0 ||
            value.IsRef || value.Attributes != value.DefaultAttributes ||
            !ReferenceEquals(value.ParameterType, app.SystemTypes.SystemObjectType) ||
            value.OverrideParameterType != null ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            booleanResult && !ReferenceEquals(method.ReturnType,
                app.SystemTypes.SystemBooleanType) ||
            method.UnderlyingPointer == 0)
            return false;

        return true;
    }

    private static bool StableClassHierarchy(TypeAnalysisContext target)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var type = target; type != null && seen.Add(type); type = type.BaseType)
        {
            if (ReferenceEquals(type, target.AppContext.SystemTypes.SystemObjectType))
                return !ReferenceEquals(type, target);
            if (!X64ClassCastLookupProof.PublicOrdinaryClass(type))
                return false;
        }
        return false;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        var shape = X64ClassCastLookupProof.TryProveParameterShape(body);
        return shape == null ? null : new Shape(shape.Flag, shape.TypeInfoSlot,
            shape.Initializer);
    }
}
