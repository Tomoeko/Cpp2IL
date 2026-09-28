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
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a frame-free wrapper method that passes its unchanged value-type
/// receiver straight to an instance method on the sole scalar field. Windows
/// x64 passes the wrapper parameter in the same integer slot as that field.
/// </summary>
internal static class X64ScalarWrapperTailCallProof
{
    internal readonly record struct Shape(ulong TargetAddress, bool HasValueArgument);
    internal sealed record Evidence(FieldAnalysisContext Field, MethodAnalysisContext Target,
        bool HasValueArgument);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                app.InstructionSet.CallingConventionResolver is not X64CallingConventionResolver convention)
                return null;
            if (!OrdinaryCaller(method) || method.DeclaringType is not { } wrapper)
                return null;
            if (ScalarField(wrapper) is not { } field)
                return null;
            // UInt32.GetHashCode commonly shares its identity native body with
            // other scalar methods. Keep this operation unresolved until its
            // callsite has a separate managed-target proof.
            if (method.Name == "GetHashCode" &&
                ReferenceEquals(field.FieldType, app.SystemTypes.SystemUInt32Type))
                return null;
            if (!RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                convention.ReturnsViaHiddenBuffer(method))
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            var native = X86Utils.Iterate(method.RawBytes.AsSpan(), start, false);
            if (TryProveShape(native) is not { } shape ||
                shape.HasValueArgument != (method.Name == "CompareTo") ||
                !ClosedBody(method, native, pe, unwind) ||
                !HasExpectedArguments(convention, method, shape.HasValueArgument) ||
                !app.MethodsByAddress.TryGetValue(shape.TargetAddress, out var bindings) ||
                bindings is not [var target] ||
                !OrdinaryTarget(target, method, field) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
                convention.ReturnsViaHiddenBuffer(target) ||
                !HasExpectedArguments(convention, target, shape.HasValueArgument))
                return null;

            return new Evidence(field, target, shape.HasValueArgument);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 2 || body[0].NextIP != body[1].IP ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body[0].Code != Code.Xor_r32_rm32 ||
            body[0].FlowControl != FlowControl.Next ||
            body[0].OpCount != 2 || body[0].Op0Kind != OpKind.Register ||
            body[0].Op1Kind != OpKind.Register ||
            body[0].Op0Register != body[0].Op1Register ||
            body[0].Op0Register is not (NativeRegister.EDX or NativeRegister.R8D) ||
            body[1].Code != Code.Jmp_rel32_64 ||
            body[1].FlowControl != FlowControl.UnconditionalBranch ||
            body[1].OpCount != 1 || body[1].Op0Kind != OpKind.NearBranch64 ||
            body[1].NearBranchTarget == 0 ||
            body[1].NearBranchTarget == body[0].IP)
            return null;
        return new Shape(body[1].NearBranchTarget,
            body[0].Op0Register == NativeRegister.R8D);
    }

    private static bool OrdinaryCaller(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        var wrapper = method.DeclaringType;
        if (wrapper is not { Definition: { GenericContainer: null, HasCctor: false,
                IsValueType: true, IsEnumType: false, IsBlittable: true,
                IsByRefLike: false, IsImportOrWindowsRuntime: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } ||
            wrapper is GenericInstanceTypeAnalysisContext ||
            wrapper.GenericParameters.Count != 0 ||
            wrapper.Name != wrapper.DefaultName ||
            wrapper.Namespace != wrapper.DefaultNamespace ||
            wrapper.Attributes != wrapper.DefaultAttributes ||
            (wrapper.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(wrapper.BaseType, wrapper.DefaultBaseType) ||
            !ReferenceEquals(wrapper.BaseType, app.SystemTypes.SystemValueTypeType) ||
            wrapper.Methods.Any(candidate => candidate.Name == ".cctor"))
            return false;

        var parameterCount = method.Name == "CompareTo" ? 1 : 0;
        return method.Name is "ToString" or "GetHashCode" or "CompareTo" &&
               method.Name == method.DefaultName &&
               !method.IsStatic && !method.IsVoid &&
               method.GenericParameters.Count == 0 &&
               method.OverrideReturnType == null &&
               ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
               method.Definition is { GenericContainer: null,
                   RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               definition.RawReturnType.Type == (method.Name == "ToString"
                   ? Il2CppTypeEnum.IL2CPP_TYPE_STRING : Il2CppTypeEnum.IL2CPP_TYPE_I4) &&
               ReferenceEquals(definition.DeclaringType, wrapper.Definition) &&
               definition.parameterCount == parameterCount &&
               (definition.InternalParameterData?.Length ?? 0) == parameterCount &&
               method.Parameters.Count == parameterCount &&
               (parameterCount == 0 || UnchangedWrapperParameter(method, wrapper));
    }

    private static FieldAnalysisContext? ScalarField(TypeAnalysisContext wrapper)
    {
        var app = wrapper.AppContext;
        var fields = wrapper.Fields.Where(candidate => !candidate.IsStatic).ToArray();
        if (fields is not [{ } field] ||
            !ReferenceEquals(field.DeclaringType, wrapper) ||
            field.Name != field.DefaultName ||
            field.Offset != 0 || field.Offset != field.DefaultOffset ||
            field.Attributes != field.DefaultAttributes ||
            field.OverrideFieldType != null || field.UseOverrideConstantValue ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA |
                                 FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) != 0 ||
            field.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            !ReferenceEquals(field.FieldType, field.DefaultFieldType))
            return null;

        var primitive = field.FieldType;
        var width = primitive.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => 4,
            Il2CppTypeEnum.IL2CPP_TYPE_U8 => 8,
            _ => 0,
        };
        if (width == 0 ||
            raw.Type != primitive.Type ||
            !ReferenceEquals(primitive, app.SystemTypes.SystemUInt32Type) &&
            !ReferenceEquals(primitive, app.SystemTypes.SystemUInt64Type) ||
            TypeSizes.UnboxedSize(wrapper, 8) != width ||
            TypeSizes.UnboxedSize(primitive, 8) != width)
            return null;
        return field;
    }

    private static bool UnchangedWrapperParameter(MethodAnalysisContext method,
        TypeAnalysisContext wrapper)
    {
        if (method.Parameters is not [var parameter] ||
            method.Definition?.InternalParameterData is not [var rawParameter])
            return false;
        return parameter.ParameterIndex == 0 &&
               ReferenceEquals(parameter.DeclaringMethod, method) &&
               ReferenceEquals(parameter.Definition, rawParameter) &&
               !parameter.IsRef && parameter.Name == parameter.DefaultName &&
               parameter.Attributes == parameter.DefaultAttributes &&
               parameter.OverrideParameterType == null &&
               parameter.OverrideAttributes == null &&
               !parameter.UseOverrideDefaultValue &&
               ReferenceEquals(parameter.ParameterType, wrapper) &&
               ReferenceEquals(parameter.DefaultParameterType, wrapper) &&
               rawParameter.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                   NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool OrdinaryTarget(MethodAnalysisContext target,
        MethodAnalysisContext caller, FieldAnalysisContext field)
    {
        var app = caller.AppContext;
        var parameterCount = caller.Name == "CompareTo" ? 1 : 0;
        var primitive = field.FieldType;
        if (!ReferenceEquals(target.DeclaringType, primitive) ||
            target.Name != caller.Name || target.Name != target.DefaultName ||
            target.IsStatic || target.IsVoid ||
            target.GenericParameters.Count != 0 || target.OverrideReturnType != null ||
            target.Attributes != target.DefaultAttributes ||
            target.ImplAttributes != target.DefaultImplAttributes ||
            (target.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
            (target.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !ReferenceEquals(target.ReturnType, caller.ReturnType) ||
            !ReferenceEquals(target.DefaultReturnType, caller.ReturnType) ||
            !ReferenceEquals(target.ReturnType, caller.Name == "ToString"
                ? app.SystemTypes.SystemStringType : app.SystemTypes.SystemInt32Type) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(target) ||
            target.Definition is not { GenericContainer: null,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.RawReturnType.Type != (caller.Name == "ToString"
                ? Il2CppTypeEnum.IL2CPP_TYPE_STRING : Il2CppTypeEnum.IL2CPP_TYPE_I4) ||
            !ReferenceEquals(definition.DeclaringType, primitive.Definition) ||
            definition.parameterCount != parameterCount ||
            (definition.InternalParameterData?.Length ?? 0) != parameterCount ||
            target.Parameters.Count != parameterCount)
            return false;

        if (parameterCount == 0)
            return true;
        var parameter = target.Parameters[0];
        return parameter.ParameterIndex == 0 &&
               ReferenceEquals(parameter.DeclaringMethod, target) &&
               ReferenceEquals(parameter.Definition, definition.InternalParameterData![0]) &&
               !parameter.IsRef && parameter.Name == parameter.DefaultName &&
               parameter.Attributes == parameter.DefaultAttributes &&
               parameter.OverrideParameterType == null &&
               parameter.OverrideAttributes == null &&
               !parameter.UseOverrideDefaultValue &&
               ReferenceEquals(parameter.ParameterType, primitive) &&
               ReferenceEquals(parameter.DefaultParameterType, primitive) &&
               parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
               raw.Type == primitive.Type;
    }

    private static bool HasExpectedArguments(X64CallingConventionResolver convention,
        MethodAnalysisContext method, bool hasValueArgument)
    {
        var arguments = convention.ResolveForManaged(method);
        var expected = hasValueArgument
            ? new[] { "rcx", "rdx", "r8" }
            : ["rcx", "rdx"];
        return arguments.Length == expected.Length &&
               arguments.Select((operand, index) =>
                   operand is ManagedRegister register && register.Name == expected[index]).All(equal => equal);
    }

    private static bool ClosedBody(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native, PE pe,
        X64UnwindProof.Index unwind)
    {
        var app = method.AppContext;
        var start = method.UnderlyingPointer;
        if (start == 0 || native[0].IP != start ||
            (ulong)method.RawBytes.Length != native[^1].NextIP - start ||
            method.RawBytes.Length is not (7 or 8) ||
            native[^1].NextIP > ulong.MaxValue - 15)
            return false;

        var end = native[^1].NextIP;
        var alignedEnd = (end + 15) & ~15UL;
        if (alignedEnd <= end || alignedEnd - start > 16 ||
            app.GetAddressOfNextFunctionStart(start) != alignedEnd ||
            unwind.ClassifySpan(start, alignedEnd).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, alignedEnd) ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(
                pe, unwind, method.RawBytes.AsSpan(), start) ||
            start < unwind.ImageBase ||
            alignedEnd - unwind.ImageBase > uint.MaxValue ||
            Enumerable.Range(1, checked((int)(alignedEnd - start) - 1)).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            X86CallerExceptionRegionProof.Check(method, native,
                new HashSet<ulong>()) != null)
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        return rawStart >= 0 && rawStart <= image.Length - (long)(alignedEnd - start) &&
               Enumerable.Range(0, checked((int)(alignedEnd - start))).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start + (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) == rawStart + offset);
    }
}
