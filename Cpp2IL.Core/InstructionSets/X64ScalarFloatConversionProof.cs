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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates a complete scalar floating parameter-to-return width conversion leaf.</summary>
internal static class X64ScalarFloatConversionProof
{
    internal const string EvidenceKey = "X64ScalarFloatConversionProof";
    internal sealed record Shape(NativeInstruction Conversion, NativeInstruction Return, int SourceWidth, int ResultWidth);

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Shape>(EvidenceKey) != null;

    internal static List<ManagedInstruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> native)
    {
        if (TryProveShape(native) is not { } candidate || Find(method) is not { } proof || candidate != proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var result = new ManagedRegister(null, "scalar_float_conversion_result");
        return
        [
            new(0, OpCode.FloatConvert, result, new ManagedRegister(null, "xmm0"),
                new Immediate(proof.SourceWidth), new Immediate(proof.ResultWidth))
                { NativeAddress = proof.Conversion.IP },
            new(1, OpCode.Return, result) { NativeAddress = proof.Return.IP },
        ];
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
            method.GetExtraData<Shape>(EvidenceKey) is not { } admitted || Find(method) != admitted ||
            method.ControlFlowGraph?.Instructions.ToArray() is not
                [var conversion, { OpCode: OpCode.Return, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct } returned] ||
            !FloatConversion.TryGet(conversion, out var shape) || shape.SourceWidth != admitted.SourceWidth ||
            shape.ResultWidth != admitted.ResultWidth || conversion.NativeAddress != admitted.Conversion.IP ||
            returned.NativeAddress != admitted.Return.IP || conversion.Operands is not [LocalVariable destination,
                LocalVariable source, _, _] || returned.Operands is not [LocalVariable returnValue] ||
            !ReferenceEquals(destination, returnValue) || ReferenceEquals(destination, source) ||
            !ReferenceEquals(source.Type, shape.SourceType(method.AppContext.SystemTypes)) ||
            !ReferenceEquals(destination.Type, shape.ResultType(method.AppContext.SystemTypes)) ||
            !method.ParameterLocals.Contains(source) || LocalVariables.GetIncomingParameterIndex(method, source) != 0)
            return false;
        return true;
    }

    internal static Shape? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!Signature(method) ||
                (X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 16) ??
                 X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 16)) is not { } body ||
                TryProveShape(body) is not { } shape || body[0].IP != method.UnderlyingPointer ||
                !ReferenceEquals(method.Parameters[0].ParameterType, shape.SourceWidth == 32
                    ? method.AppContext.SystemTypes.SystemSingleType : method.AppContext.SystemTypes.SystemDoubleType) ||
                !ReferenceEquals(method.ReturnType, shape.ResultWidth == 32
                    ? method.AppContext.SystemTypes.SystemSingleType : method.AppContext.SystemTypes.SystemDoubleType) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
                return null;
            return shape;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool Signature(MethodAnalysisContext method)
    {
        var types = method.AppContext.SystemTypes;
        var sourceWidth = method.Parameters is [var value]
            ? ReferenceEquals(value.ParameterType, types.SystemSingleType) ? 32 :
                ReferenceEquals(value.ParameterType, types.SystemDoubleType) ? 64 : 0 : 0;
        var resultWidth = ReferenceEquals(method.ReturnType, types.SystemSingleType) ? 32 :
            ReferenceEquals(method.ReturnType, types.SystemDoubleType) ? 64 : 0;
        if (sourceWidth == 0 || resultWidth == 0 || sourceWidth == resultWidth ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.Attributes != owner.DefaultAttributes || owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            method.Definition is not { GenericContainer: null, parameterCount: 1, IsUnmanagedCallersOnly: false,
                InternalParameterData: [var rawParameter], RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0, Type: var rawReturn } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || method.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) || !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef || parameter.OverrideParameterType != null ||
            parameter.UseOverrideDefaultValue || parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            rawParameter.RawType is not { NumMods: 0, Byref: 0, Pinned: 0, Type: var rawSource } ||
            rawSource != (sourceWidth == 32 ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
            rawReturn != (resultWidth == 32 ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
            !method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            new X64CallingConventionResolver().ResolveForParameters(method) is not
                [ManagedRegister { Name: "xmm0" }, ManagedRegister { Name: "rdx" }])
            return false;
        return true;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (2 or 3)) return null;
        if (body.Count == 3 && body[0] is not { Code: Code.Nopw, Length: 2, OpCount: 0 }) return null;
        for (var index = 0; index < body.Count; index++)
            if (body[index].IsInvalid || body[index].CodeSize != CodeSize.Code64 || body[index].HasLockPrefix ||
                body[index].HasRepPrefix || body[index].HasRepnePrefix || body[index].SegmentPrefix != NativeRegister.None ||
                index > 0 && body[index].IP != body[index - 1].NextIP)
                return null;
        var conversion = body[^2];
        var returned = body[^1];
        if (conversion.Code is not (Code.Cvtss2sd_xmm_xmmm32 or Code.Cvtsd2ss_xmm_xmmm64) ||
            conversion.Length != 4 || conversion.FlowControl != FlowControl.Next || conversion.OpCount != 2 ||
            conversion.Op0Kind != OpKind.Register || conversion.Op1Kind != OpKind.Register ||
            conversion.Op0Register != NativeRegister.XMM0 || conversion.Op1Register != NativeRegister.XMM0 ||
            returned.Code != Code.Retnq || returned.Length != 1 || returned.OpCount != 0)
            return null;
        var sourceWidth = conversion.Code == Code.Cvtss2sd_xmm_xmmm32 ? 32 : 64;
        return new(conversion, returned, sourceWidth, sourceWidth == 32 ? 64 : 32);
    }
}
