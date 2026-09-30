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
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete static constructor that initializes one static field of
/// its own single-scalar value type from a native immediate. The method
/// metadata guard, TypeInfo binding, static storage and unwind region are all
/// authenticated before the store is emitted.
/// </summary>
internal static class X64ScalarWrapperStaticConstructorProof
{
    private const string EvidenceKey = "X64ScalarWrapperStaticConstructorProof";
    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
        method.GetExtraData<X64SmallAggregateFieldGetterProof.InputState>(EvidenceKey) != null;
    internal sealed record Evidence(FieldAnalysisContext StaticField,
        FieldAnalysisContext ScalarField, ulong ValueBits);
    internal readonly record struct Shape(ulong Flag, ulong TypeInfoSlot,
        ulong Initializer, int Width, ulong ValueBits);

    internal static bool TryAuthenticate(MethodAnalysisContext method, out Evidence proof)
    {
        proof = null!;
        if (Find(method) is not { } current) return false;
        var values = new List<object>();
        X64SmallAggregateFieldGetterProof.CaptureType(method.DeclaringType!, values);
        X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
        X64SmallAggregateFieldGetterProof.CaptureRawType(method.Definition!.RawReturnType!, values);
        values.Add(current.StaticField);
        values.Add(current.ScalarField);
        values.Add(current.ValueBits);
        var input = new X64SmallAggregateFieldGetterProof.InputState(values, method.RawBytes.AsSpan().ToArray());
        var saved = method.GetExtraData<X64SmallAggregateFieldGetterProof.InputState>(EvidenceKey);
        if (NativeRecoveryProofTracker.Has(method, EvidenceKey))
        {
            if (saved == null || !saved.Matches(input)) return false;
        }
        else
        {
            if (saved != null) return false;
            method.PutExtraData(EvidenceKey, input);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        }
        proof = current;
        return true;
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !OrdinaryConstructor(method) ||
                method.DeclaringType is not { } owner ||
                !OrdinaryOwner(owner, method) ||
                X64ScalarWrapperTailCallProof.ScalarField(owner, allowSignedWord: true) is not { } scalar ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method))
                return null;

            var width = TypeSizes.UnboxedSize(owner, 8);
            if (width is not (2 or 4 or 8) ||
                owner.Definition!.RawSizes.static_fields_size != width ||
                owner.Fields.Where(field => field.IsStatic).ToArray() is not [{ } stored] ||
                !OrdinaryStaticField(stored, owner))
                return null;

            var start = method.UnderlyingPointer;
            var region = unwind.ClassifySpan(start, start + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != start || region.RootStart != start ||
                (width == 2 ? region.End - start is not (56UL or 59UL) :
                    region.End - start != (width == 4 ? 57UL : 58UL)) ||
                !unwind.MatchesUnwind(region.Start, region.End, 4, 0,
                    new byte[] { 4, 0x42 }))
                return null;

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            if (method.RawBytes.Length != (long)(region.End - start) ||
                body.Length is not (11 or 12) || body[0].IP != start ||
                body[^1].NextIP != region.End ||
                body.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None) ||
                Enumerable.Range(1, body.Length - 1).Any(index =>
                    body[index].IP != body[index - 1].NextIP) ||
                Enumerable.Range(1, checked((int)(region.End - start) - 1)).Any(offset =>
                    app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
                !FileBackedExecutableBody(method, pe, unwind, region.End) ||
                TryProveShape(body) is not { } shape || shape.Width != width ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null)
                return null;

            if ((shape.Flag >= shape.TypeInfoSlot &&
                 shape.Flag - shape.TypeInfoSlot < 8) ||
                !X64MetadataStaticGetterProof.ZeroInitializedWritableData(
                    unwind, shape.Flag, 1) ||
                !unwind.IsUnaffectedByBaseRelocation(shape.Flag, 1) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(
                    pe, unwind, shape.TypeInfoSlot, 8) ||
                !unwind.IsUnaffectedByBaseRelocation(shape.TypeInfoSlot, 8) ||
                app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata !=
                    shape.Initializer ||
                !X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(
                    app, pe, unwind, shape.Initializer) ||
                app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot) is not
                    { Type: MetadataUsageType.TypeInfo, IsValid: true } usage ||
                !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), owner))
                return null;

            return new Evidence(stored, scalar, shape.ValueBits);
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
        if (body.Count is not (11 or 12) ||
            !Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Cmp_rm8_imm8 || !RipMemory(body[1], 0, 1) ||
            body[1].Op1Kind != OpKind.Immediate8 || body[1].Immediate8 != 0 ||
            body[2].Code != Code.Jne_rel8_64 ||
            body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[6].IP ||
            body[3].Code != Code.Lea_r64_m ||
            body[3].Op0Register != NativeRegister.RCX ||
            !RipMemory(body[3], 1, 0) ||
            body[4].Code != Code.Call_rel32_64 ||
            body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget == 0 ||
            body[5].Code != Code.Mov_rm8_imm8 ||
            !RipMemory(body[5], 0, 1) ||
            body[5].Op1Kind != OpKind.Immediate8 || body[5].Immediate8 != 1 ||
            body[5].IPRelativeMemoryAddress != body[1].IPRelativeMemoryAddress ||
            body[6].Code != Code.Mov_r64_rm64 ||
            body[6].Op0Register != NativeRegister.RAX ||
            !RipMemory(body[6], 1, 8) ||
            body[6].IPRelativeMemoryAddress != body[3].IPRelativeMemoryAddress ||
            body[7].Code != Code.Mov_r64_rm64 ||
            body[7].Op0Register != NativeRegister.RCX ||
            body[7].Op1Kind != OpKind.Memory ||
            body[7].MemoryBase != NativeRegister.RAX ||
            body[7].MemoryIndex != NativeRegister.None ||
            body[7].MemorySize.GetSize() != 8 ||
            body[7].MemoryDisplacement64 !=
                (ulong)Il2CppClassLayout.StaticFieldsOffset64 ||
            !Stack(body[^2], Mnemonic.Add) ||
            body[^1].Code != Code.Retnq || body[^1].OpCount != 0)
            return null;

        var store = body[^3];
        if (store.Op0Kind != OpKind.Memory || store.MemoryBase != NativeRegister.RCX ||
            store.MemoryIndex != NativeRegister.None || store.MemoryDisplacement64 != 0)
            return null;

        int width;
        ulong bits;
        if (body.Count == 12)
        {
            // The immediate's upper bits cannot escape: only AX is stored, and
            // the complete remaining body restores the frame and returns void.
            var literal = body[8];
            if (literal.Code != Code.Mov_r32_imm32 || literal.Op0Kind != OpKind.Register ||
                literal.Op0Register != NativeRegister.EAX || literal.Op1Kind != OpKind.Immediate32 ||
                store.Code != Code.Mov_rm16_r16 || store.Op1Kind != OpKind.Register ||
                store.Op1Register != NativeRegister.AX || store.MemorySize.GetSize() != 2)
                return null;
            width = 2;
            bits = unchecked((ushort)literal.Immediate32);
        }
        else
        {
            width = store.Code switch
            {
                Code.Mov_rm16_imm16 when store.Op1Kind == OpKind.Immediate16 &&
                    store.MemorySize.GetSize() == 2 => 2,
                Code.Mov_rm32_imm32 when store.Op1Kind == OpKind.Immediate32 &&
                    store.MemorySize.GetSize() == 4 => 4,
                Code.Mov_rm64_imm32 when store.Op1Kind == OpKind.Immediate32to64 &&
                    store.MemorySize.GetSize() == 8 => 8,
                _ => 0,
            };
            bits = width == 2 ? store.Immediate16 : width == 4 ? store.Immediate32 :
                unchecked((ulong)(long)unchecked((int)store.Immediate32));
        }
        if (width == 0)
            return null;
        return new Shape(body[1].IPRelativeMemoryAddress,
            body[3].IPRelativeMemoryAddress, body[4].NearBranchTarget,
            width, bits);
    }

    private static bool OrdinaryConstructor(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.IsStatic && !method.IsVirtual && method.IsVoid &&
               method.Name == ".cctor" && method.Name == method.DefaultName &&
               method.OverrideReturnType == null && method.Parameters.Count == 0 &&
               method.GenericParameters.Count == 0 &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract |
                                     MethodAttributes.PinvokeImpl)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               method.Definition is { GenericContainer: null, parameterCount: 0,
                   RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                       NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               ReferenceEquals(definition.DeclaringType,
                   method.DeclaringType?.Definition) &&
               ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType);
    }

    private static bool OrdinaryOwner(TypeAnalysisContext owner,
        MethodAnalysisContext constructor)
    {
        var app = owner.AppContext;
        return owner.Definition is { GenericContainer: null, HasCctor: true,
                   IsValueType: true, IsEnumType: false, IsBlittable: true,
                   IsByRefLike: false, IsImportOrWindowsRuntime: false,
                   PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               owner is not GenericInstanceTypeAnalysisContext &&
               owner.GenericParameters.Count == 0 &&
               owner.Name == owner.DefaultName &&
               owner.Namespace == owner.DefaultNamespace &&
               owner.Attributes == owner.DefaultAttributes &&
               (owner.Attributes & TypeAttributes.BeforeFieldInit) != 0 &&
               (owner.Attributes & TypeAttributes.LayoutMask) ==
                   TypeAttributes.SequentialLayout &&
               ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
               ReferenceEquals(owner.BaseType, app.SystemTypes.SystemValueTypeType) &&
               owner.Methods.Count(method => method.Name == ".cctor") == 1 &&
               ReferenceEquals(owner.Methods.Single(method =>
                   method.Name == ".cctor"), constructor);
    }

    private static bool OrdinaryStaticField(FieldAnalysisContext field,
        TypeAnalysisContext owner) =>
        field.IsStatic && ReferenceEquals(field.DeclaringType, owner) &&
        field.Offset == 0 && field.Offset == field.DefaultOffset &&
        field.Name == field.DefaultName &&
        field.Attributes == field.DefaultAttributes &&
        (field.Attributes & (FieldAttributes.FieldAccessMask |
                             FieldAttributes.Static | FieldAttributes.InitOnly)) ==
            (FieldAttributes.Public | FieldAttributes.Static |
             FieldAttributes.InitOnly) &&
        (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldRVA |
                             FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal)) == 0 &&
        field.OverrideFieldType == null && !field.UseOverrideConstantValue &&
        field.RawIl2CppCustomAttributeData.Length == 0 &&
        field.CustomAttributes is not { Count: > 0 } &&
        field.BackingData?.Field.RawFieldType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } &&
        ReferenceEquals(field.FieldType, owner) &&
        ReferenceEquals(field.DefaultFieldType, owner) &&
        field.StaticArrayInitialValue.Length == 0;

    private static bool FileBackedExecutableBody(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind, ulong end)
    {
        var start = method.UnderlyingPointer;
        if (start < unwind.ImageBase || end <= start ||
            end - unwind.ImageBase > uint.MaxValue)
            return false;
        var length = checked((int)(end - start));
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        return first >= 0 && last == first + length - 1 &&
               first <= image.Length - length &&
               method.RawBytes.AsSpan().SequenceEqual(
                   image.Slice(checked((int)first), length)) &&
               Enumerable.Range(0, length).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(
                       start + (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                       first + offset);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool RipMemory(NativeInstruction instruction,
        int operand, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        (width == 0 || instruction.MemorySize.GetSize() == width);
}
