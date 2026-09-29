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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates a frameless reference-field transfer with ordered Int32
/// increments. Only the final field-address calculation and installed GC card
/// marker are implementation details; every preceding field effect is retained.
/// </summary>
internal static class X64ReferenceFieldAddressStoreProof
{
    internal sealed record Increment(int Offset, ulong Address);
    internal sealed record Shape(int SourceOffset, ulong SourceAddress, NativeRegister ValueRegister,
        IReadOnlyList<Increment> Increments, int DestinationOffset, ulong AddressSetup,
        ulong StoreAddress, ulong TailAddress, ulong EndAddress, ulong BarrierTarget);
    internal sealed record FieldIncrement(FieldAnalysisContext Field, ulong Address);
    internal sealed record Proof(FieldAnalysisContext Source, FieldAnalysisContext Destination,
        IReadOnlyList<FieldIncrement> Increments, Shape Native);

    internal static Proof? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind || method.IsStatic || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" || method.Parameters.Count != 0 || !method.IsVoid ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } } ||
            method.DeclaringType is not { } owner || !NullCheckedCall.IsReferenceClass(owner) ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Definition is not { GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return null;
        try
        {
            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            var start = method.UnderlyingPointer;
            if (TryProveShape(body) is not { } shape || body[0].IP != start ||
                shape.EndAddress - start != (ulong)method.RawBytes.Length ||
                unwind.ClassifySpan(start, shape.EndAddress).Kind != X64UnwindProof.SpanKind.NoEntry ||
                app.MethodsByAddress.Keys.Any(address => address > start && address < shape.EndAddress) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, method.RawBytes.AsSpan(), start) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, start, (uint)method.RawBytes.Length) ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.BarrierTarget))
                return null;

            var local = new LocalVariable("proved-owner", new ISIL.Register(null, "rcx"), owner);
            var source = Field(shape.SourceOffset);
            var destination = Field(shape.DestinationOffset);
            if (source == null || destination == null ||
                !ReferenceEquals(source.FieldType, destination.FieldType) ||
                !ReferenceField(source) || !ReferenceField(destination) ||
                (destination.Attributes & FieldAttributes.InitOnly) != 0)
                return null;
            var increments = new List<FieldIncrement>();
            foreach (var increment in shape.Increments)
            {
                var field = Field(increment.Offset);
                if (field == null || !ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt32Type) ||
                    field.BackingData?.Field.RawFieldType is not
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } ||
                    (field.Attributes & FieldAttributes.InitOnly) != 0 ||
                    !Contained(field, 4) ||
                    !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field, local, field.Offset), 32))
                    return null;
                increments.Add(new FieldIncrement(field, increment.Address));
            }
            return new Proof(source, destination, increments, shape);

            FieldAnalysisContext? Field(int offset)
            {
                var fields = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
                return fields is [{ } field] && field.Name == field.DefaultName &&
                       field.Visibility == FieldAttributes.Public && field.Attributes == field.DefaultAttributes
                    ? field : null;
            }

            bool ReferenceField(FieldAnalysisContext field) =>
                field.BackingData?.Field.RawFieldType is
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } &&
                NullCheckedCall.IsReferenceClass(field.FieldType) &&
                Contained(field, 8) &&
                NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(field, local, field.Offset));

            bool Contained(FieldAnalysisContext field, uint width) => field.Offset >= 16 &&
                (ulong)field.Offset + width <= owner.Definition.RawSizes.instance_size;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is < 4 or > 8 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            Enumerable.Range(1, body.Count - 1).Any(index => body[index - 1].NextIP != body[index].IP))
            return null;
        var address = body[^3];
        var store = body[^2];
        var tail = body[^1];
        var offset = address.Code switch
        {
            Code.Add_rm64_imm8 or Code.Add_rm64_imm32 when address.Op0Kind == OpKind.Register &&
                address.Op0Register == NativeRegister.RCX && address.Op1Kind is
                    OpKind.Immediate8to64 or OpKind.Immediate32to64 => address.GetImmediate(1),
            Code.Lea_r64_m when address.Op0Kind == OpKind.Register && address.Op0Register == NativeRegister.RCX &&
                Memory(address, 1, 0) => address.MemoryDisplacement64,
            _ => 0UL,
        };
        if (offset is < 16 or > int.MaxValue - 8 || store.Code != Code.Mov_rm64_r64 ||
            !Memory(store, 0, 8) || store.MemoryDisplacement64 != 0 || store.Op1Kind != OpKind.Register ||
            store.Op1Register is not (NativeRegister.RAX or NativeRegister.RDX or NativeRegister.R8 or
                NativeRegister.R9 or NativeRegister.R10 or NativeRegister.R11) ||
            tail.Code != Code.Jmp_rel32_64 || tail.Op0Kind != OpKind.NearBranch64 || tail.NearBranchTarget == 0)
            return null;

        NativeInstruction? capture = null;
        var increments = new List<Increment>();
        foreach (var instruction in body.Take(body.Count - 3))
        {
            if (instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
                instruction.Op0Register == store.Op1Register && Memory(instruction, 1, 8) &&
                instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue - 8)
            {
                if (capture != null)
                    return null;
                capture = instruction;
            }
            else if (instruction.Code == Code.Inc_rm32 && Memory(instruction, 0, 4) &&
                     instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue - 4)
                increments.Add(new Increment((int)instruction.MemoryDisplacement64, instruction.IP));
            else
                return null;
        }
        return capture is { } source ? new Shape((int)source.MemoryDisplacement64, source.IP,
            source.Op0Register, increments, (int)offset, address.IP, store.IP, tail.IP,
            tail.NextIP, tail.NearBranchTarget) : null;
    }

    private static bool Memory(NativeInstruction instruction, int operand, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory && instruction.MemoryBase == NativeRegister.RCX &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryIndexScale == 1 &&
        (width == 0 || instruction.MemorySize.GetSize() == width);
}
