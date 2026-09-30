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
/// Binds a complete direct reference-returning call and a guarded false Boolean
/// tail. Runtime-provided callees remain calls to their original declarations;
/// their native implementations are neither copied nor inferred here.
/// </summary>
internal static class X64CallResultBooleanFalseTailProof
{
    internal sealed record Evidence(MethodAnalysisContext Producer,
        MethodAnalysisContext Target, bool ChecksProducerReceiver);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                !ValidCaller(method, out var receiver) ||
                ReadBody(method) is not { } body ||
                TryProveShape(body, method.IsStatic) is not { } shape ||
                !app.MethodsByAddress.TryGetValue(shape.ProducerTarget,
                    out var producers) || producers is not [var producer] ||
                !app.MethodsByAddress.TryGetValue(shape.GuardedTarget,
                    out var targets) || targets is not [var target] ||
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

            return new Evidence(producer, target, method.IsStatic);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidCaller(MethodAnalysisContext method,
        out TypeAnalysisContext receiver)
    {
        receiver = null!;
        if (!UnchangedMethod(method, allowInternalCall: false) ||
            !method.IsVoid || method.IsVirtual ||
            method.Definition is not { RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID } } ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ||
            method.DeclaringType is not { } owner ||
            !OrdinaryReferenceClass(owner))
            return false;

        if (!method.IsStatic)
        {
            if (method.Parameters.Count != 0)
                return false;
            receiver = owner;
        }
        else
        {
            if (method.Parameters is not [var parameter] ||
                !ValidParameter(method, parameter, 0, Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
                !OrdinaryReferenceClass(parameter.ParameterType))
                return false;
            receiver = parameter.ParameterType;
        }

        return HasRegisters(method, "rcx", "rdx");
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
             MethodImplAttributes.InternalCall);
        return method.Name is not (".ctor" or ".cctor") &&
               method.Name == method.DefaultName &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               (implementation == 0 || allowInternalCall &&
                   implementation == MethodImplAttributes.InternalCall) &&
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
        type.Definition is { RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
            NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool OrdinaryReferenceBase(TypeAnalysisContext receiver,
        TypeAnalysisContext owner)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null && visited.Add(current); current = current.BaseType)
        {
            if (!OrdinaryReferenceClass(current))
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
        var length = method.IsStatic ? 44U : 39U;
        if (method.RawBytes.Length < length - 1 ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.UnderlyingPointer > ulong.MaxValue - length ||
            unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1).End != method.UnderlyingPointer + length)
            return null;
        // Metadata-derived slices may contain a following native function.
        // The shared reader authenticates the independent unwind boundary,
        // exact cached prefix and padding before returning only this body.
        return X64Stack28BodyProof.Read(method, method.IsStatic ? 13 : 11, (int)length);
    }

    internal static CallResultFalseTailNullGuardProof.Shape? TryProveShape(
        IReadOnlyList<NativeInstruction> body, bool checksProducerReceiver)
    {
        if (!checksProducerReceiver)
            return CallResultFalseTailNullGuardProof.TryProveShape(body);

        if (body.Count != 13 || body[0].IP > ulong.MaxValue - 43 ||
            body[^1].NextIP != body[0].IP + 43 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Sub_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !Registers(body[1], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) ||
            !NullBranch(body[2], body[12].IP) ||
            !Registers(body[3], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !DirectTransfer(body[4], Code.Call_rel32_64) ||
            !Registers(body[5], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !NullBranch(body[6], body[12].IP) ||
            !Registers(body[7], Code.Xor_r32_rm32, NativeRegister.R8D, NativeRegister.R8D) ||
            !Registers(body[8], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[9], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
            body[10].Code != Code.Add_rm64_imm8 ||
            !X64Stack28BodyProof.Stack(body[10], Mnemonic.Add) ||
            !DirectTransfer(body[11], Code.Jmp_rel32_64) ||
            !DirectTransfer(body[12], Code.Call_rel32_64))
            return null;

        return new CallResultFalseTailNullGuardProof.Shape(body[4].IP,
            body[4].NearBranchTarget, body[11].IP, body[11].NearBranchTarget,
            body[12].IP, body[12].NearBranchTarget);
    }

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
