using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Authenticates a direct instance call followed by one Int32 array read from
/// its result. Both nonreturning failure arms and the callee identity are part
/// of the proof; the argument array is never used by the caller's index check.
/// </summary>
internal static class X64CallResultInt32ArrayReadProof
{
    internal sealed record Evidence(MethodAnalysisContext Target);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                !method.IsStatic || method.IsVirtual ||
                method.Definition is not { GenericContainer: null, parameterCount: 3,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
                !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
                method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                method.Parameters is not [{ } receiver, { } argument, { } index] ||
                receiver.ParameterType is not { } receiverType ||
                !NullCheckedCall.IsReferenceClass(receiverType) ||
                receiverType.IsGenericInstance || receiverType.GenericParameters.Count != 0 ||
                argument.ParameterType is not SzArrayTypeAnalysisContext { ElementType: var argumentElement } ||
                !ReferenceEquals(argumentElement, app.SystemTypes.SystemInt32Type) ||
                !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
                !ValidParameter(method, definition, receiver, 0, Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
                !ValidParameter(method, definition, argument, 1, Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY) ||
                !ValidParameter(method, definition, index, 2, Il2CppTypeEnum.IL2CPP_TYPE_I4) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var callerBindings) ||
                callerBindings is not [var caller] || !ReferenceEquals(caller, method))
                return null;

            var decoded = X86Utils.Iterate(method).ToArray();
            var completeOk = X64ArrayGuardSiteProof.TryCompleteFileBackedRegion(method, decoded,
                pe, unwind, out var body);
            if (!completeOk ||
                body.Count != 19 || !MatchesBody(body) ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 6, 0,
                    [6, 0x32, 2, 0x30]) ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[15].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, body[17].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[15].IP, body[17].IP }) != null ||
                !app.MethodsByAddress.TryGetValue(body[6].NearBranchTarget,
                    out var targetBindings) || targetBindings is not [var target] ||
                !ValidTarget(target, receiverType, argument.ParameterType))
                return null;

            return new Evidence(target);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool ValidParameter(MethodAnalysisContext method,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition,
        ParameterAnalysisContext parameter, int index, Il2CppTypeEnum type) =>
        definition.InternalParameterData is { Length: 3 } raw &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        parameter.Definition != null && parameter.Definition == raw[index] &&
        parameter.ParameterIndex == index && !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null &&
        parameter.Definition.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } rawType &&
        rawType.Type == type;

    private static bool ValidTarget(MethodAnalysisContext target,
        TypeAnalysisContext receiver, TypeAnalysisContext argument) =>
        !target.IsStatic && !target.IsVirtual &&
        target.Definition is { GenericContainer: null, parameterCount: 1,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(target.DeclaringType, receiver) &&
        ReferenceEquals(definition.DeclaringType, receiver.Definition) &&
        target.GenericParameters.Count == 0 &&
        target.Name == target.DefaultName &&
        target.Attributes == target.DefaultAttributes &&
        target.ImplAttributes == target.DefaultImplAttributes &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(target) &&
        target.Parameters is [{ } parameter] &&
        ValidTargetParameter(target, definition, parameter, argument) &&
        target.ReturnType is SzArrayTypeAnalysisContext { ElementType: var element } &&
        ReferenceEquals(element, target.AppContext.SystemTypes.SystemInt32Type);

    private static bool ValidTargetParameter(MethodAnalysisContext target,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition,
        ParameterAnalysisContext parameter, TypeAnalysisContext argument) =>
        definition.InternalParameterData is [{ } raw] &&
        parameter.Definition == raw && parameter.ParameterIndex == 0 &&
        ReferenceEquals(parameter.DeclaringMethod, target) &&
        NullCheckedCall.SameOrdinaryType(parameter.ParameterType, argument) &&
        !parameter.IsRef && parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null &&
        raw.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
            NumMods: 0, Byref: 0, Pinned: 0 };

    private static bool MatchesBody(IReadOnlyList<NativeInstruction> body)
    {
        if (body[0].IP == 0 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return false;

        return body[0].Code == Code.Push_r64 && body[0].Op0Register == NativeRegister.RBX &&
               Stack(body[1], Mnemonic.Sub) &&
               body[2].Code == Code.Movsxd_r64_rm32 &&
               Registers(body[2], Mnemonic.Movsxd, NativeRegister.RBX, NativeRegister.R8D) &&
               Registers(body[3], Mnemonic.Test, NativeRegister.RCX, NativeRegister.RCX) &&
               Branch(body[4], Mnemonic.Je, body[15].IP) &&
               Registers(body[5], Mnemonic.Xor, NativeRegister.R8D, NativeRegister.R8D) &&
               Call(body[6]) && Call(body[15]) && Call(body[17]) &&
               Registers(body[7], Mnemonic.Test, NativeRegister.RAX, NativeRegister.RAX) &&
               Branch(body[8], Mnemonic.Je, body[15].IP) &&
               body[9].Code == Code.Cmp_r32_rm32 &&
               body[9].Op0Kind == OpKind.Register && body[9].Op0Register == NativeRegister.EBX &&
               Memory(body[9], 1, NativeRegister.RAX, NativeRegister.None, 1, 0x18, 4) &&
               Branch(body[10], Mnemonic.Jae, body[17].IP) &&
               body[11].Code == Code.Mov_r32_rm32 &&
               body[11].Op0Kind == OpKind.Register && body[11].Op0Register == NativeRegister.EAX &&
               Memory(body[11], 1, NativeRegister.RAX, NativeRegister.RBX, 4, 0x20, 4) &&
               Stack(body[12], Mnemonic.Add) &&
               body[13].Code == Code.Pop_r64 && body[13].Op0Register == NativeRegister.RBX &&
               body[14].Code == Code.Retnq && body[14].OpCount == 0 &&
               body[16].Code == Code.Int3 && body[18].Code == Code.Int3;
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool Registers(NativeInstruction instruction, Mnemonic mnemonic,
        NativeRegister destination, NativeRegister source) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister basis, NativeRegister index, int scale, ulong offset, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == basis && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == size;
}
