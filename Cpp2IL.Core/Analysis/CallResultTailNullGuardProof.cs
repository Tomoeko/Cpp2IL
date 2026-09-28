using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using LiftedInstruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds the terminal direct jump in a complete guarded call-result body to its
/// managed invocation. The ordinary direct-call proof remains a separate gate.
/// </summary>
internal static class CallResultTailNullGuardProof
{
    internal readonly record struct Shape(ulong ProducerCallsite, ulong ProducerTarget,
        ulong GuardedCallsite, ulong GuardedTarget, ulong NullHelper, ulong NullCallsite,
        int Argument);

    internal static bool HasBoundTarget(MethodAnalysisContext caller, LocalVariable result,
        LiftedInstruction origin, LiftedInstruction guardedCall,
        MethodAnalysisContext producer, LocalVariable producerReceiver,
        MethodAnalysisContext target)
    {
        try
        {
            var app = caller.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                caller.IsStatic || caller.IsVirtual || !caller.IsVoid ||
                caller.Name is ".ctor" or ".cctor" ||
                caller.Name != caller.DefaultName ||
                caller.Attributes != caller.DefaultAttributes ||
                caller.ImplAttributes != caller.DefaultImplAttributes ||
                caller.OverrideReturnType != null || caller.Parameters.Count != 0 ||
                caller.GenericParameters.Count != 0 ||
                caller.Definition is not { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                caller.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(caller) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(caller) ||
                caller.ParameterLocals is not [{ } thisLocal] ||
                !ReferenceEquals(producerReceiver, thisLocal) ||
                !ReferenceEquals(thisLocal.Type, owner) ||
                producer.Parameters.Count != 0 ||
                target.IsStatic || target.IsVirtual || !target.IsVoid ||
                target.Parameters is not [{ } argument] ||
                !ReferenceEquals(argument.ParameterType, app.SystemTypes.SystemInt32Type) ||
                argument.Definition?.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                origin.NativeAddress == null || guardedCall.NativeAddress == null)
                return false;

            var receiverIndex = guardedCall.OpCode == OpCode.Call ? 2 : 1;
            if (guardedCall.Operands.Count != receiverIndex + 2 &&
                !(guardedCall.Operands.Count == receiverIndex + 3 &&
                  guardedCall.Operands[receiverIndex + 2] is Immediate { Value: 0 }) ||
                !ReferenceEquals(guardedCall.Operands[receiverIndex], result) ||
                guardedCall.Operands[receiverIndex + 1] is not LocalVariable argumentLocal ||
                !ReferenceEquals(argumentLocal.Type, app.SystemTypes.SystemInt32Type) ||
                caller.ControlFlowGraph is not { } graph)
                return false;

            // The exact installed x64 optimizer emits 40 code bytes and one
            // INT3 alignment byte in this handler-free frame.
            // Reading the whole unwind region also authenticates executable bytes,
            // native entry boundaries, the unwind code and any INT3 suffix.
            caller.EnsureRawBytes();
            if (caller.RawBytes.Length != 40 ||
                X64Stack28BodyProof.Read(caller, 11, 41) is not { } body ||
                TryProveShape(body) is not { } shape ||
                shape.ProducerCallsite != origin.NativeAddress ||
                shape.ProducerTarget != producer.UnderlyingPointer ||
                shape.GuardedCallsite != guardedCall.NativeAddress ||
                shape.GuardedTarget != target.UnderlyingPointer)
                return false;

            var argumentDefinitions = graph.Instructions.Where(instruction =>
                ReferenceEquals(instruction.Destination, argumentLocal)).ToArray();
            if (argumentDefinitions is not [{ } constant] ||
                constant.OpCode != OpCode.Add || constant.IntegerBitWidth != 32 ||
                constant.CallSemantics != CallSemantics.Direct ||
                constant.NativeAddress != body[7].IP ||
                constant.Operands.Count != 3 ||
                !ReferenceEquals(constant.Operands[0], argumentLocal) ||
                constant.Operands[1] is not Immediate { Value: 0 } ||
                constant.Operands[2] is not Immediate literal ||
                literal.Value != shape.Argument ||
                graph.Instructions.Where(instruction =>
                    !ReferenceEquals(instruction, constant) &&
                    OperandEffects.ReadLocals(instruction).Any(local =>
                        ReferenceEquals(local, argumentLocal))).ToArray() is not
                    [var use] || !ReferenceEquals(use, guardedCall) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(caller, body,
                    new HashSet<ulong> { shape.NullCallsite }) != null)
                return false;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    // This structural check is also exercised against altered decoded instructions.
    // The PE, unwind, helper and managed identities are established by HasBoundTarget.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 11 || body[0].IP > ulong.MaxValue - 40 ||
            body[^1].NextIP != body[0].IP + 40 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Sub_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Xor_r32_rm32 ||
            !Registers(body[1], NativeRegister.EDX, NativeRegister.EDX) ||
            body[2].Code != Code.Call_rel32_64 || body[2].Length != 5 ||
            body[2].Op0Kind != OpKind.NearBranch64 ||
            body[3].Code != Code.Test_rm64_r64 ||
            !Registers(body[3], NativeRegister.RAX, NativeRegister.RAX) ||
            body[4].Code != Code.Je_rel8_64 ||
            body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget != body[10].IP ||
            body[5].Code != Code.Xor_r32_rm32 ||
            !Registers(body[5], NativeRegister.R8D, NativeRegister.R8D) ||
            body[6].Code != Code.Mov_r64_rm64 ||
            !Registers(body[6], NativeRegister.RCX, NativeRegister.RAX) ||
            body[7].Code != Code.Lea_r32_m ||
            body[7].Op0Kind != OpKind.Register ||
            body[7].Op0Register != NativeRegister.EDX ||
            body[7].Op1Kind != OpKind.Memory ||
            body[7].MemoryBase != NativeRegister.R8 ||
            body[7].MemoryIndex != NativeRegister.None ||
            body[7].MemoryDisplacement64 > 127 ||
            body[8].Code != Code.Add_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) ||
            body[9].Code != Code.Jmp_rel32_64 || body[9].Length != 5 ||
            body[9].Op0Kind != OpKind.NearBranch64 ||
            body[10].Code != Code.Call_rel32_64 || body[10].Length != 5 ||
            body[10].Op0Kind != OpKind.NearBranch64)
            return null;

        return new Shape(body[2].IP, body[2].NearBranchTarget, body[9].IP,
            body[9].NearBranchTarget, body[10].NearBranchTarget, body[10].IP,
            checked((int)body[7].MemoryDisplacement64));
    }

    private static bool Registers(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;
}
