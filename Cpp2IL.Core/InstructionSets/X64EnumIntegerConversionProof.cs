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
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates one complete native extension of an original narrow enum argument.</summary>
internal static class X64EnumIntegerConversionProof
{
    internal const string EvidenceKey = "X64EnumIntegerConversionProof";
    internal const string CaptureRegister = "enum_integer_native_result";
    internal const string ResultRegister = "enum_integer_return_result";
    internal sealed record Shape(NativeInstruction Extension, NativeInstruction Return, int Width, bool Signed);
    internal sealed class InputState(List<object> values, byte[] bytes)
    {
        private readonly object[] _values = values.ToArray();
        private readonly byte[] _bytes = bytes;
        internal bool Matches(InputState other) => _values.SequenceEqual(other._values) && _bytes.SequenceEqual(other._bytes);
    }
    internal sealed record Proof(Shape Native, EnumIntegerStorageProof.Storage Storage, bool ReturnSigned, InputState Input)
    {
        internal bool ConvertSign => Native.Signed != ReturnSigned;
        internal bool Matches(Proof other) => Native == other.Native && Storage == other.Storage &&
            ReturnSigned == other.ReturnSigned && Input.Matches(other.Input);
    }
    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var capture = new IsilRegister(null, CaptureRegister);
        var result = new List<IsilInstruction>
        {
            new(0, OpCode.IntegerExtend, capture, new IsilRegister(null, "rcx"),
                new Immediate(proof.Native.Width), new Immediate(32), new Immediate(proof.Native.Signed ? 1 : 0))
                { NativeAddress = proof.Native.Extension.IP }
        };
        var returned = capture;
        if (proof.ConvertSign)
        {
            returned = new IsilRegister(null, ResultRegister);
            // MOVSX/MOVZX establishes the first result independently of the
            // return type. The second explicit conversion preserves its low32.
            result.Add(new(1, OpCode.IntegerExtend, returned, capture, new Immediate(32),
                new Immediate(32), new Immediate(proof.ReturnSigned ? 1 : 0))
                { NativeAddress = proof.Native.Extension.IP });
        }
        result.Add(new(result.Count, OpCode.Return, returned) { NativeAddress = proof.Native.Return.IP });
        return result;
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method == null)
            return null;
        try
        {
            if (!Signature(method) || EnumIntegerStorageProof.Find(method.Parameters[0].ParameterType) is not { } storage ||
                method.Parameters[0].Definition!.RawType!.Data.Dummy != storage.Enum.Definition!.RawType.Data.Dummy ||
                (X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) ??
                 X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32)) is not { } body ||
                TryProveShape(body) is not { } shape || storage.Width != shape.Width || storage.Signed != shape.Signed ||
                new X64CallingConventionResolver().ResolveForParameters(method) is not
                    [IsilRegister { Name: "rcx", Version: -1 }, IsilRegister { Name: "rdx", Version: -1 }])
                return null;
            var length = checked((int)(shape.Return.NextIP - method.UnderlyingPointer));
            return new Proof(shape, storage,
                ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type),
                new InputState(Snapshot(method, storage), method.RawBytes.AsSpan().Slice(0, length).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (2 or 3) || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            body.Count == 3 && body[0] is not { Code: Code.Nopw or Code.Nopd, OpCount: 0 })
            return null;
        var extension = body[^2];
        var ret = body[^1];
        var width = extension.Code switch
        {
            Code.Movsx_r32_rm8 or Code.Movzx_r32_rm8 => 8,
            Code.Movsx_r32_rm16 or Code.Movzx_r32_rm16 => 16,
            _ => 0,
        };
        if (width == 0 || extension.OpCount != 2 || extension.Op0Kind != OpKind.Register ||
            extension.Op0Register != NativeRegister.EAX || extension.Op1Kind != OpKind.Register ||
            extension.Op1Register != (width == 8 ? NativeRegister.CL : NativeRegister.CX) ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new Shape(extension, ret, width, extension.Mnemonic == Mnemonic.Movsx);
    }

    private static bool Signature(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE || !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                InternalParameterData: [var rawParameter] } definition ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4,
                NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.DeclaringType is not { Definition: { GenericContainer: null, HasCctor: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 },
                RawBaseType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || method.Parameters.Count != 1 ||
            !method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            (!ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type) &&
             !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemUInt32Type)) ||
            method.ReturnType.Type != rawReturn.Type || method.BaseMethod != null || method.Overrides.Count != 0 ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, method.AppContext.SystemTypes.SystemObjectType) ||
            method.UnderlyingPointer == 0 || !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var entries) ||
            entries.Count(candidate => ReferenceEquals(candidate, method)) != 1)
            return false;
        var parameter = method.Parameters[0];
        return ReferenceEquals(parameter.Definition, rawParameter) && ReferenceEquals(parameter.DeclaringMethod, method) &&
            parameter.ParameterIndex == 0 && parameter.OverrideParameterType == null &&
            ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) &&
            parameter.Name == parameter.DefaultName && parameter.Attributes == ParameterAttributes.None &&
            parameter.Attributes == parameter.DefaultAttributes && parameter.OverrideAttributes == null &&
            !parameter.UseOverrideDefaultValue;
    }

    private static List<object> Snapshot(MethodAnalysisContext method, EnumIntegerStorageProof.Storage storage)
    {
        var definition = method.Definition!;
        var owner = method.DeclaringType!;
        var type = owner.Definition!;
        var parameter = method.Parameters[0];
        var rawParameter = parameter.Definition!;
        var values = new List<object>
        {
            method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx, definition.declaringTypeIdx, definition.parameterStart, definition.parameterCount,
            owner, owner.Name, owner.Namespace, owner.Attributes, owner.BaseType!, type.NameIndex, type.NamespaceIndex,
            type.Token, type.Bitfield, type.ByvalTypeIndex, type.ParentIndex,
            parameter, parameter.Name, parameter.Attributes, parameter.ParameterType,
            rawParameter.nameIndex, rawParameter.token, rawParameter.typeIndex,
        };
        EnumIntegerStorageProof.CaptureRaw(type.RawType, values);
        EnumIntegerStorageProof.CaptureRaw(type.RawBaseType!, values);
        EnumIntegerStorageProof.CaptureRaw(definition.RawReturnType!, values);
        EnumIntegerStorageProof.CaptureRaw(rawParameter.RawType!, values);
        EnumIntegerStorageProof.Capture(storage, values);
        return values;
    }
}
