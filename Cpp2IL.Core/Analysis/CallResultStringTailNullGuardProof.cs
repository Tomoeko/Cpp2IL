using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LiftedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Authenticates an instance reference getter followed by a guarded,
/// no-argument string-return tail call in one complete native body.
/// </summary>
internal static class CallResultStringTailNullGuardProof
{
    internal readonly record struct Shape(ulong ProducerCallsite,
        ulong ProducerTarget, ulong TailCallsite, ulong TailTarget,
        ulong NullCallsite, ulong NullHelper);

    internal static bool HasBoundTarget(MethodAnalysisContext caller,
        LocalVariable result, LiftedInstruction origin,
        LiftedInstruction guardedCall, MethodAnalysisContext producer,
        LocalVariable producerReceiver, MethodAnalysisContext target)
    {
        try
        {
            var app = caller.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !OrdinaryMethod(caller, app, Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    requireUniqueBinding: true) ||
                caller.IsStatic || caller.IsVirtual || caller.Parameters.Count != 0 ||
                !ReferenceEquals(caller.ReturnType,
                    app.SystemTypes.SystemStringType) ||
                caller.DeclaringType is not { } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                caller.ParameterLocals is not [{ } thisLocal] ||
                !thisLocal.IsThis || !ReferenceEquals(thisLocal.Type, owner) ||
                !ReferenceEquals(producerReceiver, thisLocal) ||
                !OrdinaryMethod(producer, app, Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    requireUniqueBinding: false) ||
                producer.IsStatic || producer.IsVirtual ||
                producer.Parameters.Count != 0 ||
                !ReferenceEquals(producer.DeclaringType, owner) ||
                !NullCheckedCall.IsReferenceClass(producer.ReturnType) ||
                !CallResultNullGuardProof.HasExactReferenceGetterBody(producer) ||
                !OrdinaryMethod(target, app, Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    requireUniqueBinding: true) ||
                target.IsStatic || target.IsVirtual ||
                target.Parameters.Count != 0 ||
                !ReferenceEquals(target.DeclaringType, result.Type) ||
                !ReferenceEquals(target.DeclaringType, producer.ReturnType) ||
                !ReferenceEquals(target.ReturnType,
                    app.SystemTypes.SystemStringType) ||
                origin.NativeAddress == null || guardedCall.NativeAddress == null ||
                origin is not { OpCode: OpCode.Call, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct } ||
                !ReferenceEquals(origin.Destination, result) ||
                origin.Operands.Count is not (3 or 4) ||
                !ReferenceEquals(origin.Operands[0], producer) ||
                !ReferenceEquals(origin.Operands[1], result) ||
                !ReferenceEquals(origin.Operands[2], thisLocal) ||
                origin.Operands.Count == 4 &&
                origin.Operands[3] is not Immediate { Value: 0 } ||
                guardedCall is not { OpCode: OpCode.Call,
                    IntegerBitWidth: 0 } ||
                guardedCall.CallSemantics is not
                    (CallSemantics.Direct or CallSemantics.NullCheckedInstance) ||
                guardedCall.Destination is not LocalVariable returnValue ||
                !ReferenceEquals(returnValue.Type, caller.ReturnType) ||
                guardedCall.Operands.Count is not (3 or 4) ||
                !ReferenceEquals(guardedCall.Operands[0], target) ||
                !ReferenceEquals(guardedCall.Operands[1], returnValue) ||
                !ReferenceEquals(guardedCall.Operands[2], result) ||
                guardedCall.Operands.Count == 4 &&
                guardedCall.Operands[3] is not Immediate { Value: 0 } ||
                caller.ControlFlowGraph is not { } graph)
                return false;

            var instructions = graph.Instructions.ToArray();
            if (instructions.Where(instruction => instruction.IsCall)
                    .ToArray() is not [var producerCall, var tailCall] ||
                !ReferenceEquals(producerCall, origin) ||
                !ReferenceEquals(tailCall, guardedCall) ||
                instructions.Where(instruction =>
                    instruction.OpCode == OpCode.Return).ToArray() is not
                    [var returned] ||
                returned.Operands is not [LocalVariable returnedValue] ||
                !ReferenceEquals(returnedValue, returnValue) ||
                instructions.Any(instruction => instruction.OpCode is not
                    (OpCode.Call or OpCode.CheckEqual or
                     OpCode.ConditionalJump or OpCode.Jump or OpCode.Return or
                     OpCode.RuntimeNullThrow or OpCode.Nop)) ||
                ReadBody(caller) is not { } body ||
                TryProveShape(body) is not { } shape ||
                origin.NativeAddress != shape.ProducerCallsite ||
                producer.UnderlyingPointer != shape.ProducerTarget ||
                guardedCall.NativeAddress != shape.TailCallsite ||
                target.UnderlyingPointer != shape.TailTarget ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(caller, body,
                    new HashSet<ulong> { shape.NullCallsite }) != null)
                return false;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return false;
        }
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        ApplicationAnalysisContext app, Il2CppTypeEnum rawReturn,
        bool requireUniqueBinding) =>
        ReferenceEquals(method.AppContext, app) &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract |
                              MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall)) == 0 &&
        method.OverrideReturnType == null &&
        method.GenericParameters.Count == 0 &&
        method.Definition is { GenericContainer: null,
            parameterCount: 0, RawReturnType:
            { NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        definition.RawReturnType.Type == rawReturn &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        method.DeclaringType is { Definition: { GenericContainer: null } } owner &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        NullCheckedCall.IsReferenceClass(owner) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext caller)
    {
        caller.EnsureRawBytes();
        if (caller.RawBytes.Length != 35 ||
            caller.UnderlyingPointer > ulong.MaxValue - 35)
            return null;
        return X64Stack28BodyProof.Read(caller, 10, 64);
    }

    // ReadBody and HasBoundTarget separately bind this native shape to the PE,
    // unwind frame, exact metadata, null helper, and managed call graph.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 10 || body[0].IP > ulong.MaxValue - 35 ||
            body[^1].NextIP != body[0].IP + 35 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Sub_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !Registers(body[1], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !DirectTransfer(body[2], Code.Call_rel32_64) ||
            !Registers(body[3], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            body[4].Code != Code.Je_rel8_64 ||
            body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget != body[9].IP ||
            !Registers(body[5], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[6], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            body[7].Code != Code.Add_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[7], Mnemonic.Add) ||
            !DirectTransfer(body[8], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[9], Code.Call_rel32_64))
            return null;

        return new Shape(body[2].IP, body[2].NearBranchTarget,
            body[8].IP, body[8].NearBranchTarget, body[9].IP,
            body[9].NearBranchTarget);
    }

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool DirectTransfer(NativeInstruction instruction,
        Code code) => instruction.Code == code && instruction.Length == 5 &&
                     instruction.Op0Kind == OpKind.NearBranch64;
}
