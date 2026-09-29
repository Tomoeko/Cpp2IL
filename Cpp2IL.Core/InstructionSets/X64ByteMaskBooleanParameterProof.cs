using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete x64 leaf that masks bit zero of an unsigned-byte enum
/// argument and returns the resulting canonical Boolean byte. The AND's
/// partial DL write is safe here because the only later read is DL itself.
/// </summary>
internal static class X64ByteMaskBooleanParameterProof
{
    private static ReadOnlySpan<byte> BodyBytes => [0x80, 0xE2, 0x01, 0x0F, 0xB6, 0xC2, 0xC3];

    internal readonly record struct Shape(ulong End);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Instruction> native)
    {
        if (!Find(method, native))
            return null;

        var byteValue = new ISIL.Register(null, "byte_mask_argument");
        var masked = new ISIL.Register(null, "byte_mask_bit_zero");
        var result = new ISIL.Register(null, "byte_mask_result");
        return
        [
            // The ABI supplies the enum in RDX. Truncate before the mask so
            // unspecified upper argument bits cannot enter managed arithmetic.
            new(0, ISIL.OpCode.IntegerExtend, byteValue, new ISIL.Register(null, "rdx"),
                new ISIL.Immediate(8), new ISIL.Immediate(32), new ISIL.Immediate(0))
                { NativeAddress = native[0].IP },
            new(1, ISIL.OpCode.And, masked, byteValue, new ISIL.Immediate(1))
                { IntegerBitWidth = 32, NativeAddress = native[0].IP },
            new(2, ISIL.OpCode.CheckNotEqual, result, masked, new ISIL.Immediate(0))
                { IntegerBitWidth = 32, NativeAddress = native[1].IP },
            new(3, ISIL.OpCode.Return, result) { NativeAddress = native[2].IP },
        ];
    }

    internal static bool Find(MethodAnalysisContext method, IReadOnlyList<Instruction> native)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            !EligibleMethod(method) ||
            TryProveShape(native.Take(3).ToArray()) is not { } shape ||
            method.UnderlyingPointer == 0 || native[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return false;

        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        if (shape.End <= start || shape.End - start != (ulong)BodyBytes.Length ||
            method.RawBytes.Length < BodyBytes.Length ||
            !method.RawBytes.AsSpan()[..BodyBytes.Length].SequenceEqual(BodyBytes) ||
            start > ulong.MaxValue - (ulong)method.RawBytes.Length ||
            TryProveLeafExtent(native, shape.End,
                start + (ulong)method.RawBytes.Length, unwind) is not { } extent ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan()[..checked((int)(extent - start))], start) ||
            !X64NativePaddingProof.HasInt3Padding(pe, shape.End, extent) ||
            Enumerable.Range(1, checked((int)(extent - start) - 1)).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            X86CallerExceptionRegionProof.Check(method, native.Take(3).ToArray(),
                new HashSet<ulong>()) != null)
            return false;
        return true;
    }

    // A metadata-estimated span may include another unmanaged function. Admit
    // only decoded INT3 alignment followed by its independently indexed .pdata
    // entry; the adjacent function's instructions are never part of this leaf.
    internal static ulong? TryProveLeafExtent(IReadOnlyList<Instruction> native,
        ulong bodyEnd, ulong rawEnd, X64UnwindProof.Index unwind)
    {
        if (native.Count < 3 || native[2].NextIP != bodyEnd || rawEnd < bodyEnd)
            return null;
        var index = 3;
        var extent = bodyEnd;
        while (index < native.Count && native[index].Code == Code.Int3)
        {
            var padding = native[index];
            if (padding.IP != extent || padding.Length != 1 || padding.OpCount != 0 ||
                padding.IsInvalid || padding.CodeSize != CodeSize.Code64 ||
                padding.HasLockPrefix || padding.HasRepPrefix || padding.HasRepnePrefix ||
                padding.SegmentPrefix != Register.None || extent - bodyEnd >= 15)
                return null;
            extent = padding.NextIP;
            index++;
        }
        if (extent > rawEnd || unwind.ClassifySpan(native[0].IP, extent) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var regionStart,
                    End: var regionEnd } ||
            regionStart != native[0].IP || regionEnd != extent)
            return null;
        if (extent == rawEnd)
            return index == native.Count ? extent : null;
        return index > 3 && index < native.Count &&
               native[index] is { IsInvalid: false, CodeSize: CodeSize.Code64 } next &&
               next.IP == extent && next.NextIP > extent && next.NextIP <= rawEnd &&
               unwind.HasFunctionEntryAt(extent, next.NextIP) ? extent : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> native)
    {
        if (native is not [{ } mask, { } widen, { } ret] ||
            native.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            mask.Code != Code.And_rm8_imm8 || mask.Length != 3 ||
            mask.OpCount != 2 || mask.Op0Kind != OpKind.Register ||
            mask.Op0Register != Register.DL ||
            mask.Op1Kind != OpKind.Immediate8 || mask.Immediate8 != 1 ||
            mask.FlowControl != FlowControl.Next ||
            widen.IP != mask.NextIP ||
            widen.Code != Code.Movzx_r32_rm8 || widen.Length != 3 ||
            widen.OpCount != 2 || widen.Op0Kind != OpKind.Register ||
            widen.Op0Register != Register.EAX ||
            widen.Op1Kind != OpKind.Register || widen.Op1Register != Register.DL ||
            widen.FlowControl != FlowControl.Next ||
            ret.IP != widen.NextIP || ret.Code != Code.Retnq ||
            ret.Length != 1 || ret.OpCount != 0)
            return null;
        return new Shape(ret.NextIP);
    }

    private static bool EligibleMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            owner.Definition.HasCctor || owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var first, var second] ||
            method.Parameters is not [var firstParameter, var secondParameter] ||
            !UnchangedParameter(method, firstParameter, first, 0) ||
            !UnchangedParameter(method, secondParameter, second, 1) ||
            first.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(firstParameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
            second.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ISIL.IntegerExtension.IsCanonicalUnsignedByteEnum(secondParameter.ParameterType,
                app.SystemTypes) ||
            !method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0)
            return false;
        return true;
    }

    private static bool UnchangedParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter,
        LibCpp2IL.Metadata.Il2CppParameterDefinition definition, int index) =>
        parameter.ParameterIndex == index &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition, definition) &&
        !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.Name == parameter.DefaultName &&
        parameter.OverrideParameterType == null &&
        parameter.OverrideAttributes == null &&
        !parameter.UseOverrideDefaultValue &&
        ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType);
}
