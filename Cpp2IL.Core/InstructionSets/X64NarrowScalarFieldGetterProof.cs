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

/// <summary>Proves one complete byte/word field getter on an unchanged blittable struct.</summary>
internal static class X64NarrowScalarFieldGetterProof
{
    internal const string EvidenceKey = "X64NarrowScalarFieldGetterProof";
    internal const string CaptureRegister = "narrow_scalar_field_capture";
    internal const string ResultRegister = "narrow_scalar_field_result";
    internal sealed record Shape(NativeInstruction Load, NativeInstruction Return, int Offset, int Width, bool Signed);
    internal sealed record Proof(Shape Native, FieldAnalysisContext Field, bool Widen);

    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        var receiver = new IsilRegister(null, "rcx");
        var capture = new IsilRegister(null, CaptureRegister);
        var result = new List<IsilInstruction>
        {
            new(0, OpCode.Move, capture, new ISIL.MemoryOperand(receiver, null, proof.Native.Offset))
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
        if (method is not { AppContext: { } app, DeclaringType: { Definition: { } } owner,
                Definition: { GenericContainer: null, parameterCount: 0 } definition } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 || method.Parameters.Count != 0 ||
            method.IsStatic || method.IsVirtual || method.IsVoid || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            definition.RawReturnType.Type != method.ReturnType.Type ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            !HasUnchangedLayout(owner) || ReadBody(method, pe, unwind) is not { } body ||
            TryProveShape(body) is not { } shape)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.Offset).ToArray();
        if (fields is not [var field] ||
            IntegerExtension.StorageBits(field.FieldType, app.SystemTypes) != shape.Width)
            return null;
        var returnBits = IntegerExtension.StorageBits(method.ReturnType, app.SystemTypes);
        if (returnBits == shape.Width)
        {
            // Only the declared low return bits are observable in the scalar Win64 ABI.
            // MOVZX may implement a signed narrow return; the metadata supplies its signed type.
            return ReferenceEquals(method.ReturnType, field.FieldType) &&
                   definition.RawReturnType.Type == field.BackingData!.Field.RawFieldType!.Type
                ? new Proof(shape, field, false) : null;
        }
        if (returnBits != 32 || shape.Signed != IsSigned(field.FieldType, app.SystemTypes) ||
            shape.Signed && !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            !shape.Signed && !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemUInt32Type))
            return null;
        return new Proof(shape, field, true);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (2 or 3) || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any())
            return null;
        if (body.Count == 3 && body[0] is not
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
            load.Op0Register != NativeRegister.EAX || load.Op1Kind != OpKind.Memory ||
            load.MemoryBase != NativeRegister.RCX || load.MemoryIndex != NativeRegister.None ||
            load.MemoryIndexScale != 1 || load.MemorySize.GetSize() != width / 8 ||
            load.MemoryDisplacement64 >= 4096 || ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new Shape(load, ret, (int)load.MemoryDisplacement64, width, load.Mnemonic == Mnemonic.Movsx);
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method, PE pe, X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        if (start == 0 || method.RawBytes.Length == 0 ||
            X64NativeInstructionReader.Read(pe, unwind, start, 2, 32) is not { } initial)
            return null;
        var body = initial[0].Mnemonic == Mnemonic.Nop
            ? X64NativeInstructionReader.Read(pe, unwind, start, 3, 32)?.ToArray()
            : initial.ToArray();
        if (body == null || TryProveShape(body) is not { } shape)
            return null;
        var length = checked((int)(shape.Return.NextIP - start));
        if (method.RawBytes.Length < length ||
            unwind.ClassifySpan(start, shape.Return.NextIP) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var regionStart, End: var regionEnd } ||
            regionStart != start || regionEnd != shape.Return.NextIP ||
            method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < shape.Return.NextIP))
            return null;
        var offset = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - length)
            return null;
        var bytes = image.Slice(checked((int)offset), length);
        return bytes.SequenceEqual(method.RawBytes.AsSpan().Slice(0, length)) &&
               X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, bytes, start)
            ? body : null;
    }

    private static bool HasUnchangedLayout(TypeAnalysisContext owner)
    {
        var types = owner.AppContext.SystemTypes;
        if (owner is GenericInstanceTypeAnalysisContext || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace || owner.Attributes != owner.DefaultAttributes ||
            (owner.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, types.SystemValueTypeType) ||
            owner.Definition is not { IsValueType: true, IsEnumType: false, IsBlittable: true,
                IsImportOrWindowsRuntime: false, IsByRefLike: false, GenericContainer: null,
                HasCctor: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } } ||
            TypeSizes.UnboxedSize(owner, 8) is not (> 0 and <= 4096) ||
            owner.Methods.Any(method => method.Name == ".cctor"))
            return false;
        var fields = owner.Fields.Where(field => !field.IsStatic).ToArray();
        if (fields.Length == 0)
            return false;
        long end = 0;
        var alignment = 1;
        foreach (var field in fields)
        {
            var width = IntegerExtension.StorageBits(field.FieldType, types);
            if (width == 0 || !NarrowFieldEqualityProof.HasExactStorageWidth(field.FieldType, width) ||
                field.Name != field.DefaultName || field.Offset != field.DefaultOffset ||
                field.Attributes != field.DefaultAttributes || field.OverrideFieldType != null ||
                !ReferenceEquals(field.DeclaringType, owner) ||
                !ReferenceEquals(field.BackingData?.Field.DeclaringType, owner.Definition) ||
                (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
                field.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } raw ||
                raw.Type != field.FieldType.Type)
                return false;
            var size = width / 8;
            end = (end + size - 1) / size * size;
            if (field.Offset != end)
                return false;
            end += size;
            alignment = Math.Max(alignment, size);
        }
        return (end + alignment - 1) / alignment * alignment == TypeSizes.UnboxedSize(owner, 8);
    }

    private static bool IsSigned(TypeAnalysisContext type, SystemTypesContext types) =>
        ReferenceEquals(type, types.SystemSByteType) || ReferenceEquals(type, types.SystemInt16Type);
}
