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
/// Proves a complete virtual base call, direct reference getter, and guarded
/// terminal call in their native order. The existing null-guard rewrite
/// then retains the first two calls and gives the last one its implicit check.
/// </summary>
internal static class OrderedCallResultTailNullGuardProof
{
    private enum TailVariant { BooleanTrue, NoArgument }

    private static readonly byte[] SavedRbxFrame = [6, 0x32, 2, 0x30];

    internal readonly record struct Shape(ulong EffectCallsite, ulong EffectTarget,
        ulong ProducerCallsite, ulong ProducerTarget, ulong TailCallsite,
        ulong TailTarget, ulong NullCallsite, ulong NullHelper);

    internal static bool HasBoundTarget(MethodAnalysisContext caller, LocalVariable result,
        LiftedInstruction origin, LiftedInstruction guardedCall,
        MethodAnalysisContext producer, LocalVariable producerReceiver,
        MethodAnalysisContext target)
    {
        try
        {
            var app = caller.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !caller.IsVirtual || caller.IsStatic || !caller.IsVoid ||
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
                !thisLocal.IsThis || !ReferenceEquals(thisLocal.Type, owner) ||
                !ReferenceEquals(producerReceiver, thisLocal) ||
                !OrdinaryCallTarget(producer, app) || producer.IsStatic ||
                producer.IsVirtual || producer.Parameters.Count != 0 ||
                producer.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !NullCheckedCall.IsReferenceClass(producer.ReturnType) ||
                !NullCheckedCall.HasUnchangedReferenceBase(owner, producer.DeclaringType!) ||
                !OrdinaryCallTarget(target, app) || target.IsStatic ||
                target.IsVirtual || !target.IsVoid ||
                !NullCheckedCall.HasUnchangedReferenceBase(result.Type,
                    target.DeclaringType!) ||
                origin.NativeAddress == null || guardedCall.NativeAddress == null ||
                !ReferenceEquals(origin.Destination, result) ||
                origin.Operands is not [MethodAnalysisContext, LocalVariable,
                    LocalVariable, ..] ||
                !ReferenceEquals(origin.Operands[0], producer) ||
                !ReferenceEquals(origin.Operands[2], thisLocal) ||
                guardedCall.OpCode != OpCode.CallVoid ||
                !ReferenceEquals(guardedCall.Operands[0], target) ||
                !ReferenceEquals(guardedCall.Operands[1], result) ||
                caller.ControlFlowGraph is not { } graph)
                return false;

            var variant = TailVariantFor(target, guardedCall, app);
            if (variant == null)
                return false;

            var calls = graph.Instructions.Where(instruction => instruction.IsCall).ToArray();
            if (calls is not [var effectCall, var producerCall, var tailCall] ||
                !ReferenceEquals(producerCall, origin) ||
                !ReferenceEquals(tailCall, guardedCall) ||
                effectCall is not { OpCode: OpCode.CallVoid,
                    IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct } ||
                effectCall.Operands.Count is not (2 or 3) ||
                effectCall.Operands[0] is not MethodAnalysisContext effect ||
                !ReferenceEquals(effectCall.Operands[1], thisLocal) ||
                effectCall.Operands.Count == 3 &&
                effectCall.Operands[2] is not Immediate { Value: 0 } ||
                !OrdinaryCallTarget(effect, app) || !effect.IsVirtual ||
                effect.IsStatic || !effect.IsVoid || effect.Parameters.Count != 0 ||
                effect.Definition?.RawReturnType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !NullCheckedCall.HasUnchangedReferenceBase(owner,
                    effect.DeclaringType!) ||
                graph.Instructions.Any(instruction => instruction.OpCode is
                    OpCode.UnresolvedValue or OpCode.NotImplemented or OpCode.IndirectCall) ||
                ReadBody(caller, variant.Value) is not { } body ||
                TryProveShape(body, variant.Value) is not { } shape ||
                effectCall.NativeAddress != shape.EffectCallsite ||
                effect.UnderlyingPointer != shape.EffectTarget ||
                origin.NativeAddress != shape.ProducerCallsite ||
                producer.UnderlyingPointer != shape.ProducerTarget ||
                guardedCall.NativeAddress != shape.TailCallsite ||
                target.UnderlyingPointer != shape.TailTarget ||
                !UniqueBinding(app, caller) ||
                !UniqueBinding(app, effect) ||
                !UniqueBinding(app, producer) ||
                !UniqueBinding(app, target) ||
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

    private static bool OrdinaryCallTarget(MethodAnalysisContext target,
        ApplicationAnalysisContext app) =>
        ReferenceEquals(target.AppContext, app) &&
        target.Name is not (".ctor" or ".cctor") &&
        target.Name == target.DefaultName &&
        target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes &&
        (target.Attributes & (MethodAttributes.Abstract |
                              MethodAttributes.PinvokeImpl)) == 0 &&
        (target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall)) == 0 &&
        target.GenericParameters.Count == 0 &&
        target.Definition is { GenericContainer: null,
            RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        definition.parameterCount == target.Parameters.Count &&
        (definition.InternalParameterData?.Length ?? 0) == target.Parameters.Count &&
        target.DeclaringType is { Definition: { GenericContainer: null } } owner &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        target.OverrideReturnType == null &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(target);

    private static bool UniqueBinding(ApplicationAnalysisContext app,
        MethodAnalysisContext target) =>
        app.MethodsByAddress.TryGetValue(target.UnderlyingPointer,
            out var bindings) &&
        bindings is [var bound] && ReferenceEquals(bound, target);

    private static TailVariant? TailVariantFor(MethodAnalysisContext target,
        LiftedInstruction guardedCall, ApplicationAnalysisContext app)
    {
        if (target.Parameters is [{ } argument] &&
            ReferenceEquals(argument.ParameterType,
                app.SystemTypes.SystemBooleanType) &&
            argument.Definition?.RawType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } &&
            guardedCall.Operands.Count is 3 or 4 &&
            guardedCall.Operands[2] is Immediate { Value: 1 } &&
            (guardedCall.Operands.Count == 3 ||
             guardedCall.Operands[3] is Immediate { Value: 0 }))
            return TailVariant.BooleanTrue;
        if (target.Parameters.Count == 0 &&
            (guardedCall.Operands.Count == 2 ||
             guardedCall.Operands.Count == 3 &&
             guardedCall.Operands[2] is Immediate { Value: 0 }))
            return TailVariant.NoArgument;
        return null;
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method) =>
        ReadBody(method, TailVariant.BooleanTrue) ??
        ReadBody(method, TailVariant.NoArgument);

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method,
        TailVariant variant)
    {
        var app = method.AppContext;
        var bodyLength = variant == TailVariant.BooleanTrue ? 54 : 51;
        var instructionCount = variant == TailVariant.BooleanTrue ? 17 : 16;
        if (app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.UnderlyingPointer > ulong.MaxValue - (ulong)bodyLength - 1)
            return null;

        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End != start + (ulong)bodyLength + 1 ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame) ||
            app.MethodsByAddress.Keys.Any(address => address > start &&
                address < region.End))
            return null;

        method.EnsureRawBytes();
        if (method.RawBytes.Length != bodyLength ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan(), start) ||
            !X64NativePaddingProof.HasInt3Padding(pe,
                start + (ulong)bodyLength, region.End))
            return null;

        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(region.End - 1, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - bodyLength - 1 ||
            last != first + bodyLength ||
            Enumerable.Range(0, bodyLength + 1).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(start +
                    (ulong)offset - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset,
                    false) != first + offset))
            return null;

        var body = X86Utils.Iterate(method).ToArray();
        return body.Length == instructionCount && body[0].IP == start &&
               body[^1].NextIP == start + (ulong)bodyLength ? body : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count == 17)
            return TryProveShape(body, TailVariant.BooleanTrue);
        if (body.Count == 16)
            return TryProveShape(body, TailVariant.NoArgument);
        return null;
    }

    private static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        TailVariant variant)
    {
        var bodyLength = variant == TailVariant.BooleanTrue ? 54 : 51;
        var instructionCount = variant == TailVariant.BooleanTrue ? 17 : 16;
        var nullCallIndex = instructionCount - 1;
        var tailIndex = instructionCount - 2;
        if (body.Count != instructionCount ||
            body[0].IP > ulong.MaxValue - (ulong)bodyLength ||
            body[^1].NextIP != body[0].IP + (ulong)bodyLength ||
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
            !Registers(body[5], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[6], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectTransfer(body[7], Code.Call_rel32_64) ||
            !Registers(body[8], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            body[9].Code != Code.Je_rel8_64 ||
            body[9].Op0Kind != OpKind.NearBranch64 ||
            body[9].NearBranchTarget != body[nullCallIndex].IP ||
            !(variant == TailVariant.BooleanTrue
                ? Registers(body[10], Code.Xor_r32_rm32,
                    NativeRegister.R8D, NativeRegister.R8D) &&
                  body[11].Code == Code.Mov_r8_imm8 &&
                  body[11].Op0Kind == OpKind.Register &&
                  body[11].Op0Register == NativeRegister.DL &&
                  body[11].Op1Kind == OpKind.Immediate8 &&
                  body[11].Immediate8 == 1
                : Registers(body[10], Code.Xor_r32_rm32,
                    NativeRegister.EDX, NativeRegister.EDX)) ||
            !Registers(body[tailIndex - 3], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            !Stack(body[tailIndex - 2], Mnemonic.Add) ||
            !Register(body[tailIndex - 1], Code.Pop_r64, NativeRegister.RBX) ||
            !DirectTransfer(body[tailIndex], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[nullCallIndex], Code.Call_rel32_64))
            return null;

        return new Shape(body[4].IP, body[4].NearBranchTarget,
            body[7].IP, body[7].NearBranchTarget,
            body[tailIndex].IP, body[tailIndex].NearBranchTarget,
            body[nullCallIndex].IP, body[nullCallIndex].NearBranchTarget);
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

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool DirectTransfer(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.Length == 5 &&
        instruction.Op0Kind == OpKind.NearBranch64;
}
