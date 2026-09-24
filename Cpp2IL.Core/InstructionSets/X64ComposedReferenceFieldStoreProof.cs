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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete instance method which increments one Int32 field, captures a
/// reference field, then stores that captured value through a guarded class
/// parameter. The native tail only marks the GC card for the reference store.
/// The managed field operations preserve the three observable effects and supply
/// the same receiver checks at their original positions.
/// </summary>
internal static class X64ComposedReferenceFieldStoreProof
{
    internal sealed record Shape(int MarkerOffset, int SourceOffset, int DestinationOffset,
        ulong NullTarget, ulong BarrierTarget);

    internal sealed record Evidence(FieldAnalysisContext MarkerField,
        FieldAnalysisContext SourceField, FieldAnalysisContext DestinationField,
        ulong MarkerIncrementIp, ulong SourceReadIp, ulong DestinationStoreIp,
        ulong NativeEndExclusiveIp, ulong NullHelperCallIp, ulong BarrierTailIp);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        method.ComposedReferenceFieldStoreEvidence = null;
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        var holder = new ISIL.Register(null, "rdx");
        var captured = new ISIL.Register(null, "composed_reference_value");
        var marker = new ISIL.MemoryOperand(receiver, null, proof.MarkerField.Offset);
        var result = new List<ISIL.Instruction>
        {
            new(0, ISIL.OpCode.Add, marker,
                new ISIL.MemoryOperand(receiver, null, proof.MarkerField.Offset),
                new ISIL.Immediate(1))
                { IntegerBitWidth = 32, NativeAddress = proof.MarkerIncrementIp },
            new(1, ISIL.OpCode.Move, captured,
                new ISIL.MemoryOperand(receiver, null, proof.SourceField.Offset))
                { NativeAddress = proof.SourceReadIp },
            new(2, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(holder, null, proof.DestinationField.Offset), captured)
                { NativeAddress = proof.DestinationStoreIp },
            new(3, ISIL.OpCode.Return) { NativeAddress = proof.BarrierTailIp },
        };
        method.ComposedReferenceFieldStoreEvidence = proof;
        return result;
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            if (decoded.Count < 11 || TryProveShape(decoded.Take(11).ToArray()) is not { } shape)
                return null;

            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                !OrdinaryClass(owner) || !OrdinaryMethod(method, owner, out var holder) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                decoded[0].IP != method.UnderlyingPointer ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method))
                return null;

            var body = decoded.Take(11).ToArray();
            method.EnsureRawBytes();
            if (!decoded.SequenceEqual(X86Utils.Iterate(method)) ||
                !FileBackedClosedRegion(method, decoded, body, pe, unwind) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullTarget) == null ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.BarrierTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[10].IP }) != null)
                return null;

            var marker = UniqueField(owner, shape.MarkerOffset);
            var source = UniqueField(owner, shape.SourceOffset);
            var destination = UniqueField(holder, shape.DestinationOffset);
            if (marker == null || source == null || destination == null ||
                ReferenceEquals(marker, source) ||
                marker.FieldType != app.SystemTypes.SystemInt32Type ||
                marker.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } ||
                (marker.Attributes & FieldAttributes.InitOnly) != 0 ||
                !MatchingReferenceTypes(source, destination) ||
                (destination.Attributes & FieldAttributes.InitOnly) != 0 ||
                (!ReferenceEquals(owner, holder) &&
                 (destination.Visibility != FieldAttributes.Public ||
                  holder.Visibility != TypeAttributes.Public || holder.DeclaringType != null)) ||
                owner.Definition!.RawSizes.instance_size < shape.MarkerOffset + 4 ||
                owner.Definition.RawSizes.instance_size < shape.SourceOffset + 8 ||
                holder.Definition!.RawSizes.instance_size < shape.DestinationOffset + 8)
                return null;

            var ownerLocal = new ISIL.LocalVariable("proved-owner",
                new ISIL.Register(null, "rcx"), owner);
            var holderLocal = new ISIL.LocalVariable("proved-holder",
                new ISIL.Register(null, "rdx"), holder);
            if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(marker, ownerLocal, shape.MarkerOffset), 32) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new ISIL.FieldReference(source, ownerLocal, shape.SourceOffset)) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new ISIL.FieldReference(destination, holderLocal, shape.DestinationOffset)))
                return null;

            return new Evidence(marker, source, destination, body[1].IP, body[3].IP,
                body[7].IP, body[10].NextIP, body[10].IP, body[9].IP);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 11 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            Enumerable.Range(1, body.Count - 1).Any(index =>
                body[index].IP != body[index - 1].NextIP) ||
            !StackAdjustment(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Inc_rm32 ||
            !Memory(body[1], 0, Register.RCX, 4) ||
            !RegisterCopy(body[2], Register.RAX, Register.RDX) ||
            body[3].Code != Code.Mov_r64_rm64 ||
            body[3].Op0Kind != OpKind.Register || body[3].Op0Register != Register.RDX ||
            !Memory(body[3], 1, Register.RCX, 8) ||
            body[4].Code != Code.Test_rm64_r64 ||
            !Registers(body[4], Register.RAX, Register.RAX) ||
            body[5].Mnemonic != Mnemonic.Je || body[5].Op0Kind != OpKind.NearBranch64 ||
            body[5].NearBranchTarget != body[10].IP ||
            body[6].Code != Code.Lea_r64_m || body[6].Op0Kind != OpKind.Register ||
            body[6].Op0Register != Register.RCX ||
            !Memory(body[6], 1, Register.RAX, 0) ||
            body[7].Code != Code.Mov_rm64_r64 ||
            !Memory(body[7], 0, Register.RCX, 8) ||
            body[7].MemoryDisplacement64 != 0 ||
            body[7].Op1Kind != OpKind.Register || body[7].Op1Register != Register.RDX ||
            !StackAdjustment(body[8], Mnemonic.Add) ||
            body[9].Code != Code.Jmp_rel32_64 || body[9].Op0Kind != OpKind.NearBranch64 ||
            body[9].NearBranchTarget == 0 ||
            body[10].Code != Code.Call_rel32_64 || body[10].Op0Kind != OpKind.NearBranch64 ||
            body[10].NearBranchTarget == 0 ||
            body[9].NearBranchTarget == body[10].NearBranchTarget)
            return null;

        var marker = body[1].MemoryDisplacement64;
        var source = body[3].MemoryDisplacement64;
        var destination = body[6].MemoryDisplacement64;
        if (marker is < 16 or > int.MaxValue - 8 ||
            source is < 16 or > int.MaxValue - 8 ||
            destination is < 16 or > int.MaxValue - 8 ||
            NarrowFieldEqualityProof.StorageRangesOverlap((long)marker, 4, (long)source, 8))
            return null;
        return new Shape((int)marker, (int)source, (int)destination,
            body[10].NearBranchTarget, body[9].NearBranchTarget);
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        ISIL.NullCheckedCall.IsReferenceClass(type) &&
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace;

    private static bool OrdinaryMethod(MethodAnalysisContext method, TypeAnalysisContext owner,
        out TypeAnalysisContext holder)
    {
        holder = null!;
        if (method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var rawParameter] ||
            method.Parameters is not [var parameter] ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null || !method.IsVoid ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Name != parameter.DefaultName ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.AppContext.InstructionSet.GetParameterOperandsFromMethod(method) is not
                [ISIL.Register { Name: "rcx" }, ISIL.Register { Name: "rdx" },
                    ISIL.Register { Name: "r8" }])
            return false;

        holder = parameter.ParameterType;
        return OrdinaryClass(holder) &&
               ReferenceEquals(holder, method.AppContext.ResolveIl2CppType(rawParameter.RawType));
    }

    private static FieldAnalysisContext? UniqueField(TypeAnalysisContext owner, int offset)
    {
        var candidates = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        return candidates is [{ } field] && field.Name == field.DefaultName &&
               field.Attributes == field.DefaultAttributes ? field : null;
    }

    private static bool MatchingReferenceTypes(FieldAnalysisContext source,
        FieldAnalysisContext destination)
    {
        var sourceRaw = source.BackingData?.Field.RawFieldType;
        var destinationRaw = destination.BackingData?.Field.RawFieldType;
        if (sourceRaw is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            destinationRaw is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            sourceRaw.Type != destinationRaw.Type ||
            sourceRaw.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or
                Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING or
                Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY) ||
            !ISIL.NullCheckedCall.SameOrdinaryType(source.FieldType, destination.FieldType))
            return false;

        var type = source.FieldType;
        return ISIL.NullCheckedCall.IsReferenceClass(type) ||
               ISIL.NullCheckedCall.IsBoundedArrayReference(type);
    }

    private static bool FileBackedClosedRegion(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded, IReadOnlyList<NativeInstruction> body,
        PE pe, X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        var bodyEnd = body[^1].NextIP;
        if (bodyEnd <= start || bodyEnd - start > 96 ||
            unwind.ClassifySpan(start, bodyEnd) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree } span ||
            span.Start != start || span.RootStart != start ||
            span.End < bodyEnd || span.End - bodyEnd > 15 ||
            !unwind.MatchesUnwind(start, span.End, 4, 0, [4, 0x42]) ||
            !X64NativePaddingProof.HasInt3Padding(pe, bodyEnd, span.End) ||
            method.AppContext.MethodsByAddress.Keys.Any(address =>
                address > start && address < span.End))
            return false;

        var within = decoded.TakeWhile(instruction => instruction.IP < span.End).ToArray();
        if (within.Length < body.Count || within[^1].NextIP != span.End ||
            !within.Take(body.Count).SequenceEqual(body) ||
            within.Skip(body.Count).Any(instruction =>
                instruction.Code != Code.Int3 || instruction.Length != 1))
            return false;

        var length = checked((int)(span.End - start));
        if (method.RawBytes.Length < (int)(bodyEnd - start) ||
            start < unwind.ImageBase ||
            start - unwind.ImageBase > uint.MaxValue - (uint)length + 1)
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(span.End - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawEnd - rawStart != length - 1 || rawEnd >= raw.Length ||
            !raw.Slice((int)rawStart, (int)(bodyEnd - start))
                .SequenceEqual(method.RawBytes.AsSpan().Slice(0, (int)(bodyEnd - start))))
            return false;

        var rva = checked((uint)(start - unwind.ImageBase));
        for (var offset = 0; offset < length; offset++)
            if (!unwind.IsExecutableRva(rva + (uint)offset) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) != rawStart + offset)
                return false;
        return true;
    }

    private static bool StackAdjustment(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool RegisterCopy(NativeInstruction instruction, Register destination,
        Register source) =>
        instruction.Code == Code.Mov_r64_rm64 && Registers(instruction, destination, source);

    private static bool Registers(NativeInstruction instruction, Register destination,
        Register source) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Memory(NativeInstruction instruction, int operand, Register @base,
        int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory && instruction.MemoryBase == @base &&
        instruction.MemoryIndex == Register.None && instruction.MemoryIndexScale == 1 &&
        (width == 0 || instruction.MemorySize.GetSize() == width);
}
