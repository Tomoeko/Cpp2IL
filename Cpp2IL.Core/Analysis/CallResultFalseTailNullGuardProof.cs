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
/// Binds a direct getter call and a Boolean-false terminal call in one closed
/// native body. Each managed caller is checked independently, including when
/// identical native code has been folded across distinct caller identities.
/// </summary>
internal static class CallResultFalseTailNullGuardProof
{
    internal readonly record struct Shape(ulong ProducerCallsite,
        ulong ProducerTarget, ulong GuardedCallsite, ulong GuardedTarget,
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
                !OrdinaryMethod(caller, app, requireUniqueBinding: false) ||
                caller.IsStatic || !caller.IsVoid || caller.Parameters.Count != 0 ||
                caller.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } ||
                caller.DeclaringType is not { } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                caller.ParameterLocals is not [{ } thisLocal] ||
                !thisLocal.IsThis || !ReferenceEquals(thisLocal.Type, owner) ||
                !ReferenceEquals(producerReceiver, thisLocal) ||
                !SameAssemblyFoldedCallers(caller) ||
                !OrdinaryMethod(producer, app, requireUniqueBinding: false, allowInternalCall: true) ||
                producer.IsStatic || producer.IsVirtual ||
                producer.Parameters.Count != 0 ||
                producer.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } ||
                !NullCheckedCall.IsReferenceClass(producer.ReturnType) ||
                producer.DeclaringType is not { } producerOwner ||
                !NullCheckedCall.HasUnchangedReferenceBase(owner,
                    producerOwner) ||
                !OrdinaryMethod(target, app, requireUniqueBinding: false, allowInternalCall: true) ||
                target.IsStatic || target.IsVirtual || !target.IsVoid ||
                target.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } ||
                target.DeclaringType is not { } targetOwner ||
                !NullCheckedCall.HasUnchangedReferenceBase(result.Type,
                    targetOwner) ||
                target.Parameters is not [{ } argument] ||
                !ReferenceEquals(argument.ParameterType,
                    app.SystemTypes.SystemBooleanType) ||
                argument.Definition?.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                origin.NativeAddress == null || guardedCall.NativeAddress == null ||
                origin.OpCode != OpCode.Call ||
                !ReferenceEquals(origin.Destination, result) ||
                origin.Operands is not [MethodAnalysisContext,
                    LocalVariable, LocalVariable, ..] ||
                !ReferenceEquals(origin.Operands[0], producer) ||
                !ReferenceEquals(origin.Operands[2], thisLocal) ||
                guardedCall.OpCode != OpCode.CallVoid ||
                guardedCall.Operands.Count is not (3 or 4) ||
                !ReferenceEquals(guardedCall.Operands[0], target) ||
                !ReferenceEquals(guardedCall.Operands[1], result) ||
                guardedCall.Operands[2] is not Immediate { Value: 0 } ||
                guardedCall.Operands.Count == 4 &&
                guardedCall.Operands[3] is not Immediate { Value: 0 } ||
                caller.ControlFlowGraph is not { } graph)
                return false;

            var calls = graph.Instructions.Where(instruction =>
                instruction.IsCall).ToArray();
            if (calls is not [var producerCall, var tailCall] ||
                !ReferenceEquals(producerCall, origin) ||
                !ReferenceEquals(tailCall, guardedCall) ||
                graph.Instructions.Any(instruction => instruction.OpCode is
                    OpCode.UnresolvedValue or OpCode.NotImplemented or
                    OpCode.IndirectCall) ||
                ReadBody(caller) is not { } body ||
                TryProveShape(body) is not { } shape ||
                origin.NativeAddress != shape.ProducerCallsite ||
                producer.UnderlyingPointer != shape.ProducerTarget ||
                guardedCall.NativeAddress != shape.GuardedCallsite ||
                target.UnderlyingPointer != shape.GuardedTarget ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
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
        ApplicationAnalysisContext app, bool requireUniqueBinding, bool allowInternalCall = false) =>
        ReferenceEquals(method.AppContext, app) &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract |
                              MethodAttributes.PinvokeImpl)) == 0 &&
        (allowInternalCall
            ? NullCheckedCall.HasEligibleImplementation(method)
            : (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                       MethodImplAttributes.ManagedMask |
                                       MethodImplAttributes.InternalCall)) == 0) &&
        method.OverrideReturnType == null &&
        method.GenericParameters.Count == 0 &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool SameAssemblyFoldedCallers(MethodAnalysisContext caller)
    {
        var app = caller.AppContext;
        if (caller.DeclaringType?.DeclaringAssembly is not { } assembly ||
            !app.MethodsByAddress.TryGetValue(caller.UnderlyingPointer,
                out var bindings) || bindings.Count == 0 ||
            bindings.Count(candidate => ReferenceEquals(candidate, caller)) != 1)
            return false;
        return bindings.All(candidate =>
            ReferenceEquals(candidate.DeclaringType?.DeclaringAssembly,
                assembly) &&
            OrdinaryMethod(candidate, app, requireUniqueBinding: false) &&
            candidate.DeclaringType is { } owner &&
            NullCheckedCall.IsReferenceClass(owner));
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext caller)
    {
        caller.EnsureRawBytes();
        if (caller.RawBytes.Length != 38 ||
            X64UnwindProof.ForApplication(caller.AppContext) is not { } unwind ||
            caller.UnderlyingPointer > ulong.MaxValue - 39)
            return null;
        var region = unwind.ClassifySpan(caller.UnderlyingPointer,
            caller.UnderlyingPointer + 1);
        if (region.End != caller.UnderlyingPointer + 39)
            return null;
        return X64Stack28BodyProof.Read(caller, 11, 39);
    }

    // This structural check is also exercised against altered decoded
    // instructions. ReadBody and HasBoundTarget authenticate the PE, unwind,
    // method metadata, helper, native identities, and exception closure.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 11 || body[0].IP > ulong.MaxValue - 38 ||
            body[^1].NextIP != body[0].IP + 38 ||
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
            body[4].NearBranchTarget != body[10].IP ||
            !Registers(body[5], Code.Xor_r32_rm32,
                NativeRegister.R8D, NativeRegister.R8D) ||
            !Registers(body[6], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[7], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            body[8].Code != Code.Add_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) ||
            !DirectTransfer(body[9], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[10], Code.Call_rel32_64))
            return null;

        return new Shape(body[2].IP, body[2].NearBranchTarget,
            body[9].IP, body[9].NearBranchTarget, body[10].IP,
            body[10].NearBranchTarget);
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
