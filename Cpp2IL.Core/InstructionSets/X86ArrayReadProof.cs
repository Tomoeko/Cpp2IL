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
/// Closed exact-profile int[] read: prove both runtime exception exits and the only
/// successful memory read before replacing the diamond with an implicit managed ldelem.
/// This does not generalize to unchecked native array access or other element widths.
/// </summary>
internal static class X86ArrayReadProof
{
    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var app = context.AppContext;
        var owner = context.DeclaringType;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { parameterCount: 2, GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } ||
            owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            owner.Attributes != owner.DefaultAttributes || context.GenericParameters.Count != 0 ||
            !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                       MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters is not [var array, var index] ||
            definition.InternalParameterData is not [var rawArray, var rawIndex] ||
            array.Definition == null || index.Definition == null ||
            array.Definition != rawArray || index.Definition != rawIndex ||
            array.ParameterIndex != 0 || index.ParameterIndex != 1 ||
            !ReferenceEquals(array.DeclaringMethod, context) ||
            !ReferenceEquals(index.DeclaringMethod, context) ||
            array.IsRef || index.IsRef ||
            array.Attributes != array.DefaultAttributes || index.Attributes != index.DefaultAttributes ||
            array.OverrideParameterType != null || index.OverrideParameterType != null ||
            array.ParameterType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
            context.OverrideReturnType != null ||
            !ReferenceEquals(context.ReturnType, app.SystemTypes.SystemInt32Type) ||
            array.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            index.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            body.Count < 13 || body[0].IP != context.UnderlyingPointer)
            return null;

        var nullCall = body[9];
        var boundsCall = body[11];
        if (!TryProveShape(body) ||
            X86RuntimeNullThrowProof.TryIdentify(app, nullCall.NearBranchTarget) == null ||
            !X86RuntimeBoundsThrowProof.TryIdentify(app, boundsCall.NearBranchTarget) ||
            X86CallerExceptionRegionProof.Check(context, body,
                new HashSet<ulong> { nullCall.IP, boundsCall.IP }) != null)
            return null;

        // ArrayRecovery recognizes this typed offset/scale as int[] element access and
        // the emitter generates ldelem. It preserves the source null-first, unsigned
        // bounds check and successful load without emitting either helper explicitly.
        var result = new IsilRegister(null, "array_read_result");
        var memory = new ISIL.MemoryOperand(new IsilRegister(null, "rcx"),
            new IsilRegister(null, "rdx"), 0x20, 4);
        return
        [
            new(0, ISIL.OpCode.Move, result, memory),
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    internal static bool TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count < 13)
            return false;
        for (var i = 0; i < 13; i++)
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
               element.Op0Kind == OpKind.Register && element.Op0Register == Register.EAX &&
               Memory(element, 1, Register.RCX, Register.RAX, 4, 0x20, 4) &&
               Stack(body[7], Mnemonic.Add, 0x28) &&
               body[8].Code == Code.Retnq && body[8].OpCount == 0 &&
               Call(body[9]) && body[10].Code == Code.Int3 &&
               Call(body[11]) && body[12].Code == Code.Int3;
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
