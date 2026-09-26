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
/// Proves a Boolean literal store through an instance's reference-array element.
/// Array null, unsigned bounds and element null checks precede the only store.
/// </summary>
internal static class X64ArrayElementBooleanStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        FieldAnalysisContext ValueField, bool Value);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || method.IsStatic || method.IsVirtual ||
                !method.IsVoid || method.Name is ".ctor" or ".cctor" ||
                method.Name != method.DefaultName || method.OverrideReturnType != null ||
                method.GenericParameters.Count != 0 ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract |
                    MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
                method.DeclaringType is not { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                !UnchangedType(owner) ||
                owner.Properties.Any(property => ReferenceEquals(property.Getter, method) ||
                    ReferenceEquals(property.Setter, method)) ||
                method.Definition is not { GenericContainer: null, parameterCount: 1,
                    InternalParameterData: [var rawParameter],
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                method.Parameters is not [var parameter] ||
                parameter.ParameterIndex != 0 || parameter.IsRef ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                !ReferenceEquals(parameter.Definition, rawParameter) ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
                rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                X64Stack28BodyProof.Read(method, 16, 96) is not { } body ||
                !ArrayLoad(body[1], out var arrayOffset) ||
                !Registers(body[2], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
                !Branch(body[3], Code.Je_rel8_64, body[13].IP) ||
                !LengthCompare(body[4], pe) ||
                !Branch(body[5], Code.Jae_rel8_64, body[15].IP) ||
                !Registers(body[6], Code.Movsxd_r64_rm32, NativeRegister.RAX, NativeRegister.EDX) ||
                !ElementLoad(body[7], pe) ||
                !Registers(body[8], Code.Test_rm64_r64, NativeRegister.RCX, NativeRegister.RCX) ||
                !Branch(body[9], Code.Je_rel8_64, body[13].IP) ||
                !BooleanStore(body[10], out var valueOffset, out var value) ||
                !X64Stack28BodyProof.Stack(body[11], Mnemonic.Add) ||
                body[12].Code != Code.Retnq || body[12].OpCount != 0 ||
                body[13].Code != Code.Call_rel32_64 || body[14].Code != Code.Int3 ||
                body[15].Code != Code.Call_rel32_64 ||
                X86RuntimeNullThrowProof.TryIdentify(app, body[13].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, body[15].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[13].IP, body[15].IP }) != null)
                return null;

            var arrays = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == (long)arrayOffset).ToArray();
            if (arrays is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
                element.Definition is not { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } ||
                !NullCheckedCall.IsReferenceClass(element) || !UnchangedType(element))
                return null;
            var ownerLocal = new LocalVariable("proved-owner",
                new ManagedRegister(null, "proved-owner"), owner);
            if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(arrayField, ownerLocal, (int)arrayOffset)))
                return null;

            var values = element.Fields.Where(field => !field.IsStatic &&
                field.Offset == (long)valueOffset).ToArray();
            if (values is not [{ } valueField] ||
                valueField.Name != valueField.DefaultName ||
                (valueField.Attributes & FieldAttributes.InitOnly) != 0 ||
                !ReferenceEquals(valueField.FieldType, app.SystemTypes.SystemBooleanType) ||
                valueField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !ReferenceEquals(element, owner) &&
                (element.DeclaringType != null || element.Visibility != TypeAttributes.Public ||
                    valueField.Visibility != FieldAttributes.Public))
                return null;
            var elementLocal = new LocalVariable("proved-element",
                new ManagedRegister(null, "proved-element"), element);
            if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new FieldReference(valueField, elementLocal, (int)valueOffset), 8))
                return null;

            return new Evidence(arrayField, valueField, value);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool UnchangedType(TypeAnalysisContext type) =>
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType);

    private static bool ArrayLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 &&
            instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.R8 &&
            Memory(instruction, 1, NativeRegister.RCX, NativeRegister.None, 1, offset, 8) &&
            offset <= 0x1000 - 8;
    }

    private static bool LengthCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.EDX &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor((uint)instruction.MemoryDisplacement64, pe) &&
        Memory(instruction, 1, NativeRegister.R8, NativeRegister.None, 1,
            instruction.MemoryDisplacement64, 4);

    private static bool ElementLoad(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RCX &&
        Memory(instruction, 1, NativeRegister.R8, NativeRegister.RAX, pe.PointerSizeBytes,
            Il2CppArrayUtils.GetFirstItemOffset(pe), pe.PointerSizeBytes);

    private static bool BooleanStore(NativeInstruction instruction, out ulong offset, out bool value)
    {
        offset = instruction.MemoryDisplacement64;
        value = instruction.Immediate8 == 1;
        return instruction.Code == Code.Mov_rm8_imm8 &&
            Memory(instruction, 0, NativeRegister.RCX, NativeRegister.None, 1, offset, 1) &&
            instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 is 0 or 1 &&
            offset <= int.MaxValue;
    }

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale, ulong offset, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == size;
}
