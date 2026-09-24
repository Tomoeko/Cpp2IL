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
/// Recovers a closed Int32 array read, two ordinary Int32 field effects, and an
/// Int32 array write. The checks bind both accesses to the same native array
/// value; only their proven implicit managed accesses replace the throw arms.
/// </summary>
internal static class X64SequentialInt32FieldArrayProof
{
    internal sealed record Shape(int ArrayOffset, int CounterOffset, int ObservedOffset,
        ulong NullThrowTarget, ulong BoundsThrowTarget);

    internal sealed record Evidence(Shape Shape, FieldAnalysisContext ArrayField,
        FieldAnalysisContext CounterField, FieldAnalysisContext ObservedField);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        var array = new ISIL.Register(null, "sequential_array");
        var value = new ISIL.Register(null, "sequential_value");
        var counter = new ISIL.MemoryOperand(receiver, null, proof.CounterField.Offset);
        // These six operations are the native success-path events in order. The
        // typed array accesses supply each proven null/bounds failure at the
        // point where that access occurs, including after the field effects.
        return
        [
            new(0, ISIL.OpCode.Move, array,
                new ISIL.MemoryOperand(receiver, null, proof.ArrayField.Offset)),
            new(1, ISIL.OpCode.Move, value,
                new ISIL.MemoryOperand(array, new ISIL.Register(null, "rdx"), 0x20, 4)),
            new(2, ISIL.OpCode.Add, counter, counter, new ISIL.Immediate(1))
                { IntegerBitWidth = 32 },
            new(3, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.ObservedField.Offset), value),
            new(4, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(array, new ISIL.Register(null, "r8"), 0x20, 4), value),
            new(5, ISIL.OpCode.Return, value),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                !OrdinaryOwner(owner) || !OrdinaryMethod(method, owner) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                TryProveShape(decoded) is not { } shape ||
                decoded[0].IP != method.UnderlyingPointer)
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            var terminal = decoded[^1];
            if (terminal.NextIP <= start || terminal.NextIP >= ulong.MaxValue ||
                terminal.NextIP - start > 256 ||
                method.RawBytes.Length != (int)(terminal.NextIP - start) ||
                !FileBackedRegion(method, decoded, pe, unwind))
                return null;

            var complete = CompleteRegion(decoded, pe, terminal.NextIP);
            if (complete == null ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullThrowTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, shape.BoundsThrowTarget) ||
                X86CallerExceptionRegionProof.Check(method, complete,
                    new HashSet<ulong> { decoded[16].IP, decoded[18].IP }) != null)
                return null;

            var fields = owner.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Where(field => field.Offset == shape.ArrayOffset).ToArray() is not
                    [{ } arrayField] ||
                fields.Where(field => field.Offset == shape.CounterOffset).ToArray() is not
                    [{ } counterField] ||
                fields.Where(field => field.Offset == shape.ObservedOffset).ToArray() is not
                    [{ } observedField] ||
                ReferenceEquals(arrayField, counterField) ||
                ReferenceEquals(arrayField, observedField) ||
                ReferenceEquals(counterField, observedField) ||
                arrayField.Name != arrayField.DefaultName ||
                counterField.Name != counterField.DefaultName ||
                observedField.Name != observedField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
                !ReferenceEquals(element, app.SystemTypes.SystemInt32Type) ||
                !Int32Field(counterField, app) || !Int32Field(observedField, app) ||
                owner.Definition!.RawSizes.instance_size < shape.ArrayOffset + 8 ||
                owner.Definition.RawSizes.instance_size < shape.CounterOffset + 4 ||
                owner.Definition.RawSizes.instance_size < shape.ObservedOffset + 4)
                return null;

            var typedReceiver = new ISIL.LocalVariable("proved-sequential-owner",
                new ISIL.Register(null, "rcx"), owner);
            if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new ISIL.FieldReference(arrayField, typedReceiver, shape.ArrayOffset)) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(counterField, typedReceiver, shape.CounterOffset), 32) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(observedField, typedReceiver, shape.ObservedOffset), 32))
                return null;

            return new Evidence(shape, arrayField, counterField, observedField);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryOwner(TypeAnalysisContext owner) =>
        !owner.IsValueType && !owner.IsInterface && !owner.IsGenericInstance &&
        owner.GenericParameters.Count == 0 &&
        owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
        owner.Attributes == owner.DefaultAttributes &&
        ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
        owner.Definition is { PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool OrdinaryMethod(MethodAnalysisContext method, TypeAnalysisContext owner)
    {
        var app = method.AppContext;
        if (method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var first, var second] ||
            method.Parameters is not [var firstParameter, var secondParameter] ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type))
            return false;

        return Parameter(firstParameter, first, 0, method, app) &&
               Parameter(secondParameter, second, 1, method, app);
    }

    private static bool Parameter(ParameterAnalysisContext parameter,
        LibCpp2IL.Metadata.Il2CppParameterDefinition definition, int position,
        MethodAnalysisContext method, ApplicationAnalysisContext app) =>
        ReferenceEquals(parameter.Definition, definition) &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        parameter.ParameterIndex == position && !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null &&
        ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) &&
        definition.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
            NumMods: 0, Byref: 0, Pinned: 0 };

    private static bool Int32Field(FieldAnalysisContext field, ApplicationAnalysisContext app) =>
        ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt32Type) &&
        field.BackingData?.Field.RawFieldType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 };

    private static bool FileBackedRegion(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded, PE pe, X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        var end = decoded[^1].NextIP + 1;
        var length = checked((int)(end - start));
        var span = unwind.ClassifySpan(start, end);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != start || span.RootStart != start || span.End != end ||
            !unwind.MatchesUnwind(start, end, 4, 0, [4, 0x42]) ||
            method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < end))
            return false;

        if (start < unwind.ImageBase || start - unwind.ImageBase > uint.MaxValue - (uint)length + 1)
            return false;
        var raw = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        if (raw < 0 || last - raw != length - 1 || last >= image.Length ||
            !image.Slice(checked((int)raw), length - 1).SequenceEqual(method.RawBytes.AsSpan()) ||
            image[(int)last] != 0xCC)
            return false;
        var rva = checked((uint)(start - unwind.ImageBase));
        for (var offset = 0; offset < length; offset++)
            if (!unwind.IsExecutableRva(rva + (uint)offset) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) != raw + offset)
                return false;

        var fromBytes = X86Utils.Iterate(method.RawBytes.AsSpan(), start, false);
        return decoded.SequenceEqual(fromBytes);
    }

    private static IReadOnlyList<NativeInstruction>? CompleteRegion(
        IReadOnlyList<NativeInstruction> decoded, PE pe, ulong trapAddress)
    {
        if (!pe.TryMapVirtualAddressToRaw(trapAddress, out var raw) ||
            raw < 0 || raw >= pe.GetRawBinaryContent().Length ||
            pe.GetRawBinaryContent()[(int)raw] != 0xCC)
            return null;
        var trap = Decoder.Create(64, new ByteArrayCodeReader([0xCC]), trapAddress).Decode();
        return trap.Code == Code.Int3 && trap.NextIP == trapAddress + 1
            ? decoded.Append(trap).ToArray() : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 19 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            Enumerable.Range(1, 18).Any(index => body[index].IP != body[index - 1].NextIP) ||
            !Entry(body) || !FirstAccess(body) || !InterveningEffects(body) ||
            !SecondAccess(body) || !Exits(body))
            return null;

        var arrayOffset = body[1].MemoryDisplacement64;
        var counterOffset = body[8].MemoryDisplacement64;
        var observedOffset = body[9].MemoryDisplacement64;
        if (arrayOffset is < 16 or > int.MaxValue - 8 ||
            counterOffset is < 16 or > int.MaxValue - 8 ||
            observedOffset is < 16 or > int.MaxValue - 8 ||
            arrayOffset == counterOffset || arrayOffset == observedOffset ||
            counterOffset == observedOffset ||
            body[16].NearBranchTarget == body[18].NearBranchTarget)
            return null;
        return new Shape((int)arrayOffset, (int)counterOffset, (int)observedOffset,
            body[16].NearBranchTarget, body[18].NearBranchTarget);
    }

    private static bool Entry(IReadOnlyList<NativeInstruction> body) =>
        Stack(body[0], Mnemonic.Sub) &&
        MemoryLoad(body[1], Register.R9, Register.RCX, 8) &&
        Registers(body[2], Mnemonic.Test, Register.R9, Register.R9) &&
        Branch(body[3], Mnemonic.Je, body[16].IP);

    private static bool FirstAccess(IReadOnlyList<NativeInstruction> body) =>
        LengthComparison(body[4], Register.EDX) &&
        Branch(body[5], Mnemonic.Jae, body[18].IP) &&
        body[6].Code == Code.Movsxd_r64_rm32 &&
        Registers(body[6], Mnemonic.Movsxd, Register.RAX, Register.EDX) &&
        body[7].Code == Code.Mov_r32_rm32 &&
        body[7].Op0Register == Register.EAX &&
        Memory(body[7], 1, Register.R9, Register.RAX, 4, 0x20, 4);

    private static bool InterveningEffects(IReadOnlyList<NativeInstruction> body) =>
        body[8].Code == Code.Inc_rm32 &&
        Memory(body[8], 0, Register.RCX, Register.None, 1,
            body[8].MemoryDisplacement64, 4) &&
        body[9].Code == Code.Mov_rm32_r32 &&
        Memory(body[9], 0, Register.RCX, Register.None, 1,
            body[9].MemoryDisplacement64, 4) &&
        body[9].Op1Kind == OpKind.Register && body[9].Op1Register == Register.EAX;

    private static bool SecondAccess(IReadOnlyList<NativeInstruction> body) =>
        LengthComparison(body[10], Register.R8D) &&
        Branch(body[11], Mnemonic.Jae, body[18].IP) &&
        body[12].Code == Code.Movsxd_r64_rm32 &&
        Registers(body[12], Mnemonic.Movsxd, Register.RCX, Register.R8D) &&
        body[13].Code == Code.Mov_rm32_r32 &&
        Memory(body[13], 0, Register.R9, Register.RCX, 4, 0x20, 4) &&
        body[13].Op1Kind == OpKind.Register && body[13].Op1Register == Register.EAX;

    private static bool Exits(IReadOnlyList<NativeInstruction> body) =>
        Stack(body[14], Mnemonic.Add) &&
        body[15].Code == Code.Retnq && body[15].OpCount == 0 &&
        DirectCall(body[16]) && body[17].Code == Code.Int3 &&
        DirectCall(body[18]);

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool MemoryLoad(NativeInstruction instruction, Register destination,
        Register source, int size) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        Memory(instruction, 1, source, Register.None, 1,
            instruction.MemoryDisplacement64, size);

    private static bool Registers(NativeInstruction instruction, Mnemonic mnemonic,
        Register destination, Register source) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool LengthComparison(NativeInstruction instruction, Register index) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == index &&
        Memory(instruction, 1, Register.R9, Register.None, 1, 0x18, 4);

    private static bool Memory(NativeInstruction instruction, int operand, Register @base,
        Register index, int scale, ulong displacement, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == displacement &&
        instruction.MemorySize.GetSize() == size;

    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic,
        ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
