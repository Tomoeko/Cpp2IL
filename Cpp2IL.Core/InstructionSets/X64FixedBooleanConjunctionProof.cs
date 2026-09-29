using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves two ordered fixed-index Boolean-array reads with an early false return.
/// The second field is loaded only when the first element is zero. Both array
/// null exits and both unsigned bounds exits share independently proved helpers.
/// </summary>
internal static class X64FixedBooleanConjunctionProof
{
    internal sealed record Evidence(FieldAnalysisContext First, FieldAnalysisContext Second);
    internal sealed record Shape(int FirstOffset, int SecondOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                method.IsStatic || method.IsVirtual || method.IsVoid ||
                method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
                method.GenericParameters.Count != 0 || method.Parameters.Count != 0 ||
                method.OverrideReturnType != null ||
                !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
                !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl |
                                      MethodAttributes.SpecialName)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                          MethodImplAttributes.ManagedMask |
                                          MethodImplAttributes.InternalCall)) != 0 ||
                method.DeclaringType is not { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
                owner.GenericParameters.Count != 0 ||
                owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                owner.Definition is not { PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
                method.Definition is not { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method) ||
                X64Stack28BodyProof.Read(method, 23, 96) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[20].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, body[22].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[20].IP, body[22].IP }) != null)
                return null;

            var first = BindArrayField(owner, shape.FirstOffset, app);
            var second = BindArrayField(owner, shape.SecondOffset, app);
            return first != null && second != null && !ReferenceEquals(first, second)
                ? new Evidence(first, second) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static FieldAnalysisContext? BindArrayField(TypeAnalysisContext owner,
        int offset, ApplicationAnalysisContext app)
    {
        var candidates = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        if (candidates is not [{ } field] ||
            field.Name != field.DefaultName ||
            field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            field.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, app.SystemTypes.SystemBooleanType))
            return null;
        var receiver = new LocalVariable("proved-owner",
            new ManagedRegister(null, "rcx"), owner);
        return NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
            new FieldReference(field, receiver, offset)) ? field : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body, PE pe)
    {
        if (body.Count != 23 || pe.PointerSizeBytes != 8 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !ArrayFieldLoad(body[1], out var firstOffset) ||
            !Registers(body[2], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[3], Code.Je_rel8_64, body[20].IP) ||
            !LengthCompare(body[4], pe) ||
            !Branch(body[5], Code.Jbe_rel8_64, body[22].IP) ||
            !ElementCompare(body[6], pe) ||
            !Branch(body[7], Code.Je_rel8_64, body[11].IP) ||
            !Registers(body[8], Code.Xor_r8_rm8, NativeRegister.AL, NativeRegister.AL) ||
            !X64Stack28BodyProof.Stack(body[9], Mnemonic.Add) ||
            !Return(body[10]) ||
            !ArrayFieldLoad(body[11], out var secondOffset) ||
            firstOffset == secondOffset ||
            !Registers(body[12], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[13], Code.Je_rel8_64, body[20].IP) ||
            !LengthCompare(body[14], pe) ||
            !Branch(body[15], Code.Jbe_rel8_64, body[22].IP) ||
            !ElementCompare(body[16], pe) ||
            body[17].Code != Code.Sete_rm8 || body[17].OpCount != 1 ||
            body[17].Op0Kind != OpKind.Register || body[17].Op0Register != NativeRegister.AL ||
            !X64Stack28BodyProof.Stack(body[18], Mnemonic.Add) ||
            !Return(body[19]) ||
            !Call(body[20]) || body[21].Code != Code.Int3 ||
            !Call(body[22]))
            return null;
        return new Shape(firstOffset, secondOffset);
    }

    private static bool ArrayFieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register || instruction.Op0Register != NativeRegister.RAX ||
            instruction.Op1Kind != OpKind.Memory || instruction.MemoryBase != NativeRegister.RCX ||
            instruction.MemoryIndex != NativeRegister.None || instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != 8 ||
            instruction.MemoryDisplacement64 is < 16 or > 0xFF8 ||
            instruction.MemoryDisplacement64 % 8 != 0)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool LengthCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_rm32_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor((uint)instruction.MemoryDisplacement64, pe) &&
        instruction.Op1Kind == OpKind.Immediate8to32 && instruction.Immediate8 == 0;

    private static bool ElementCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryIndexScale == 1 &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.MemoryDisplacement64 == Il2CppArrayUtils.GetFirstItemOffset(pe) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;

    private static bool Return(NativeInstruction instruction) =>
        instruction.Code == Code.Retnq && instruction.OpCount == 0;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;
}
