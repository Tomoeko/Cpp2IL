using System;
using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Closed exact-profile 32/64-bit integer array access: prove both runtime exception exits and the only
/// successful memory access before replacing the diamond with managed ldelem or stelem.
/// This does not generalize to unchecked native array access or other element widths.
/// </summary>
internal static class X86IntegerArrayAccessProof
{
    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var app = context.AppContext;
        var owner = context.DeclaringType;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } ||
            owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            owner.Attributes != owner.DefaultAttributes || context.GenericParameters.Count != 0 ||
            !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                       MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters.Count is not (2 or 3) ||
            definition.parameterCount != context.Parameters.Count ||
            definition.InternalParameterData?.Length != context.Parameters.Count)
            return null;

        var isWrite = context.Parameters.Count == 3;
        var array = context.Parameters[0];
        var index = context.Parameters[1];
        var rawArray = definition.InternalParameterData![0];
        var rawIndex = definition.InternalParameterData[1];
        if (array.Definition == null || index.Definition == null ||
            array.Definition != rawArray || index.Definition != rawIndex ||
            array.ParameterIndex != 0 || index.ParameterIndex != 1 ||
            !ReferenceEquals(array.DeclaringMethod, context) ||
            !ReferenceEquals(index.DeclaringMethod, context) ||
            array.IsRef || index.IsRef ||
            array.Attributes != array.DefaultAttributes || index.Attributes != index.DefaultAttributes ||
            array.OverrideParameterType != null || index.OverrideParameterType != null ||
            array.ParameterType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            (!ReferenceEquals(element, app.SystemTypes.SystemInt32Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemUInt32Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemInt64Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemUInt64Type)) ||
            !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
            array.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            index.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            context.OverrideReturnType != null ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            (isWrite
                ? !ReferenceEquals(context.ReturnType, app.SystemTypes.SystemVoidType) ||
                  rawReturn.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID ||
                  !ValidStoredParameter(context, definition, element)
                : !ReferenceEquals(context.ReturnType, element) ||
                  rawReturn.Type != RawElementType(app, element)) ||
            body.Count < 12 || body[0].IP != context.UnderlyingPointer)
            return null;

        var elementSize = ReferenceEquals(element, app.SystemTypes.SystemInt64Type) ||
                          ReferenceEquals(element, app.SystemTypes.SystemUInt64Type) ? 8 : 4;
        var nullCall = body[9];
        var boundsCall = body[11];
        if (!TryProveShape(body, isWrite, elementSize) ||
            X86RuntimeNullThrowProof.TryIdentify(app, nullCall.NearBranchTarget) == null ||
            !X86RuntimeBoundsThrowProof.TryIdentify(app, boundsCall.NearBranchTarget) ||
            X86CallerExceptionRegionProof.Check(context, body,
                new HashSet<ulong> { nullCall.IP, boundsCall.IP }) != null)
            return null;

        // ArrayRecovery recognizes this typed offset/scale as an element access.
        // The emitter preserves null-first and unsigned bounds failure via ldelem/stelem.
        var memory = new ISIL.MemoryOperand(new IsilRegister(null, "rcx"),
            new IsilRegister(null, "rdx"), 0x20, elementSize);
        if (isWrite)
            return
            [
                new(0, ISIL.OpCode.Move, memory, new IsilRegister(null, "r8")),
                new(1, ISIL.OpCode.Return),
            ];
        var result = new IsilRegister(null, "array_read_result");
        return
        [
            new(0, ISIL.OpCode.Move, result, memory),
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    private static bool ValidStoredParameter(MethodAnalysisContext context,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition, TypeAnalysisContext element)
    {
        var value = context.Parameters[2];
        return value.Definition != null && value.Definition == definition.InternalParameterData![2] &&
               value.ParameterIndex == 2 && ReferenceEquals(value.DeclaringMethod, context) &&
               !value.IsRef && value.Attributes == value.DefaultAttributes && value.OverrideParameterType == null &&
               ReferenceEquals(value.ParameterType, element) &&
               value.Definition.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
               raw.Type == RawElementType(context.AppContext, element);
    }

    private static Il2CppTypeEnum RawElementType(ApplicationAnalysisContext app, TypeAnalysisContext element)
        => ReferenceEquals(element, app.SystemTypes.SystemUInt64Type)
            ? Il2CppTypeEnum.IL2CPP_TYPE_U8
            : ReferenceEquals(element, app.SystemTypes.SystemInt64Type)
                ? Il2CppTypeEnum.IL2CPP_TYPE_I8
                : ReferenceEquals(element, app.SystemTypes.SystemUInt32Type)
                    ? Il2CppTypeEnum.IL2CPP_TYPE_U4 : Il2CppTypeEnum.IL2CPP_TYPE_I4;

    internal static bool TryProveShape(IReadOnlyList<Instruction> body, bool isWrite, int elementSize = 4)
    {
        if (body.Count < 12 || elementSize is not (4 or 8))
            return false;
        for (var i = 0; i < 12; i++)
        {
            var instruction = body[i];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                (i > 0 && instruction.IP != body[i - 1].NextIP))
                return false;
        }
        var nullBranch = body[2];
        var boundsBranch = body[4];
        var length = body[3];
        var element = body[6];
        return Stack(body[0], Mnemonic.Sub, 0x28) &&
               Registers(body[1], Mnemonic.Test, Register.RCX, Register.RCX) &&
               nullBranch.Mnemonic == Mnemonic.Je && nullBranch.Op0Kind == OpKind.NearBranch64 &&
               nullBranch.NearBranchTarget == body[9].IP &&
               length.Mnemonic == Mnemonic.Cmp && length.OpCount == 2 &&
               length.Op0Kind == OpKind.Register && length.Op0Register == Register.EDX &&
               Memory(length, 1, Register.RCX, Register.None, 1, 0x18, 4) &&
               boundsBranch.Mnemonic == Mnemonic.Jae && boundsBranch.Op0Kind == OpKind.NearBranch64 &&
               boundsBranch.NearBranchTarget == body[11].IP &&
               body[5].Code == Code.Movsxd_r64_rm32 &&
               Registers(body[5], Mnemonic.Movsxd, Register.RAX, Register.EDX) &&
               element.Mnemonic == Mnemonic.Mov && element.OpCount == 2 &&
               (isWrite
                   ? Memory(element, 0, Register.RCX, Register.RAX, elementSize, 0x20, elementSize) &&
                     element.Op1Kind == OpKind.Register &&
                     element.Op1Register == (elementSize == 8 ? Register.R8 : Register.R8D)
                   : element.Op0Kind == OpKind.Register &&
                     element.Op0Register == (elementSize == 8 ? Register.RAX : Register.EAX) &&
                     Memory(element, 1, Register.RCX, Register.RAX, elementSize, 0x20, elementSize)) &&
               Stack(body[7], Mnemonic.Add, 0x28) &&
               body[8].Code == Code.Retnq && body[8].OpCount == 0 &&
               Call(body[9]) && body[10].Code == Code.Int3 &&
               Call(body[11]);
    }

    private static bool Registers(Instruction i, Mnemonic mnemonic, Register destination, Register source)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op1Kind == OpKind.Register && i.Op0Register == destination && i.Op1Register == source;
    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == Register.RSP && i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           i.GetImmediate(1) == size;
    private static bool Memory(Instruction i, int operand, Register @base, Register index,
        int scale, ulong displacement, int size)
        => i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == @base &&
           i.MemoryIndex == index && i.MemoryIndexScale == scale &&
           i.MemoryDisplacement64 == displacement && i.MemorySize.GetSize() == size;
    private static bool Call(Instruction i)
        => i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0;
}
