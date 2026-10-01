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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves complete reference-array loops that reset a scalar field on each element.</summary>
internal static partial class X64ReferenceArrayScalarResetProof
{
    internal const string EvidenceKey = "X64ReferenceArrayScalarResetProof";
    internal enum LoopMode { ReloadedLength, FixedCount }
    internal sealed record Shape(LoopMode Mode, int ArrayOffset, int ElementOffset, int MarkerOffset,
        int Count, NativeInstruction NullCall, NativeInstruction BoundsCall, bool RepeatsArrayNullGuard = true);
    internal sealed record Proof(Shape Native, FieldAnalysisContext ArrayField, FieldAnalysisContext ElementField,
        FieldAnalysisContext? MarkerField, MethodAnalysisContext NullConstructor,
        X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native &&
            ReferenceEquals(ArrayField, other.ArrayField) && ReferenceEquals(ElementField, other.ElementField) &&
            ReferenceEquals(MarkerField, other.MarkerField) && ReferenceEquals(NullConstructor, other.NullConstructor) &&
            Input.Matches(other.Input);
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                !OrdinaryMethod(method) || !X64SmallAggregateFieldGetterProof.OriginalAbi(method)) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 4, 0, [4, 0x42]) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullCall.NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, shape.BoundsCall.NearBranchTarget) ||
                X86RuntimeNullThrowProof.BindIdentity(app) is not { } nullConstructor ||
                X86RuntimeNullThrowProof.BindIdentity(app, "IndexOutOfRangeException") is not { } boundsConstructor ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { shape.NullCall.IP, shape.BoundsCall.IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var values = new List<object>();
            var seen = new HashSet<TypeAnalysisContext>();
            // Freeze raw array chains before lazy resolution can follow malformed siblings.
            if (!CaptureChain(owner, values, seen)) return null;
            if (owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.ArrayOffset).ToArray()
                    is not [{ } arrayField] ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY } rawArray ||
                rawArray.GetEncapsulatedType() is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } rawElement ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
                !OrdinaryClass(element) || !AccessibleElement(owner, element) ||
                !ReferenceEquals(app.ResolveIl2CppType(rawElement), element) ||
                !CaptureChain(element, values, seen) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(arrayField,
                    new LocalVariable("reset-owner", new ManagedRegister(null, "rcx"), owner), shape.ArrayOffset)))
                return null;

            var boolean = shape.Mode == LoopMode.ReloadedLength;
            var type = boolean ? app.SystemTypes.SystemBooleanType : app.SystemTypes.SystemSingleType;
            if (element.Fields.Where(field => !field.IsStatic && field.Offset == shape.ElementOffset).ToArray()
                    is not [{ } elementField] || !WritableField(elementField, type) ||
                !(boolean ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(ElementAccess(), 8) :
                    NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(ElementAccess(), 32)))
                return null;
            FieldReference ElementAccess() => new(elementField,
                new LocalVariable("reset-element", new ManagedRegister(null, "rdx"), element), shape.ElementOffset);

            FieldAnalysisContext? marker = null;
            if (boolean)
            {
                if (owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.MarkerOffset).ToArray()
                        is not [{ } markerField] || !WritableField(markerField, app.SystemTypes.SystemSingleType) ||
                    !NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(new FieldReference(markerField,
                        new LocalVariable("reset-owner", new ManagedRegister(null, "rcx"), owner), shape.MarkerOffset), 32))
                    return null;
                marker = markerField;
            }
            foreach (var constructor in new[] { nullConstructor, boundsConstructor })
            {
                if (constructor.Definition?.RawReturnType is not { Data: not null } rawReturn ||
                    !CaptureChain(constructor.DeclaringType!, values, seen)) return null;
                X64SmallAggregateFieldGetterProof.CaptureMethod(constructor, values);
                X64SmallAggregateFieldGetterProof.CaptureRawType(rawReturn, values);
            }
            X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
            X64SmallAggregateFieldGetterProof.CaptureRawType(method.Definition!.RawReturnType!, values);
            var length = checked((int)(body[^1].NextIP - body[0].IP));
            var offset = checked((int)pe.MapVirtualAddressToRaw(body[0].IP, false));
            return new(shape, arrayField, elementField, marker, nullConstructor,
                new(values, pe.GetRawBinaryContent().Slice(offset, length).ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static bool TryAuthenticate(MethodAnalysisContext method, out Proof proof)
    {
        proof = null!;
        if (Find(method) is not { } current) return false;
        var saved = method.GetExtraData<Proof>(EvidenceKey);
        if (NativeRecoveryProofTracker.Has(method, EvidenceKey))
        {
            if (saved == null || !saved.Matches(current)) return false;
        }
        else
        {
            if (saved != null) return false;
            method.PutExtraData(EvidenceKey, current);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        }
        proof = current;
        return true;
    }

    private static bool WritableField(FieldAnalysisContext field, TypeAnalysisContext type) =>
        field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
        field.OverrideFieldType == null && field.Offset == field.DefaultOffset &&
        ReferenceEquals(field.FieldType, type) &&
        field.BackingData?.Field.RawFieldType is { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } raw &&
        raw.Type == type.Type && (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal |
            FieldAttributes.HasFieldMarshal)) == 0 &&
        (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

    private static bool OrdinaryMethod(MethodAnalysisContext method) =>
        method.DeclaringType is { } owner && OrdinaryClass(owner) && !owner.Definition!.HasCctor &&
        !owner.Methods.Any(member => member.Name == ".cctor") &&
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) && method.Parameters.Count == 0 &&
        !method.IsStatic && !method.IsVirtual && method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) &&
        ReferenceEquals(method.ReturnType, method.DefaultReturnType) && method.BaseMethod == null && method.Overrides.Count == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) && RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
        method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) &&
        bindings is [var bound] && ReferenceEquals(bound, method);

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> native)
    {
        if (native.Count is < 22 or > 48 || native[0].IP == 0 ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 && instruction.IP != native[index - 1].NextIP).Any())
            return null;
        var body = native.Where(instruction => !Padding(instruction)).ToArray();
        var shape = body.Length switch
        {
            32 => ReloadedLength(body),
            22 => FixedCount(body),
            _ => null
        };
        // The first owner access must have independently established null-receiver
        // fault behavior. In the length loop this is the marker write.
        return shape != null && X64ReferenceFieldNullComparisonProof.IsProvedNullReceiverOffset(
            shape.Mode == LoopMode.ReloadedLength ? shape.MarkerOffset : shape.ArrayOffset, 8) ? shape : null;
    }

    private static Shape? ReloadedLength(NativeInstruction[] b)
    {
        if (!X64Stack28BodyProof.Stack(b[0], Mnemonic.Sub) ||
            !Registers(b[1], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(b[2], Code.Mov_r64_rm64, NativeRegister.R9, NativeRegister.RCX) ||
            !Store(b[3], Code.Mov_rm32_r32, NativeRegister.RCX, NativeRegister.EDX) ||
            !Registers(b[4], Code.Mov_r32_rm32, NativeRegister.ECX, NativeRegister.EDX) ||
            !Memory(b[5], Code.Mov_r64_rm64, NativeRegister.RAX, NativeRegister.R9, NativeRegister.None, 1, 0, true) ||
            !Registers(b[6], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) || !Branch(b[7], Mnemonic.Je, b[28].IP) ||
            !Memory(b[8], Code.Cmp_r32_rm32, NativeRegister.ECX, NativeRegister.RAX, NativeRegister.None, 1, LengthOffset) ||
            !Branch(b[9], Mnemonic.Jge, b[26].IP) ||
            !Memory(b[10], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.R9, NativeRegister.None, 1, b[5].MemoryDisplacement64) ||
            !Registers(b[11], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) || !Branch(b[12], Mnemonic.Je, b[28].IP) ||
            !Memory(b[13], Code.Cmp_r32_rm32, NativeRegister.EDX, NativeRegister.RCX, NativeRegister.None, 1, LengthOffset) ||
            !Branch(b[14], Mnemonic.Jae, b[30].IP) ||
            !Registers(b[15], Code.Movsxd_r64_rm32, NativeRegister.RAX, NativeRegister.EDX) ||
            !Memory(b[16], Code.Mov_r64_rm64, NativeRegister.R8, NativeRegister.RCX, NativeRegister.RAX, 8, ItemOffset) ||
            !Registers(b[17], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) || !Branch(b[18], Mnemonic.Je, b[28].IP) ||
            !Increment(b[19], NativeRegister.EDX) || b[20].Code != Code.Mov_rm8_imm8 || b[20].OpCount != 2 ||
            b[20].Op0Kind != OpKind.Memory || !Address(b[20], NativeRegister.R8, NativeRegister.None, 1, 0, true) ||
            b[20].Op1Kind != OpKind.Immediate8 || b[20].Immediate8 != 0 ||
            !Memory(b[21], Code.Mov_r64_rm64, NativeRegister.RAX, NativeRegister.R9, NativeRegister.None, 1, b[5].MemoryDisplacement64) ||
            !Registers(b[22], Code.Mov_r32_rm32, NativeRegister.ECX, NativeRegister.EDX) ||
            !Registers(b[23], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) || !Branch(b[24], Mnemonic.Je, b[28].IP) ||
            !Branch(b[25], Mnemonic.Jmp, b[8].IP) || !EpilogueAndHelpers(b, 26, 28, 30))
            return null;
        return new(LoopMode.ReloadedLength, checked((int)b[5].MemoryDisplacement64),
            checked((int)b[20].MemoryDisplacement64), checked((int)b[3].MemoryDisplacement64), 0, b[28], b[30]);
    }

    private static Shape? FixedCount(NativeInstruction[] b)
    {
        var index = b[3].Op0Register;
        if (index is not (NativeRegister.ECX or NativeRegister.EDX)) return null;
        var element = index == NativeRegister.ECX ? NativeRegister.RDX : NativeRegister.RCX;
        if (!X64Stack28BodyProof.Stack(b[0], Mnemonic.Sub) ||
            !Memory(b[1], Code.Mov_r64_rm64, NativeRegister.R8, NativeRegister.RCX, NativeRegister.None, 1, 0, true) ||
            !Registers(b[2], Code.Xor_r32_rm32, NativeRegister.R9D, NativeRegister.R9D) ||
            !Registers(b[3], Code.Mov_r32_rm32, index, NativeRegister.R9D) ||
            !Registers(b[4], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) || !Branch(b[5], Mnemonic.Je, b[18].IP) ||
            !Memory(b[6], Code.Cmp_r32_rm32, index, NativeRegister.R8, NativeRegister.None, 1, LengthOffset) ||
            !Branch(b[7], Mnemonic.Jae, b[20].IP) ||
            !Registers(b[8], Code.Movsxd_r64_rm32, NativeRegister.RAX, index) ||
            !Memory(b[9], Code.Mov_r64_rm64, element, NativeRegister.R8, NativeRegister.RAX, 8, ItemOffset) ||
            !Registers(b[10], Code.Test_rm64_r64, element, element) || !Branch(b[11], Mnemonic.Je, b[18].IP) ||
            !Increment(b[12], index) || !Store(b[13], Code.Mov_rm32_r32, element, NativeRegister.R9D) ||
            b[14].Code != Code.Cmp_rm32_imm8 || b[14].OpCount != 2 || b[14].Op0Kind != OpKind.Register ||
            b[14].Op0Register != index || b[14].Op1Kind != OpKind.Immediate8to32 ||
            b[14].Immediate8to32 is < 1 or > 127 ||
            !(Branch(b[15], Mnemonic.Jl, b[4].IP) || Branch(b[15], Mnemonic.Jl, b[6].IP)) ||
            !EpilogueAndHelpers(b, 16, 18, 20))
            return null;
        return new(LoopMode.FixedCount, checked((int)b[1].MemoryDisplacement64),
            checked((int)b[13].MemoryDisplacement64), 0, b[14].Immediate8to32, b[18], b[20], b[15].NearBranchTarget == b[4].IP);
    }

    private static ulong LengthOffset => (ulong)Il2CppArrayUtils.GetLengthOffset(8);
    private static ulong ItemOffset => (ulong)Il2CppArrayUtils.GetFirstItemOffset(8);
    private static bool EpilogueAndHelpers(NativeInstruction[] body, int epilogue, int nullExit, int boundsExit) =>
        X64Stack28BodyProof.Stack(body[epilogue], Mnemonic.Add) && body[epilogue + 1].Code == Code.Retnq &&
        body[epilogue + 1].OpCount == 0 && CallAndTrap(body[nullExit], body[nullExit + 1]) &&
        CallAndTrap(body[boundsExit], body[boundsExit + 1]);
    private static bool CallAndTrap(NativeInstruction call, NativeInstruction trap) =>
        call.Code == Code.Call_rel32_64 && call.OpCount == 1 && call.Op0Kind == OpKind.NearBranch64 &&
        call.NearBranchTarget != 0 && trap.Code == Code.Int3 && trap.OpCount == 0;
    private static bool Increment(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Inc_rm32 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;
    private static bool Padding(NativeInstruction instruction) => instruction.Mnemonic == Mnemonic.Nop ||
        instruction.Length == 2 && instruction.Mnemonic == Mnemonic.Xchg && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.AX && instruction.Op1Register == NativeRegister.AX;
    private static bool Registers(NativeInstruction instruction, Code code, NativeRegister first, NativeRegister second) =>
        (instruction.Code == code || code == Code.Mov_r32_rm32 && instruction.Code == Code.Mov_rm32_r32 ||
         code == Code.Mov_r64_rm64 && instruction.Code == Code.Mov_rm64_r64 ||
         code == Code.Xor_r32_rm32 && instruction.Code == Code.Xor_rm32_r32) &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == first && instruction.Op1Register == second;
    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;
    private static bool Store(NativeInstruction instruction, Code code, NativeRegister owner, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Memory &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source &&
        Address(instruction, owner, NativeRegister.None, 1, 0, true);
    private static bool Memory(NativeInstruction instruction, Code code, NativeRegister register, NativeRegister owner,
        NativeRegister index, int scale, ulong displacement, bool offset = false) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Memory &&
        Address(instruction, owner, index, scale, displacement, offset);
    private static bool Address(NativeInstruction instruction, NativeRegister owner, NativeRegister index,
        int scale, ulong displacement, bool offset = false) =>
        instruction.MemoryBase == owner && instruction.MemoryIndex == index && instruction.MemoryIndexScale == scale &&
        (offset ? instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue : instruction.MemoryDisplacement64 == displacement);
}
