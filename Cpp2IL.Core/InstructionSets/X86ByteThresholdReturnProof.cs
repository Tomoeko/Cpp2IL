using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the complete frame-free x64 body of an unsigned byte-field comparison
/// with 128. SETAE consumes only CMP's carry flag, so the managed comparison
/// retains the native threshold without modeling undefined upper AL bits.
/// </summary>
internal static class X86ByteThresholdReturnProof
{
    internal sealed record Shape(int FieldOffset, ulong End);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Instruction> body)
    {
        if (Find(method, body) is not { } field)
            return null;

        var value = new ISIL.Register(null, "byte_threshold_value");
        var result = new ISIL.Register(null, "byte_threshold_result");
        var read = new ISIL.MemoryOperand(new ISIL.Register(null, "rcx"), null, field.Offset);
        return
        [
            new(0, ISIL.OpCode.Move, value, read) { IntegerBitWidth = 8 },
            // Ldfld of System.Byte zero-extends onto the managed Int32 stack.
            // This closed proof has already established the native eight-bit
            // unsigned predicate, so no unproved narrow arithmetic is emitted.
            new(1, ISIL.OpCode.CheckGreaterOrEqualUnsigned, result, value,
                new ISIL.Immediate(128)),
            new(2, ISIL.OpCode.Return, result),
        ];
    }

    internal static FieldAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<Instruction> body)
    {
        var app = method.AppContext;
        var shape = TryProveShape(body);
        if (shape == null || !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            owner.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            shape.End < method.UnderlyingPointer ||
            shape.End - method.UnderlyingPointer > (ulong)method.RawBytes.Length ||
            (ulong)method.RawBytes.Length - (shape.End - method.UnderlyingPointer) > 15 ||
            !X64NativePaddingProof.HasInt3Padding(pe, shape.End,
                method.UnderlyingPointer + (ulong)method.RawBytes.Length) ||
            Enumerable.Range(1, checked((int)(shape.End - method.UnderlyingPointer) - 1)).Any(
                offset => app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)) ||
            unwind.ClassifySpan(method.UnderlyingPointer, shape.End) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var start, End: var end } ||
            start != method.UnderlyingPointer || end != shape.End ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset &&
            ReferenceEquals(field.FieldType, app.SystemTypes.SystemByteType) &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_U1,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (fields is not [{ } matched] || matched.Name != matched.DefaultName)
            return null;
        var receiver = new ISIL.LocalVariable("proved-byte-owner",
            new ISIL.Register(null, "rcx"), owner);
        return NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(
            new ISIL.FieldReference(matched, receiver, shape.FieldOffset))
            ? matched : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body is not [{ } compare, { } set, { } ret] ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            set.IP != compare.NextIP || ret.IP != set.NextIP ||
            compare.Code != Code.Cmp_rm8_imm8 || compare.OpCount != 2 ||
            compare.Op0Kind != OpKind.Memory || compare.Op1Kind != OpKind.Immediate8 ||
            compare.MemoryBase != Register.RCX || compare.MemoryIndex != Register.None ||
            compare.MemorySize.GetSize() != 1 ||
            // A null RCX must still fault on the byte read so managed ldfld
            // preserves the native failure. Keep the access in the null page.
            compare.MemoryDisplacement64 is < 16 or > 0x1000 - 1 ||
            compare.Immediate8 != 128 ||
            set.Code != Code.Setae_rm8 || set.OpCount != 1 ||
            set.Op0Kind != OpKind.Register ||
            set.Op0Register != Register.AL ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new Shape((int)compare.MemoryDisplacement64, ret.NextIP);
    }
}
