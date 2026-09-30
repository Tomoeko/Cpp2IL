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
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete field-backed Boolean array fill loop. Each successful
/// store is followed by a fresh field load and null check before the next
/// signed length test. The independent unsigned element guard remains part
/// of the evidence; this is not a bulk clear or a cached-array traversal.
/// </summary>
internal static class X64BooleanArrayFillLoopProof
{
    internal sealed record Shape(int FieldOffset, bool UsesParameter,
        int NullCall, int BoundsCall);

    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        bool UsesParameter);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE || !OrdinaryMethod(method) ||
                ReadBody(method) is not { } body ||
                TryProveShape(body) is not { } shape ||
                method.Parameters.Count != (shape.UsesParameter ? 1 : 0) ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[shape.NullCall].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[shape.BoundsCall].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>
                    {
                        body[shape.NullCall].IP, body[shape.BoundsCall].IP
                    }) != null)
                return null;

            var owner = method.DeclaringType!;
            var fields = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (fields is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawArray ||
                rawArray.GetEncapsulatedType() is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, app.SystemTypes.SystemBooleanType) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(arrayField,
                        new LocalVariable("proved-owner",
                            new ManagedRegister(null, "rcx"), owner),
                        shape.FieldOffset)))
                return null;
            return new Evidence(arrayField, shape.UsesParameter);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        foreach (var count in new[] { 28, 29 })
            if (X64Stack28BodyProof.Read(method, count, 128) is { } body &&
                TryProveShape(body) != null)
                return body;
        return null;
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not
                { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 },
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !ReferenceEquals(method.DefaultReturnType, app.SystemTypes.SystemVoidType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall |
                MethodImplAttributes.Synchronized)) != 0 ||
            method.Definition is not { GenericContainer: null,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.parameterCount != method.Parameters.Count ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method))
            return false;

        if (method.Parameters.Count == 0)
            return (definition.InternalParameterData?.Length ?? 0) == 0;
        return method.Parameters is [{ } parameter] &&
               definition.InternalParameterData is [{ } rawParameter] &&
               ReferenceEquals(parameter.Definition, rawParameter) &&
               ReferenceEquals(parameter.DeclaringMethod, method) &&
               parameter.ParameterIndex == 0 && !parameter.IsRef &&
               parameter.Name == parameter.DefaultName &&
               parameter.Attributes == parameter.DefaultAttributes &&
               parameter.OverrideParameterType == null &&
               ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemBooleanType) &&
               rawParameter.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                   NumMods: 0, Byref: 0, Pinned: 0 };
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (28 or 29) ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub))
            return null;

        var parameter = Registers(body[2], Code.Xor_r32_rm32,
            NativeRegister.R8D, NativeRegister.R8D);
        var index = parameter ? NativeRegister.R8D : NativeRegister.EDX;
        var lengthIndex = parameter ? NativeRegister.R10D : NativeRegister.R9D;
        var array = parameter ? NativeRegister.R9 : NativeRegister.R8;
        var owner = parameter ? NativeRegister.R11 : NativeRegister.R10;
        var shift = body.Count == 29 ? 1 : 0;
        var loop = 8 + shift;
        var nullCall = 25 + shift;
        var boundsCall = 27 + shift;
        var loadedIntoArray = body[1].Op0Register == array;
        var initialCopies = loadedIntoArray
            ? Registers(body[5], Code.Test_rm64_r64, array, array) &&
              Branch(body[6], Code.Je_rel8_64, body[nullCall].IP) &&
              Registers(body[7], Code.Mov_r64_rm64, NativeRegister.RAX, array)
            : body[1].Op0Register == NativeRegister.RAX &&
              Registers(body[5], Code.Mov_r64_rm64, array, NativeRegister.RAX) &&
              Registers(body[6], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) &&
              Branch(body[7], Code.Je_rel8_64, body[nullCall].IP);

        if (body[1].Code != Code.Mov_r64_rm64 ||
            body[1].Op0Kind != OpKind.Register || !initialCopies ||
            body[1].MemoryDisplacement64 > int.MaxValue ||
            !Memory(body[1], 1, NativeRegister.RCX, NativeRegister.None,
                1, body[1].MemoryDisplacement64, 8) ||
            !Registers(body[2], Code.Xor_r32_rm32, index, index) ||
            !Registers(body[3], Code.Mov_r64_rm64, owner, NativeRegister.RCX) ||
            !Registers(body[4], Code.Mov_r32_rm32, lengthIndex, index) ||
            shift != 0 && body[8].Mnemonic != Mnemonic.Nop ||
            !LengthCompare(body[loop], lengthIndex, array) ||
            !Branch(body[loop + 1], Code.Jge_rel8_64, body[23 + shift].IP) ||
            !Registers(body[loop + 2], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[loop + 3], Code.Je_rel8_64, body[nullCall].IP) ||
            !LengthCompare(body[loop + 4], index, NativeRegister.RAX) ||
            !Branch(body[loop + 5], Code.Jae_rel8_64, body[boundsCall].IP) ||
            !Registers(body[loop + 6], Code.Movsxd_r64_rm32, NativeRegister.RCX, index) ||
            body[loop + 7].Code != Code.Inc_rm32 ||
            body[loop + 7].Op0Kind != OpKind.Register ||
            body[loop + 7].Op0Register != index ||
            !Registers(body[loop + 8], Code.Mov_r32_rm32, lengthIndex, index) ||
            !Store(body[loop + 9], parameter) ||
            body[loop + 10].Code != Code.Mov_r64_rm64 ||
            body[loop + 10].Op0Kind != OpKind.Register ||
            body[loop + 10].Op0Register != NativeRegister.RAX ||
            !Memory(body[loop + 10], 1, owner, NativeRegister.None,
                1, body[1].MemoryDisplacement64, 8) ||
            !Registers(body[loop + 11], Code.Mov_r64_rm64, array, NativeRegister.RAX) ||
            !Registers(body[loop + 12], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[loop + 13], Code.Je_rel8_64, body[nullCall].IP) ||
            !Branch(body[loop + 14], Code.Jmp_rel8_64, body[loop].IP) ||
            !X64Stack28BodyProof.Stack(body[23 + shift], Mnemonic.Add) ||
            body[24 + shift].Code != Code.Retnq || body[24 + shift].OpCount != 0 ||
            body[nullCall].Code != Code.Call_rel32_64 ||
            body[nullCall].NearBranchTarget == 0 ||
            body[26 + shift].Code != Code.Int3 ||
            body[boundsCall].Code != Code.Call_rel32_64 ||
            body[boundsCall].NearBranchTarget == 0)
            return null;
        return new Shape((int)body[1].MemoryDisplacement64, parameter, nullCall, boundsCall);
    }

    private static bool Store(NativeInstruction instruction, bool parameter) =>
        Memory(instruction, 0, NativeRegister.RCX, NativeRegister.RAX,
            1, (ulong)Il2CppArrayUtils.GetFirstItemOffset(8), 1) &&
        (parameter
            ? instruction.Code == Code.Mov_rm8_r8 &&
              instruction.Op1Kind == OpKind.Register && instruction.Op1Register == NativeRegister.DL
            : instruction.Code == Code.Mov_rm8_imm8 &&
              instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0);

    private static bool LengthCompare(NativeInstruction instruction,
        NativeRegister index, NativeRegister array) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == index &&
        Memory(instruction, 1, array, NativeRegister.None,
            1, (ulong)Il2CppArrayUtils.GetLengthOffset(8), 4);

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        (instruction.Code == code || instruction.Code == (code switch
        {
            Code.Mov_r64_rm64 => Code.Mov_rm64_r64,
            Code.Mov_r32_rm32 => Code.Mov_rm32_r32,
            Code.Xor_r32_rm32 => Code.Xor_rm32_r32,
            _ => code
        })) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale,
        ulong displacement, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == displacement &&
        instruction.MemorySize.GetSize() == size;
}
