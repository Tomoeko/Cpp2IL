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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A closed Windows x64 scalar-float contract: one leaf zeros a three-float
/// byref value and returns its float argument; its caller snapshots that value,
/// calls the leaf on its own by-value copy, then adds the snapshots in order.
/// The full-width MOVAPS transfers are used only for their proved low float lane.
/// </summary>
internal static class X64ScalarFloatRefMutationProof
{
    internal sealed record Leaf(TypeAnalysisContext ValueType, FieldAnalysisContext[] Fields);
    internal sealed record Caller(MethodAnalysisContext Target, FieldAnalysisContext[] Fields);

    internal static Leaf? FindLeaf(MethodAnalysisContext method)
    {
        if (method.Definition is not
                { parameterCount: 2, InternalParameterData: [var byRef, var value] } ||
            !OrdinaryMethod(method) ||
            method.Parameters is not [var byRefParameter, var valueParameter] ||
            !UnchangedParameter(method, byRefParameter, byRef, 0) ||
            !UnchangedParameter(method, valueParameter, value, 1) ||
            byRef.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 1, Pinned: 0 } ||
            byRefParameter.ParameterType is not ByRefTypeAnalysisContext { ElementType: var valueType } ||
            !ThreeFloatFields(valueType, out var fields) ||
            value.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(valueParameter.ParameterType, method.AppContext.SystemTypes.SystemSingleType) ||
            !TryReadCompleteBody(method, 5, X64UnwindProof.SpanKind.NoEntry,
                out var body, out _) ||
            !LeafShape(body) ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;

        return new Leaf(valueType, fields!);
    }

    internal static Caller? FindCaller(MethodAnalysisContext method)
    {
        if (method.Definition is not
                { parameterCount: 2, InternalParameterData: [var first, var value] } ||
            !OrdinaryMethod(method) ||
            method.Parameters is not [var firstParameter, var valueParameter] ||
            !UnchangedParameter(method, firstParameter, first, 0) ||
            !UnchangedParameter(method, valueParameter, value, 1) ||
            first.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(firstParameter.ParameterType, method.AppContext.SystemTypes.SystemSingleType) ||
            value.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ThreeFloatFields(valueParameter.ParameterType, out var fields) ||
            !TryReadCompleteBody(method, 19, X64UnwindProof.SpanKind.HandlerFree,
                out var body, out var unwind) ||
            !CallerShape(body))
            return null;

        var app = method.AppContext;
        if (!app.MethodsByAddress.TryGetValue(body[10].NearBranchTarget, out var bindings) ||
            bindings is not [var target] || ReferenceEquals(target, method) ||
            !ReferenceEquals(target.DeclaringType, method.DeclaringType) ||
            FindLeaf(target) is not { } leaf ||
            !ReferenceEquals(leaf.ValueType, valueParameter.ParameterType) ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;

        // The general ABI-strip gate deliberately rejects byref call targets.
        // Here the only call is the separately proved leaf, so the lower-level
        // unwind/slot proof may authenticate exactly these six save/restores.
        var preserved = X86NonvolatileXmmStackProof.Prove(body, method.UnderlyingPointer,
            unwind!, instruction => instruction.IP == body[10].IP &&
                instruction.NearBranchTarget == target.UnderlyingPointer);
        return preserved.SetEquals(new[]
            { body[1].IP, body[5].IP, body[8].IP, body[12].IP, body[14].IP, body[16].IP })
            ? new Caller(target, fields!) : null;
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return X86RuntimeNullThrowProof.IsSupportedProfile(app) &&
            method.DeclaringType is { Definition: { GenericContainer: null, HasCctor: false } } owner &&
            owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
            owner.Attributes == owner.DefaultAttributes && owner.GenericParameters.Count == 0 &&
            !owner.IsGenericInstance &&
            method.Definition is { GenericContainer: null,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
            !definition.IsUnmanagedCallersOnly &&
            ReferenceEquals(definition.DeclaringType, owner.Definition) &&
            method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
            method.IsStatic && !method.IsVirtual && method.GenericParameters.Count == 0 &&
            method.Attributes == method.DefaultAttributes &&
            method.ImplAttributes == method.DefaultImplAttributes &&
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0 &&
            method.OverrideReturnType == null &&
            ReferenceEquals(method.ReturnType, app.SystemTypes.SystemSingleType) &&
            !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
            method.UnderlyingPointer != 0 &&
            app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) &&
            bindings is [var bound] && ReferenceEquals(bound, method);
    }

    private static bool UnchangedParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition raw, int index) =>
        parameter.ParameterIndex == index && ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition, raw) && parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes && parameter.OverrideParameterType == null &&
        parameter.OverrideAttributes == null && !parameter.UseOverrideDefaultValue;

    private static bool ThreeFloatFields(TypeAnalysisContext type, out FieldAnalysisContext[]? fields)
    {
        fields = null;
        if (type.Definition is not { GenericContainer: null, HasCctor: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            !type.IsValueType || type.IsEnumType || type.IsGenericInstance ||
            type.GenericParameters.Count != 0 || type.Name != type.DefaultName ||
            type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            TypeSizes.UnboxedSize(type, 8) != 12 || type.Fields.Count != 3)
            return false;

        var ordered = type.Fields.OrderBy(field => field.Offset).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var field = ordered[index];
            if (field.IsStatic || field.Offset != index * 4 || field.Offset != field.DefaultOffset ||
                field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                field.OverrideFieldType != null || field.UseOverrideConstantValue ||
                (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public ||
                (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0 ||
                !ReferenceEquals(field.FieldType, type.AppContext.SystemTypes.SystemSingleType) ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4, NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
        }
        fields = ordered;
        return true;
    }

    private static bool TryReadCompleteBody(MethodAnalysisContext method, int count,
        X64UnwindProof.SpanKind kind, out NativeInstruction[] body,
        out X64UnwindProof.Index? unwind)
    {
        body = [];
        unwind = null;
        if (method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } index)
            return false;
        unwind = index;
        method.EnsureRawBytes();
        if (method.RawBytes.Length is < 1 or > 128 ||
            method.UnderlyingPointer > ulong.MaxValue - (ulong)method.RawBytes.Length)
            return false;
        var end = method.UnderlyingPointer + (ulong)method.RawBytes.Length;
        var region = index.ClassifySpan(method.UnderlyingPointer, end);
        if (region.Kind != kind || region.Start != method.UnderlyingPointer ||
            region.End != end || kind == X64UnwindProof.SpanKind.HandlerFree &&
            region.RootStart != method.UnderlyingPointer ||
            method.UnderlyingPointer < index.ImageBase ||
            end - 1 - index.ImageBase > uint.MaxValue ||
            Enumerable.Range(0, method.RawBytes.Length).Any(offset =>
                !index.IsExecutableRva(checked((uint)(method.UnderlyingPointer + (ulong)offset -
                    index.ImageBase)))) ||
            Enumerable.Range(1, method.RawBytes.Length - 1).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false);
        var rawEnd = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawEnd < rawStart || rawEnd >= image.Length ||
            (ulong)(rawEnd - rawStart) != end - method.UnderlyingPointer - 1 ||
            Enumerable.Range(0, method.RawBytes.Length).Any(offset =>
                pe.MapVirtualAddressToRaw(method.UnderlyingPointer + (ulong)offset, false) !=
                rawStart + offset) ||
            !image.Slice(checked((int)rawStart), method.RawBytes.Length).SequenceEqual(method.RawBytes.AsSpan()))
            return false;

        body = X86Utils.Iterate(method).ToArray();
        var decoded = body;
        return body.Length == count && body[0].IP == method.UnderlyingPointer &&
            body[^1].NextIP == end && body[^1].Code == Code.Retnq && body[^1].OpCount == 0 &&
            body.All(instruction => !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
                !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
                !instruction.HasRepnePrefix && instruction.SegmentPrefix == NativeRegister.None) &&
            !decoded.Where((instruction, index) => index > 0 &&
                instruction.IP != decoded[index - 1].NextIP).Any();
    }

    internal static bool LeafShape(IReadOnlyList<NativeInstruction> body) =>
        body.Count == 5 && WellFormed(body) &&
        Zero(body[0], NativeRegister.EAX) &&
        VectorMove(body[1], NativeRegister.XMM0, NativeRegister.XMM1) &&
        MemoryStore(body[2], NativeRegister.RCX, 0, NativeRegister.RAX, 8) &&
        MemoryStore(body[3], NativeRegister.RCX, 8, NativeRegister.EAX, 4) &&
        body[4].Code == Code.Retnq && body[4].OpCount == 0;

    internal static bool CallerShape(IReadOnlyList<NativeInstruction> body) =>
        body.Count == 19 && WellFormed(body) &&
        Stack(body[0], Mnemonic.Sub) &&
        VectorStack(body[1], NativeRegister.XMM6, 0x40, save: true) &&
        Zero(body[2], NativeRegister.R8D) &&
        ScalarLoad(body[3], NativeRegister.XMM6, 0) &&
        VectorMove(body[4], NativeRegister.XMM1, NativeRegister.XMM0) &&
        VectorStack(body[5], NativeRegister.XMM7, 0x30, save: true) &&
        RegisterMove(body[6], NativeRegister.RCX, NativeRegister.RDX) &&
        ScalarLoad(body[7], NativeRegister.XMM7, 4) &&
        VectorStack(body[8], NativeRegister.XMM8, 0x20, save: true) &&
        ScalarLoad(body[9], NativeRegister.XMM8, 8) &&
        body[10].Code == Code.Call_rel32_64 && body[10].OpCount == 1 &&
        body[10].Op0Kind == OpKind.NearBranch64 &&
        ScalarAdd(body[11], NativeRegister.XMM6) &&
        VectorStack(body[12], NativeRegister.XMM6, 0x40, save: false) &&
        ScalarAdd(body[13], NativeRegister.XMM7) &&
        VectorStack(body[14], NativeRegister.XMM7, 0x30, save: false) &&
        ScalarAdd(body[15], NativeRegister.XMM8) &&
        VectorStack(body[16], NativeRegister.XMM8, 0x20, save: false) &&
        Stack(body[17], Mnemonic.Add) &&
        body[18].Code == Code.Retnq && body[18].OpCount == 0;

    private static bool WellFormed(IReadOnlyList<NativeInstruction> body) =>
        body.All(instruction => !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
            !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
            !instruction.HasRepnePrefix && instruction.SegmentPrefix == NativeRegister.None) &&
        body.Where((instruction, index) => index > 0 &&
            instruction.IP != body[index - 1].NextIP).Any() == false;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Xor_r32_rm32 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register;

    private static bool VectorMove(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) =>
        instruction.Mnemonic == Mnemonic.Movaps && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool RegisterMove(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool MemoryStore(NativeInstruction instruction, NativeRegister address,
        ulong offset, NativeRegister source, int width) =>
        instruction.Mnemonic == Mnemonic.Mov && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == address &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == width &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool ScalarLoad(NativeInstruction instruction, NativeRegister destination,
        ulong offset) =>
        instruction.Code == Code.Movss_xmm_xmmm32 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == 4;

    private static bool ScalarAdd(NativeInstruction instruction, NativeRegister source) =>
        instruction.Mnemonic == Mnemonic.Addss && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.XMM0 &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool VectorStack(NativeInstruction instruction, NativeRegister register,
        ulong offset, bool save) =>
        instruction.Mnemonic == Mnemonic.Movaps && instruction.OpCount == 2 &&
        instruction.GetOpKind(save ? 0 : 1) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RSP && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == 16 &&
        instruction.GetOpKind(save ? 1 : 0) == OpKind.Register &&
        instruction.GetOpRegister(save ? 1 : 0) == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic operation) =>
        instruction.Mnemonic == operation && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x58;
}
