using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using LiftedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Authenticates two ordered call-result null checks in one complete body.
/// The first guarded Boolean call has already acquired its implicit check;
/// this proof binds the second no-argument terminal call and shared null exit.
/// </summary>
internal static class DualCallResultTailNullGuardProof
{
    private static readonly byte[] SavedRbxFrame = [6, 0x32, 2, 0x30];

    internal readonly record struct Shape(ulong FirstProducerCallsite,
        ulong FirstProducerTarget, ulong FirstSinkCallsite,
        ulong FirstSinkTarget, ulong SecondProducerCallsite,
        ulong SecondProducerTarget, ulong TailCallsite, ulong TailTarget,
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
                caller.IsStatic || !caller.IsVirtual || !caller.IsVoid ||
                caller.Parameters.Count != 0 ||
                caller.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } ||
                caller.DeclaringType is not { } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                caller.ParameterLocals is not [{ } thisLocal] ||
                !thisLocal.IsThis || !ReferenceEquals(thisLocal.Type, owner) ||
                !ReferenceEquals(producerReceiver, thisLocal) ||
                !SameAssemblyFoldedCallers(caller) ||
                !OrdinaryReferenceGetter(producer, app, owner) ||
                !OrdinaryNoArgumentSink(target, app, result.Type) ||
                !GetterCall(origin, producer, result, thisLocal) ||
                guardedCall.OpCode != OpCode.CallVoid ||
                guardedCall.Operands.Count is not (2 or 3) ||
                !ReferenceEquals(guardedCall.Operands[0], target) ||
                !ReferenceEquals(guardedCall.Operands[1], result) ||
                guardedCall.Operands.Count == 3 &&
                guardedCall.Operands[2] is not Immediate { Value: 0 } ||
                caller.ControlFlowGraph is not { } graph)
                return false;

            var calls = graph.Instructions.Where(instruction =>
                instruction.IsCall).ToArray();
            if (calls is not [var firstOrigin, var firstGuarded,
                    var secondOrigin, var secondGuarded] ||
                !ReferenceEquals(secondOrigin, origin) ||
                !ReferenceEquals(secondGuarded, guardedCall) ||
                firstOrigin.Destination is not LocalVariable firstResult ||
                ReferenceEquals(firstResult, result) ||
                firstOrigin.Operands.FirstOrDefault() is not
                    MethodAnalysisContext firstProducer ||
                firstGuarded.Operands.FirstOrDefault() is not
                    MethodAnalysisContext firstSink ||
                firstOrigin.Operands.ElementAtOrDefault(2) is not
                    LocalVariable firstReceiver ||
                !ReferenceEquals(firstReceiver, thisLocal) ||
                !OrdinaryReferenceGetter(firstProducer, app, owner) ||
                !OrdinaryBooleanSink(firstSink, app, firstResult.Type) ||
                !GetterCall(firstOrigin, firstProducer, firstResult,
                    thisLocal) ||
                firstGuarded.OpCode != OpCode.CallVoid ||
                firstGuarded.CallSemantics !=
                    CallSemantics.NullCheckedInstance ||
                firstGuarded.Operands.Count is not (3 or 4) ||
                !ReferenceEquals(firstGuarded.Operands[0], firstSink) ||
                !ReferenceEquals(firstGuarded.Operands[1], firstResult) ||
                firstGuarded.Operands[2] is not Immediate { Value: 0 } ||
                firstGuarded.Operands.Count == 4 &&
                firstGuarded.Operands[3] is not Immediate { Value: 0 } ||
                !CallResultNullGuardProof.HasBoundTarget(caller,
                    firstResult, firstOrigin, firstGuarded, firstSink) ||
                graph.Instructions.Any(instruction => instruction.OpCode is
                    OpCode.UnresolvedValue or OpCode.NotImplemented or
                    OpCode.IndirectCall) ||
                firstOrigin.NativeAddress == null ||
                firstGuarded.NativeAddress == null ||
                origin.NativeAddress == null ||
                guardedCall.NativeAddress == null ||
                ReadBody(caller) is not { } body ||
                TryProveShape(body) is not { } shape ||
                firstOrigin.NativeAddress != shape.FirstProducerCallsite ||
                firstProducer.UnderlyingPointer != shape.FirstProducerTarget ||
                firstGuarded.NativeAddress != shape.FirstSinkCallsite ||
                firstSink.UnderlyingPointer != shape.FirstSinkTarget ||
                origin.NativeAddress != shape.SecondProducerCallsite ||
                producer.UnderlyingPointer != shape.SecondProducerTarget ||
                guardedCall.NativeAddress != shape.TailCallsite ||
                target.UnderlyingPointer != shape.TailTarget ||
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

    private static bool GetterCall(LiftedInstruction call,
        MethodAnalysisContext producer, LocalVariable result,
        LocalVariable receiver) =>
        call is { OpCode: OpCode.Call, IntegerBitWidth: 0,
            CallSemantics: CallSemantics.Direct } &&
        ReferenceEquals(call.Destination, result) &&
        call.Operands.Count is 3 or 4 &&
        ReferenceEquals(call.Operands[0], producer) &&
        ReferenceEquals(call.Operands[2], receiver) &&
        (call.Operands.Count == 3 ||
         call.Operands[3] is Immediate { Value: 0 });

    private static bool OrdinaryReferenceGetter(MethodAnalysisContext method,
        ApplicationAnalysisContext app, TypeAnalysisContext owner) =>
        OrdinaryMethod(method, app, requireUniqueBinding: false) &&
        !method.IsStatic && !method.IsVirtual &&
        method.Parameters.Count == 0 &&
        method.Definition?.RawReturnType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } &&
        NullCheckedCall.IsReferenceClass(method.ReturnType) &&
        method.DeclaringType is { } producerOwner &&
        NullCheckedCall.HasUnchangedReferenceBase(owner, producerOwner);

    private static bool OrdinaryBooleanSink(MethodAnalysisContext method,
        ApplicationAnalysisContext app, TypeAnalysisContext? receiver) =>
        OrdinaryMethod(method, app, requireUniqueBinding: false) &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Definition?.RawReturnType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } &&
        method.DeclaringType is { } sinkOwner &&
        NullCheckedCall.HasUnchangedReferenceBase(receiver, sinkOwner) &&
        method.Parameters is [{ } argument] &&
        ReferenceEquals(argument.ParameterType,
            app.SystemTypes.SystemBooleanType) &&
        argument.Definition?.RawType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 };

    private static bool OrdinaryNoArgumentSink(MethodAnalysisContext method,
        ApplicationAnalysisContext app, TypeAnalysisContext? receiver) =>
        OrdinaryMethod(method, app, requireUniqueBinding: false) &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Definition?.RawReturnType is
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } &&
        method.Parameters.Count == 0 &&
        method.DeclaringType is { } sinkOwner &&
        NullCheckedCall.HasUnchangedReferenceBase(receiver, sinkOwner);

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        ApplicationAnalysisContext app, bool requireUniqueBinding) =>
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
            !candidate.IsStatic && candidate.IsVirtual && candidate.IsVoid &&
            candidate.Parameters.Count == 0 &&
            candidate.Definition?.RawReturnType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } &&
            candidate.DeclaringType is { } owner &&
            NullCheckedCall.IsReferenceClass(owner));
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext caller)
    {
        var app = caller.AppContext;
        if (app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            caller.UnderlyingPointer is 0 or ulong.MaxValue ||
            caller.UnderlyingPointer > ulong.MaxValue - 70)
            return null;
        var start = caller.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End != start + 70 ||
            !unwind.MatchesUnwind(start, region.End, 6, 0,
                SavedRbxFrame) ||
            app.MethodsByAddress.Keys.Any(address => address > start &&
                address < region.End))
            return null;

        caller.EnsureRawBytes();
        if (caller.RawBytes.Length != 69 ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                unwind, caller.RawBytes.AsSpan(), start) ||
            !X64NativePaddingProof.HasInt3Padding(pe, start + 69,
                region.End))
            return null;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(region.End - 1, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - 70 ||
            last != first + 69 ||
            Enumerable.Range(0, 70).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(start +
                    (ulong)offset - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset,
                    false) != first + offset))
            return null;
        var body = X86Utils.Iterate(caller).ToArray();
        return body.Length == 22 && body[0].IP == start &&
               body[^1].NextIP == start + 69 ? body : null;
    }

    // This check also accepts a decoded instruction list for mutation tests.
    // ReadBody and HasBoundTarget supply PE, unwind, metadata, and helper proof.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 22 || body[0].IP > ulong.MaxValue - 69 ||
            body[^1].NextIP != body[0].IP + 69 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !Register(body[0], Code.Push_r64, NativeRegister.RBX) ||
            !Stack(body[1], Mnemonic.Sub) ||
            !Registers(body[2], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[3], Code.Mov_r64_rm64,
                NativeRegister.RBX, NativeRegister.RCX) ||
            !DirectTransfer(body[4], Code.Call_rel32_64) ||
            !Registers(body[5], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            !NullBranch(body[6], body[21].IP) ||
            !Registers(body[7], Code.Xor_r32_rm32,
                NativeRegister.R8D, NativeRegister.R8D) ||
            !Registers(body[8], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[9], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            !DirectTransfer(body[10], Code.Call_rel32_64) ||
            !Registers(body[11], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[12], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectTransfer(body[13], Code.Call_rel32_64) ||
            !Registers(body[14], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            !NullBranch(body[15], body[21].IP) ||
            !Registers(body[16], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[17], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            !Stack(body[18], Mnemonic.Add) ||
            !Register(body[19], Code.Pop_r64, NativeRegister.RBX) ||
            !DirectTransfer(body[20], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[21], Code.Call_rel32_64))
            return null;

        return new Shape(body[4].IP, body[4].NearBranchTarget,
            body[10].IP, body[10].NearBranchTarget,
            body[13].IP, body[13].NearBranchTarget,
            body[20].IP, body[20].NearBranchTarget,
            body[21].IP, body[21].NearBranchTarget);
    }

    private static bool Register(NativeInstruction instruction, Code code,
        NativeRegister register) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Stack(NativeInstruction instruction,
        Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or
            OpKind.Immediate32to64 && instruction.GetImmediate(1) == 0x20;

    private static bool NullBranch(NativeInstruction instruction,
        ulong helper) => instruction.Code == Code.Je_rel8_64 &&
                     instruction.Op0Kind == OpKind.NearBranch64 &&
                     instruction.NearBranchTarget == helper;

    private static bool DirectTransfer(NativeInstruction instruction,
        Code code) => instruction.Code == code && instruction.Length == 5 &&
                     instruction.Op0Kind == OpKind.NearBranch64;
}
