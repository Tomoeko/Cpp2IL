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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Closed exact-profile access to an Int32[] or a four-byte enum array held in
/// an ordinary instance field. Enum elements are read without changing identity
/// or the signedness of their underlying type; enum stores remain unproved.
/// The field read precedes the array null and bounds exits, so the emitted ldfld
/// also preserves the native owner-null failure and evaluation order.
/// </summary>
internal static class X86FieldArrayAccessProof
{
    private const string EnumReadEvidenceKey = "X86FieldArrayAccessProof.EnumRead";
    private const int ArrayDataOffset = 0x20;
    private const int ElementSize = 4;
    internal enum AccessKind { ReadFirst, ReadFixed, ReadAt, WriteAt }

    internal sealed record Shape(AccessKind Kind, int FieldOffset, int NullCallIndex,
        int BoundsCallIndex, int FixedIndex = 0);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        if (Find(method, body) is not { } evidence)
            return null;

        var array = new ISIL.Register(null, "field_array_value");
        var field = new ISIL.MemoryOperand(new ISIL.Register(null, "rcx"), null,
            evidence.Field.Offset);
        var element = ElementMemory(evidence.Shape, array, new ISIL.Register(null, "rdx"));
        if (evidence.Shape.Kind == AccessKind.WriteAt)
            return
            [
                new(0, ISIL.OpCode.Move, array, field),
                new(1, ISIL.OpCode.Move, element, new ISIL.Register(null, "r8")),
                new(2, ISIL.OpCode.Return),
            ];

        var result = new ISIL.Register(null, "field_array_read_result");
        if (method.ReturnType.IsEnumType)
            method.PutExtraData(EnumReadEvidenceKey, evidence);
        return
        [
            new(0, ISIL.OpCode.Move, array, field) { NativeAddress = evidence.FieldReadAddress },
            new(1, ISIL.OpCode.Move, result, element)
                { NativeAddress = evidence.ElementReadAddress, IntegerBitWidth = 32 },
            new(2, ISIL.OpCode.Return, result),
        ];
    }

    internal sealed record Evidence(Shape Shape, FieldAnalysisContext Field,
        ulong FieldReadAddress, ulong ElementReadAddress);

    internal static Evidence? GetEnumReadEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<Evidence>(EnumReadEvidenceKey);

    internal static ISIL.MemoryOperand ElementMemory(Shape shape, ISIL.IOperand array,
        ISIL.IOperand? index)
    {
        var fixedRead = shape.Kind is AccessKind.ReadFirst or AccessKind.ReadFixed;
        return new ISIL.MemoryOperand(array, fixedRead ? null : index,
            ArrayDataOffset + (fixedRead ? shape.FixedIndex * ElementSize : 0),
            fixedRead ? 0 : ElementSize);
    }

    internal static bool IsProvedEnumRead(MethodAnalysisContext method,
        ISIL.Instruction instruction, int operandIndex, ISIL.MemoryOperand memory,
        ISIL.LocalVariable array, TypeAnalysisContext element)
    {
        if (operandIndex != 1 || instruction.OpCode != ISIL.OpCode.Move ||
            instruction.IntegerBitWidth != 32 ||
            !ReferenceEquals(element, method.ReturnType) ||
            !Enum32StorageProof.IsUnchanged(element) ||
            GetEnumReadEvidence(method) is not { } recorded ||
            instruction.NativeAddress != recorded.ElementReadAddress ||
            method.ControlFlowGraph is not { } graph ||
            !graph.Instructions.Contains(instruction) ||
            Find(method, X86Utils.Iterate(method).ToArray()) is not { } current ||
            current != recorded)
            return false;

        var definitions = graph.Instructions.Where(candidate =>
            ReferenceEquals(candidate.Destination, array)).ToArray();
        if (definitions is not [{ OpCode: ISIL.OpCode.Move,
                NativeAddress: var fieldAddress,
                Operands: [_, ISIL.FieldReference source] }] ||
            fieldAddress != current.FieldReadAddress ||
            !ReferenceEquals(source.Field, current.Field) ||
            source.Offset != current.Field.Offset || !source.Local.IsThis ||
            !method.ParameterLocals.Contains(source.Local) ||
            method.ParameterLocals.Count(local => local.IsThis) != 1 ||
            source.Local.Register.Version != -1 ||
            method.ParameterOperands.FirstOrDefault() is not ISIL.Register receiver ||
            source.Local.Register.Number != receiver.Number ||
            !ReferenceEquals(source.Local.Type, method.DeclaringType))
            return false;

        if (current.Shape.Kind is AccessKind.ReadFirst or AccessKind.ReadFixed)
            return memory.Index == null && memory.Scale == 0 &&
                   memory.Addend == ArrayDataOffset + current.Shape.FixedIndex * ElementSize;
        return current.Shape.Kind == AccessKind.ReadAt && memory.Addend == ArrayDataOffset &&
               memory.Scale == ElementSize && memory.Index is ISIL.LocalVariable index &&
               method.ParameterLocals.Contains(index) &&
               method.ParameterLocals.Count(local =>
                   LocalVariables.GetIncomingParameterIndex(method, local) == 0) == 1 &&
               LocalVariables.GetIncomingParameterIndex(method, index) == 0 &&
               ReferenceEquals(index.Type, method.AppContext.SystemTypes.SystemInt32Type) &&
               index.Register.Number == new ISIL.Register(null, "rdx").Number &&
               index.Register.Version == -1;
    }

    internal static Evidence? Find(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        var shape = TryProveShape(body);
        if (shape == null || !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.AppContext.Binary is not PE ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Definition is not { GenericContainer: null } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            body[0].IP != method.UnderlyingPointer ||
            shape.Kind == AccessKind.ReadFixed &&
            (X64Stack28BodyProof.Read(method, 12, 96) is not { } closed ||
             !closed.SequenceEqual(body.Take(12))) ||
            !MatchesSignature(method, shape.Kind))
            return null;

        // The metadata resolver uses the first field found at an offset. Require a
        // unique physical instance field independently before emitting that offset.
        var candidates = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset).ToArray();
        if (candidates is not [{ } matched] || matched.Name != matched.DefaultName ||
            matched.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, NumMods: 0, Byref: 0, Pinned: 0 } ||
            matched.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, shape.Kind == AccessKind.WriteAt
                ? method.AppContext.SystemTypes.SystemInt32Type : method.ReturnType) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new ISIL.FieldReference(
                matched, new ISIL.LocalVariable("proved-owner", new ISIL.Register(null, "rcx"), owner),
                shape.FieldOffset)) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return null;

        var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(method.AppContext,
            method.UnderlyingPointer, body, shape.BoundsCallIndex);
        if (region == null || region.Count != shape.BoundsCallIndex + 2 ||
            X86RuntimeNullThrowProof.TryIdentify(method.AppContext,
                region[shape.NullCallIndex].NearBranchTarget) == null ||
            !X86RuntimeBoundsThrowProof.TryIdentify(method.AppContext,
                region[shape.BoundsCallIndex].NearBranchTarget) ||
            X86CallerExceptionRegionProof.Check(method, region,
                new HashSet<ulong>
                {
                    region[shape.NullCallIndex].IP,
                    region[shape.BoundsCallIndex].IP,
                }) != null)
            return null;

        return new Evidence(shape, matched, body[1].IP,
            body[shape.Kind is AccessKind.ReadFirst or AccessKind.ReadFixed ? 6 : 7].IP);
    }

    private static bool MatchesSignature(MethodAnalysisContext method, AccessKind kind)
    {
        var app = method.AppContext;
        var expectedParameters = kind switch
        {
            AccessKind.ReadFirst or AccessKind.ReadFixed => 0,
            AccessKind.ReadAt => 1,
            AccessKind.WriteAt => 2,
            _ => -1,
        };
        if (method.Parameters.Count != expectedParameters ||
            method.Definition!.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            (kind == AccessKind.WriteAt
                ? !method.IsVoid || rawReturn.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID
                : !MatchesReadElement(method, rawReturn.Type)))
            return false;

        for (var index = 0; index < expectedParameters; index++)
        {
            var parameter = method.Parameters[index];
            if (parameter.ParameterIndex != index ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
                parameter.Definition?.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
        }
        return true;
    }

    private static bool MatchesReadElement(MethodAnalysisContext method, Il2CppTypeEnum rawReturn) =>
        ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        (ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type) &&
         rawReturn == Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
         rawReturn == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE &&
         Enum32StorageProof.IsUnchanged(method.ReturnType));

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count < 12 || !FieldLoad(body[1], out var arrayRegister, out var fieldOffset))
            return null;
        var first = arrayRegister == Register.RAX;
        if (!first && arrayRegister is not (Register.R8 or Register.R9))
            return null;
        var prefixCount = first ? 12 : 13;
        if (body.Count < prefixCount)
            return null;
        for (var index = 0; index < prefixCount; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }

        if (!Stack(body[0], Mnemonic.Sub) ||
            !Registers(body[2], Mnemonic.Test, arrayRegister, arrayRegister))
            return null;
        if (first)
        {
            if (body[4].Op1Kind is not
                (OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate32))
                return null;
            var fixedIndex = body[4].GetImmediate(1);
            if (!Branch(body[3], Mnemonic.Je, body[9].IP) ||
                body[4].Mnemonic != Mnemonic.Cmp || body[4].OpCount != 2 ||
                !Memory(body[4], 0, Register.RAX, Register.None, 1, 0x18, 4) ||
                fixedIndex > 127 ||
                !Branch(body[5], Mnemonic.Jbe, body[11].IP) ||
                body[6].Code != Code.Mov_r32_rm32 ||
                body[6].Op0Kind != OpKind.Register || body[6].Op0Register != Register.EAX ||
                !Memory(body[6], 1, Register.RAX, Register.None, 1,
                    (ulong)ArrayDataOffset + fixedIndex * ElementSize, ElementSize) ||
                fixedIndex != 0 &&
                (body[3].Code != Code.Je_rel8_64 ||
                 body[4].Code != Code.Cmp_rm32_imm8 ||
                 body[5].Code != Code.Jbe_rel8_64) ||
                !SuccessAndExits(body, 7, 8, 9, 10, 11))
                return null;
            return new Shape(fixedIndex == 0 ? AccessKind.ReadFirst : AccessKind.ReadFixed,
                fieldOffset, 9, 11, checked((int)fixedIndex));
        }

        if (!Branch(body[3], Mnemonic.Je, body[10].IP) ||
            body[4].Mnemonic != Mnemonic.Cmp || body[4].OpCount != 2 ||
            body[4].Op0Kind != OpKind.Register || body[4].Op0Register != Register.EDX ||
            !Memory(body[4], 1, arrayRegister, Register.None, 1, 0x18, 4) ||
            !Branch(body[5], Mnemonic.Jae, body[12].IP) ||
            body[6].Code != Code.Movsxd_r64_rm32 ||
            !Registers(body[6], Mnemonic.Movsxd, Register.RAX, Register.EDX))
            return null;

        var access = body[7];
        var isWrite = arrayRegister == Register.R9;
        if (isWrite
            ? access.Code != Code.Mov_rm32_r32 ||
              !Memory(access, 0, Register.R9, Register.RAX, ElementSize, ArrayDataOffset, ElementSize) ||
              access.Op1Kind != OpKind.Register || access.Op1Register != Register.R8D
            : access.Code != Code.Mov_r32_rm32 ||
              access.Op0Kind != OpKind.Register || access.Op0Register != Register.EAX ||
              !Memory(access, 1, Register.R8, Register.RAX, ElementSize, ArrayDataOffset, ElementSize))
            return null;
        return SuccessAndExits(body, 8, 9, 10, 11, 12)
            ? new Shape(isWrite ? AccessKind.WriteAt : AccessKind.ReadAt, fieldOffset, 10, 12)
            : null;
    }

    private static bool FieldLoad(Instruction instruction, out Register result, out int offset)
    {
        result = Register.None;
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 || instruction.Op0Kind != OpKind.Register ||
            !Memory(instruction, 1, Register.RCX, Register.None, 1,
                instruction.MemoryDisplacement64, 8) ||
            instruction.MemoryDisplacement64 is < 16 or >= 0x1000)
            return false;
        result = instruction.Op0Register;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool SuccessAndExits(IReadOnlyList<Instruction> body, int add, int ret,
        int nullCall, int nullTrap, int boundsCall) =>
        Stack(body[add], Mnemonic.Add) &&
        body[ret].Code == Code.Retnq && body[ret].OpCount == 0 &&
        Call(body[nullCall]) && body[nullTrap].Code == Code.Int3 &&
        Call(body[boundsCall]);

    private static bool Stack(Instruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool Registers(Instruction instruction, Mnemonic mnemonic,
        Register destination, Register source) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Memory(Instruction instruction, int operand, Register @base,
        Register index, int scale, ulong displacement, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == displacement && instruction.MemorySize.GetSize() == size;

    private static bool Branch(Instruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Call(Instruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
