using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a complete direct reference-returning call and a guarded Boolean
/// tail. Runtime-provided callees remain calls to their original declarations;
/// their native implementations are neither copied nor inferred here.
/// </summary>
internal static class X64CallResultBooleanTailProof
{
    internal sealed record Evidence(MethodAnalysisContext Producer,
        MethodAnalysisContext Target, bool ChecksProducerReceiver,
        bool LiteralValue, int? ParameterIndex);

    internal sealed record Shape(ulong ProducerTarget, ulong GuardedTarget,
        ulong NullCallsite, ulong NullHelper, bool LiteralValue, bool UsesParameter);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !ValidCaller(method, out var receiver, out var parameterIndex) ||
                ReadBody(method) is not { } body ||
                TryProveShape(body, method.IsStatic, parameterIndex != null) is not { } shape ||
                !app.MethodsByAddress.TryGetValue(shape.ProducerTarget,
                    out var producers) || producers is not [var producer] ||
                !app.MethodsByAddress.TryGetValue(shape.GuardedTarget,
                    out var targets) || targets is not [var target] ||
                producer.UnderlyingPointer != shape.ProducerTarget ||
                target.UnderlyingPointer != shape.GuardedTarget ||
                !ReferenceEquals(producer.AppContext, app) || !ReferenceEquals(target.AppContext, app) ||
                !ValidTarget(producer, receiver, isProducer: true) ||
                !ValidTarget(target, producer.ReturnType, isProducer: false) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    method.DeclaringType!.DeclaringAssembly,
                    producer.DeclaringType!.DeclaringAssembly) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    method.DeclaringType.DeclaringAssembly,
                    target.DeclaringType!.DeclaringAssembly) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { shape.NullCallsite }) != null)
                return null;

            return new Evidence(producer, target, method.IsStatic, shape.LiteralValue, parameterIndex);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidCaller(MethodAnalysisContext method,
        out TypeAnalysisContext receiver, out int? parameterIndex)
    {
        receiver = null!;
        parameterIndex = null;
        if (!UnchangedMethod(method, allowInternalCall: false) ||
            !method.IsVoid ||
            method.Definition is not { RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } } ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ||
            method.DeclaringType is not { } owner ||
            !OrdinaryReferenceClass(owner))
            return false;

        if (!method.IsStatic)
        {
            if (method.Parameters.Count > 1)
                return false;
            receiver = owner;
            if (method.Parameters.Count == 1)
                parameterIndex = 0;
        }
        else
        {
            if (method.Parameters.Count is not (1 or 2) ||
                method.Parameters[0] is not { } parameter ||
                !ValidParameter(method, parameter, 0, Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
                !OrdinaryReferenceClass(parameter.ParameterType))
                return false;
            receiver = parameter.ParameterType;
            if (method.Parameters.Count == 2)
                parameterIndex = 1;
        }

        return parameterIndex is { } index
            ? ValidParameter(method, method.Parameters[index], index, Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) &&
              ReferenceEquals(method.Parameters[index].ParameterType, method.AppContext.SystemTypes.SystemBooleanType) &&
              HasRegisters(method, "rcx", "rdx", "r8")
            : HasRegisters(method, "rcx", "rdx");
    }

    private static bool ValidTarget(MethodAnalysisContext target,
        TypeAnalysisContext receiver, bool isProducer)
    {
        if (!UnchangedMethod(target, allowInternalCall: true) ||
            target.IsStatic || target.IsVirtual || target.DeclaringType is not { } owner ||
            target.Visibility != MethodAttributes.Public ||
            !OrdinaryReferenceBase(receiver, owner))
            return false;

        if (isProducer)
            return target.Parameters.Count == 0 &&
                   target.Definition is { RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } } &&
                   OrdinaryReferenceClass(target.ReturnType) &&
                   HasRegisters(target, "rcx", "rdx");

        return target.IsVoid &&
               target.Definition is { RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } } &&
               ReferenceEquals(target.ReturnType, target.AppContext.SystemTypes.SystemVoidType) &&
               target.Parameters is [var boolean] &&
               ValidParameter(target, boolean, 0, Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) &&
               ReferenceEquals(boolean.ParameterType, target.AppContext.SystemTypes.SystemBooleanType) &&
               HasRegisters(target, "rcx", "rdx", "r8");
    }

    private static bool UnchangedMethod(MethodAnalysisContext method,
        bool allowInternalCall)
    {
        var implementation = method.ImplAttributes &
            (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
             MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized);
        return method.Name is not (".ctor" or ".cctor") &&
               method.Name == method.DefaultName &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               (implementation == 0 || allowInternalCall &&
                   method.ImplAttributes == MethodImplAttributes.InternalCall) &&
               method.OverrideReturnType == null &&
               ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
               method.GenericParameters.Count == 0 &&
               method.Definition is { GenericContainer: null,
                   RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } &&
               method.DeclaringType is { Definition: { GenericContainer: null } } owner &&
               owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
               OrdinaryReferenceClass(owner) &&
               RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method);
    }

    private static bool OrdinaryReferenceClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Definition is { RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
            NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool OrdinaryReferenceBase(TypeAnalysisContext receiver,
        TypeAnalysisContext owner)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null && visited.Add(current); current = current.BaseType)
        {
            if (!OrdinaryReferenceClass(current) ||
                current.BaseType != null && current.Definition?.RawBaseType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                        NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
            if (ReferenceEquals(current, owner))
                return true;
        }
        return false;
    }

    private static bool ValidParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, int index, Il2CppTypeEnum type) =>
        parameter.ParameterIndex == index &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        method.Definition?.InternalParameterData is { } raw && index < raw.Length &&
        ReferenceEquals(parameter.Definition, raw[index]) &&
        raw[index].RawType is { NumMods: 0, Byref: 0, Pinned: 0 } rawType &&
        rawType.Type == type && parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes &&
        !parameter.IsRef && parameter.OverrideParameterType == null &&
        ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) &&
        !parameter.UseOverrideDefaultValue;

    private static bool HasRegisters(MethodAnalysisContext method, params string[] expected)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not
                X64CallingConventionResolver convention || convention.ReturnsViaHiddenBuffer(method))
            return false;
        var arguments = convention.ResolveForParameters(method);
        return arguments.Length == expected.Length && arguments.Select((argument, index) =>
            argument is ManagedRegister register &&
            register == new ManagedRegister(null, expected[index])).All(matches => matches);
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        var parameter = method.Parameters.Count == (method.IsStatic ? 2 : 1);
        var length = parameter ? (method.IsStatic ? 51U : 46U) : (method.IsStatic ? 44U : 39U);
        var count = parameter ? (method.IsStatic ? 16 : 14) : (method.IsStatic ? 13 : 11);
        if (method.RawBytes.Length < length - 1 ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.UnderlyingPointer > ulong.MaxValue - length ||
            unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1).End != method.UnderlyingPointer + length)
            return null;
        if (!parameter)
            return X64Stack28BodyProof.Read(method, count, (int)length);

        // The Boolean byte lives in saved RBX across the producer call. Authenticate
        // that frame independently and preserve any nonempty cached native prefix.
        if (!unwind.MatchesUnwind(method.UnderlyingPointer, method.UnderlyingPointer + length,
                6, 0, [6, 0x32, 2, 0x30]) ||
            X64NativeInstructionReader.ReadRootBody(method) is not { } decoded ||
            decoded.Length != count + 1 || decoded[^1].Code != Code.Int3)
            return null;
        return decoded.Take(count).ToArray();
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        bool checksProducerReceiver, bool usesParameter = false)
    {
        var shift = checksProducerReceiver ? 2 : 0;
        var count = (usesParameter ? 14 : 11) + shift;
        var length = (usesParameter ? 45 : 38) + (checksProducerReceiver ? 5 : 0);
        if (body.Count != count || body[0].IP > ulong.MaxValue - (ulong)length ||
            body[^1].NextIP != body[0].IP + (ulong)length ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        if (usesParameter)
        {
            if (!SingleRegister(body[0], Code.Push_r64, NativeRegister.RBX) || body[0].Length != 2 ||
                !SavedStack(body[1], Mnemonic.Sub) ||
                !Registers(body[2], Code.Movzx_r32_rm8, NativeRegister.EBX, NativeRegister.DL) ||
                checksProducerReceiver &&
                    (!Registers(body[3], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) ||
                     !NullBranch(body[4], body[^1].IP)) ||
                !Registers(body[3 + shift], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
                !DirectTransfer(body[4 + shift], Code.Call_rel32_64) ||
                !Registers(body[5 + shift], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
                !NullBranch(body[6 + shift], body[^1].IP) ||
                !Registers(body[7 + shift], Code.Xor_r32_rm32, NativeRegister.R8D, NativeRegister.R8D) ||
                !Registers(body[8 + shift], Code.Movzx_r32_rm8, NativeRegister.EDX, NativeRegister.BL) ||
                !Registers(body[9 + shift], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
                !SavedStack(body[10 + shift], Mnemonic.Add) ||
                !SingleRegister(body[11 + shift], Code.Pop_r64, NativeRegister.RBX) ||
                !DirectTransfer(body[12 + shift], Code.Jmp_rel32_64) ||
                !DirectTransfer(body[13 + shift], Code.Call_rel32_64))
                return null;
            return new Shape(body[4 + shift].NearBranchTarget, body[12 + shift].NearBranchTarget,
                body[^1].IP, body[^1].NearBranchTarget, false, true);
        }

        if (body[0].Code != Code.Sub_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            checksProducerReceiver &&
                (!Registers(body[1], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) ||
                 !NullBranch(body[2], body[^1].IP)) ||
            !Registers(body[1 + shift], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !DirectTransfer(body[2 + shift], Code.Call_rel32_64) ||
            !Registers(body[3 + shift], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !NullBranch(body[4 + shift], body[^1].IP) ||
            !Registers(body[5 + shift], Code.Xor_r32_rm32, NativeRegister.R8D, NativeRegister.R8D) ||
            !BooleanLiteral(body[6 + shift]) ||
            !Registers(body[7 + shift], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
            body[8 + shift].Code != Code.Add_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[8 + shift], Mnemonic.Add) ||
            !DirectTransfer(body[9 + shift], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[10 + shift], Code.Call_rel32_64))
            return null;

        return new Shape(body[2 + shift].NearBranchTarget, body[9 + shift].NearBranchTarget,
            body[^1].IP, body[^1].NearBranchTarget, body[6 + shift].Code == Code.Mov_r8_imm8, false);
    }

    private static bool BooleanLiteral(NativeInstruction instruction) =>
        Registers(instruction, Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
        instruction.Code == Code.Mov_r8_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.DL &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool SavedStack(NativeInstruction instruction, Mnemonic operation) =>
        instruction.Mnemonic == operation && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x20;

    private static bool SingleRegister(NativeInstruction instruction, Code code, NativeRegister register) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool NullBranch(NativeInstruction instruction, ulong target) =>
        instruction.Code == Code.Je_rel8_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectTransfer(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.Length == 5 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;
}
