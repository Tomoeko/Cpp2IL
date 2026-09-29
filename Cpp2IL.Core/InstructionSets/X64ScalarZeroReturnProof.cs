using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A complete x64 leaf that clears every bit of XMM0 and returns. Its low lane
/// is positive zero for either unchanged scalar floating-point return type.
/// </summary>
internal static class X64ScalarZeroReturnProof
{
    private static ReadOnlySpan<byte> BodyBytes => [0x0F, 0x57, 0xC0, 0xC3];

    internal static List<ManagedInstruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Iced.Intel.Instruction> native)
    {
        var app = method.AppContext;
        var returnType = method.ReturnType;
        var single = ReferenceEquals(returnType, app.SystemTypes.SystemSingleType);
        var doublePrecision = ReferenceEquals(returnType, app.SystemTypes.SystemDoubleType);
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            !(single || doublePrecision) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: var rawReturn,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            rawReturn != (single ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
            !ReferenceEquals(returnType, method.DefaultReturnType) || method.OverrideReturnType != null ||
            !method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters.Count != 0 ||
            method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer == 0 ||
            !MatchesBody(native, method.UnderlyingPointer) ||
            method.RawBytes.Length != BodyBytes.Length ||
            !method.RawBytes.AsSpan().SequenceEqual(BodyBytes) ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan(), method.UnderlyingPointer) ||
            Enumerable.Range(1, BodyBytes.Length - 1).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)) ||
            unwind.ClassifySpan(method.UnderlyingPointer, native[^1].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry,
                    Start: var start, End: var end } ||
            start != method.UnderlyingPointer || end != native[^1].NextIP ||
            X86CallerExceptionRegionProof.Check(method, native, new HashSet<ulong>()) != null)
            return null;

        IOperand value = single ? new FloatLiteral(0f) : new DoubleLiteral(0d);
        return [new ManagedInstruction(0, OpCode.Return, value) { NativeAddress = native[^1].IP }];
    }

    internal static bool MatchesBody(IReadOnlyList<Iced.Intel.Instruction> native, ulong start) =>
        native is [{ } clear, { } ret] &&
        clear.IP == start && clear.NextIP == ret.IP && ret.NextIP == start + 4 &&
        clear.CodeSize == CodeSize.Code64 && ret.CodeSize == CodeSize.Code64 &&
        clear.Code == Code.Xorps_xmm_xmmm128 && clear.OpCount == 2 &&
        clear.Op0Kind == OpKind.Register && clear.Op1Kind == OpKind.Register &&
        clear.Op0Register == NativeRegister.XMM0 && clear.Op1Register == NativeRegister.XMM0 &&
        ret.Code == Code.Retnq && ret.OpCount == 0 &&
        !clear.HasLockPrefix && !clear.HasRepPrefix && !clear.HasRepnePrefix &&
        clear.SegmentPrefix == NativeRegister.None &&
        !ret.HasLockPrefix && !ret.HasRepPrefix && !ret.HasRepnePrefix &&
        ret.SegmentPrefix == NativeRegister.None;
}
