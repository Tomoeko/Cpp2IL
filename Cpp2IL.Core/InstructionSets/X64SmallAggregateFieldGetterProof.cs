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

/// <summary>Proves a byte/word aggregate argument projected by a complete register-only leaf.</summary>
internal static class X64SmallAggregateFieldGetterProof
{
    internal const string EvidenceKey = "X64SmallAggregateFieldGetterProof";
    internal const string CaptureRegister = "small_aggregate_field_capture";
    internal const string ResultRegister = "small_aggregate_field_result";

    internal sealed record Shape(NativeInstruction Load, NativeInstruction Return, int Width, bool Signed);

    internal sealed class InputState(List<object> metadata, byte[] nativeBytes)
    {
        private readonly object[] _metadata = metadata.ToArray();
        private readonly byte[] _nativeBytes = nativeBytes;

        internal bool Matches(InputState other) => _metadata.SequenceEqual(other._metadata) &&
            _nativeBytes.SequenceEqual(other._nativeBytes);
    }

    internal sealed record Proof(Shape Native, ParameterAnalysisContext Parameter,
        FieldAnalysisContext Field, bool Widen, InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && Widen == other.Widen &&
            ReferenceEquals(Parameter, other.Parameter) && ReferenceEquals(Field, other.Field) &&
            Input.Matches(other.Input);
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
        // This is the proved managed field projection of the by-value aggregate.
        // It does not assert that the native RCX bits are an address.
        var result = new List<IsilInstruction>
        {
            new(0, OpCode.Move, capture, new ISIL.MemoryOperand(new IsilRegister(null, "rcx"), null, 0))
                { NativeAddress = proof.Native.Load.IP }
        };
        if (proof.Widen)
        {
            var widened = new IsilRegister(null, ResultRegister);
            result.Add(new(1, OpCode.IntegerExtend, widened, capture, new Immediate(proof.Native.Width),
                new Immediate(32), new Immediate(proof.Native.Signed ? 1 : 0))
                { NativeAddress = proof.Native.Load.IP });
            result.Add(new(2, OpCode.Return, widened) { NativeAddress = proof.Native.Return.IP });
        }
        else
            result.Add(new(1, OpCode.Return, capture) { NativeAddress = proof.Native.Return.IP });
        return result;
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext: { Binary: PE } app, DeclaringType: { Definition: { } } owner,
                Definition: { GenericContainer: null, parameterCount: 1 } definition } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app))
            return null;
        try
        {
            if (!ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                definition.InternalParameterData is not [var originalParameter] ||
                method.Parameters is not [var parameter] || parameter.Definition != originalParameter ||
                parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
                parameter.IsRef || parameter.OverrideParameterType != null || parameter.Name != parameter.DefaultName ||
                parameter.Attributes != parameter.DefaultAttributes || parameter.UseOverrideDefaultValue ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                originalParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0, Data: not null } rawParameter ||
                !ReferenceEquals(app.ResolveIl2CppType(rawParameter), parameter.ParameterType) ||
                !method.IsStatic || method.IsVirtual || method.IsVoid || method.Name is ".ctor" or ".cctor" ||
                method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
                method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                          MethodImplAttributes.InternalCall)) != 0 ||
                method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
                definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } rawReturn ||
                rawReturn.Type != method.ReturnType.Type || method.BaseMethod != null || method.Overrides.Count != 0 ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
                AggregateField(parameter.ParameterType, method) is not { } field ||
                !OrdinaryOwner(owner, parameter.ParameterType) || !OriginalAbi(method) ||
                ReadBody(method) is not { } body || TryProveShape(body) is not { } shape ||
                IntegerExtension.StorageBits(field.FieldType, app.SystemTypes) != shape.Width)
                return null;

            var returnBits = IntegerExtension.StorageBits(method.ReturnType, app.SystemTypes);
            var widen = returnBits == 32;
            if (returnBits == shape.Width)
            {
                // The declared narrow return observes only the low bits. A native
                // MOVZX can therefore implement an evidenced signed narrow return.
                if (!ReferenceEquals(method.ReturnType, field.FieldType))
                    return null;
            }
            else if (!widen || shape.Signed != IsSigned(field.FieldType, app.SystemTypes) ||
                     !ReferenceEquals(method.ReturnType, shape.Signed
                         ? app.SystemTypes.SystemInt32Type : app.SystemTypes.SystemUInt32Type))
                return null;

            var values = new List<object>();
            CaptureType(owner, values);
            if (!ReferenceEquals(owner, parameter.ParameterType))
                CaptureType(parameter.ParameterType, values);
            if (!X64ScalarWrapperInitializationProof.TryCapture(parameter.ParameterType, field, values))
                return null;
            CaptureMethod(method, values);
            values.Add(parameter);
            values.Add(parameter.Name);
            values.Add(parameter.Attributes);
            values.Add(parameter.ParameterIndex);
            values.Add(parameter.ParameterType);
            values.Add(originalParameter.nameIndex);
            values.Add(originalParameter.token);
            values.Add(originalParameter.typeIndex);
            CaptureRawType(rawParameter, values);
            CaptureRawType(rawReturn, values);
            var length = checked((int)(shape.Return.NextIP - method.UnderlyingPointer));
            return new Proof(shape, parameter, field, widen,
                new InputState(values, method.RawBytes.AsSpan().Slice(0, length).ToArray()));
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
            body.Count == 3 && body[0] is not
                { Code: Code.Nopw or Code.Nopd, OpCount: 0, FlowControl: FlowControl.Next })
            return null;
        var load = body[^2];
        var ret = body[^1];
        var width = load.Code switch
        {
            Code.Movzx_r32_rm8 or Code.Movsx_r32_rm8 => 8,
            Code.Movzx_r32_rm16 or Code.Movsx_r32_rm16 => 16,
            _ => 0
        };
        if (width == 0 || load.OpCount != 2 || load.Op0Kind != OpKind.Register ||
            load.Op0Register != NativeRegister.EAX || load.Op1Kind != OpKind.Register ||
            load.Op1Register != (width == 8 ? NativeRegister.CL : NativeRegister.CX) ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new Shape(load, ret, width, load.Mnemonic == Mnemonic.Movsx);
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method) =>
        X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) ??
        X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32);

    internal static bool OriginalAbi(MethodAnalysisContext method)
    {
        var resolver = new X64CallingConventionResolver();
        return !resolver.ReturnsViaHiddenBuffer(method) && resolver.ResolveForParameters(method) is
            [IsilRegister { Name: "rcx", Version: -1 } first,
             IsilRegister { Name: "rdx", Version: -1 } metadata] &&
            first == new IsilRegister(null, "rcx") && metadata == new IsilRegister(null, "rdx");
    }

    internal static FieldAnalysisContext? AggregateField(TypeAnalysisContext aggregate, MethodAnalysisContext method)
    {
        if (!OrdinaryType(aggregate, allowInitializer: true) || aggregate is GenericInstanceTypeAnalysisContext ||
            aggregate.Definition is not { IsValueType: true, IsEnumType: false, IsBlittable: true,
                IsImportOrWindowsRuntime: false, IsByRefLike: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE } } ||
            (aggregate.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(aggregate.BaseType, aggregate.AppContext.SystemTypes.SystemValueTypeType) ||
            aggregate.Fields.Where(field => !field.IsStatic).ToArray() is not [var field] ||
            field.Offset != 0 || field.Offset != field.DefaultOffset || field.OverrideFieldType != null ||
            !ReferenceEquals(field.DeclaringType, aggregate) ||
            !ReferenceEquals(field.BackingData?.Field.DeclaringType, aggregate.Definition) ||
            (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
            field.Visibility != FieldAttributes.Public && !ReferenceEquals(aggregate, method.DeclaringType) ||
            field.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } raw ||
            raw.Type != field.FieldType.Type ||
            IntegerExtension.StorageBits(field.FieldType, aggregate.AppContext.SystemTypes) is not (8 or 16) ||
            TypeSizes.UnboxedSize(aggregate, 8) != IntegerExtension.StorageBits(field.FieldType,
                aggregate.AppContext.SystemTypes) / 8 ||
            aggregate.Definition.RawSizes.native_size != TypeSizes.UnboxedSize(aggregate, 8))
            return null;
        return field;
    }

    private static bool OrdinaryOwner(TypeAnalysisContext owner, TypeAnalysisContext aggregate) =>
        OrdinaryType(owner, allowInitializer: ReferenceEquals(owner, aggregate)) && (ReferenceEquals(owner, aggregate) ||
            !owner.IsValueType && !owner.IsInterface &&
            owner.Definition!.RawType.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
            ReferenceEquals(owner.BaseType, owner.AppContext.SystemTypes.SystemObjectType));

    private static bool OrdinaryType(TypeAnalysisContext type, bool allowInitializer = false) =>
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } } definition &&
        (allowInitializer || !definition.HasCctor) &&
        !type.IsGenericInstance && type.GenericParameters.Count == 0 &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        type.Fields.Count == definition.FieldCount && type.Fields.All(field =>
            field.BackingData?.Field.RawFieldType is { Data: not null } &&
            field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
            field.Offset == field.DefaultOffset && field.OverrideFieldType == null) &&
        type.Methods.Count == definition.MethodCount && type.Methods.All(method => method.Definition != null) &&
        (allowInitializer || !type.Methods.Any(method => method.Name == ".cctor"));

    private static bool IsSigned(TypeAnalysisContext type, SystemTypesContext types) =>
        ReferenceEquals(type, types.SystemSByteType) || ReferenceEquals(type, types.SystemInt16Type);

    internal static void CaptureType(TypeAnalysisContext type, List<object> values)
    {
        var definition = type.Definition!;
        values.Add(type);
        values.Add(type.Name);
        values.Add(type.Namespace);
        values.Add(type.Attributes);
        values.Add(type.BaseType!);
        values.Add(type.DeclaringType!);
        values.Add(definition.NameIndex);
        values.Add(definition.NamespaceIndex);
        values.Add(definition.Token);
        values.Add(definition.Flags);
        values.Add(definition.Bitfield);
        values.Add(definition.ByvalTypeIndex);
        values.Add(definition.ParentIndex);
        values.Add(definition.DeclaringTypeIndex);
        values.Add(definition.GenericContainerIndex);
        values.Add(definition.FirstFieldIdx);
        values.Add(definition.FieldCount);
        values.Add(definition.FirstMethodIdx);
        values.Add(definition.MethodCount);
        var sizes = definition.RawSizes;
        values.Add(sizes.instance_size);
        values.Add(sizes.native_size);
        values.Add(sizes.static_fields_size);
        values.Add(sizes.thread_static_fields_size);
        CaptureRawType(definition.RawType, values);
        foreach (var field in type.Fields)
        {
            values.Add(field);
            values.Add(field.Name);
            values.Add(field.Attributes);
            values.Add(field.Offset);
            values.Add(field.FieldType.FullName);
            values.Add(field.FieldType.Type);
            var original = field.BackingData!.Field;
            values.Add(original.nameIndex);
            values.Add(original.token);
            values.Add(original.typeIndex);
            CaptureRawType(original.RawFieldType!, values);
        }
    }

    internal static void CaptureMethod(MethodAnalysisContext method, List<object> values)
    {
        var definition = method.Definition!;
        values.Add(method);
        values.Add(method.UnderlyingPointer);
        values.Add(method.Name);
        values.Add(method.Attributes);
        values.Add(method.ImplAttributes);
        values.Add(method.ReturnType);
        values.Add(definition.nameIndex);
        values.Add(definition.token);
        values.Add(definition.flags);
        values.Add(definition.iflags);
        values.Add(definition.declaringTypeIdx);
        values.Add(definition.returnTypeIdx);
        values.Add(definition.parameterStart);
        values.Add(definition.parameterCount);
        values.Add(definition.genericContainerIndex);
        values.Add(definition.slot);
    }

    internal static void CaptureRawType(Il2CppType raw, List<object> values)
    {
        values.Add(raw.Bits);
        values.Add(raw.Datapoint);
        values.Add(raw.Data.Dummy);
        values.Add(raw.Attrs);
        values.Add(raw.Type);
        values.Add(raw.NumMods);
        values.Add(raw.Byref);
        values.Add(raw.Pinned);
        values.Add(raw.ValueType);
    }
}
