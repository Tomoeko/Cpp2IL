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
/// Authenticates a complete instance Boolean[] field store whose index and
/// stored byte come from distinct managed arguments. The managed array store
/// replaces the separately proved null and bounds exits.
/// </summary>
internal static class X64FieldParameterBooleanArrayStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField);
    internal sealed record Shape(int FieldOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                !OrdinaryMethod(method) ||
                !UnchangedArguments(method) ||
                X64Stack28BodyProof.Read(method, 13, 80) is not { } body ||
                TryProveShape(body, pe) is not { } shape ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    method.UnderlyingPointer,
                    checked((uint)(body[^1].NextIP - method.UnderlyingPointer))) ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[10].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[12].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[10].IP, body[12].IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var candidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (candidates is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawArray ||
                rawArray.GetEncapsulatedType() is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
                !ReferenceEquals(element, app.SystemTypes.SystemBooleanType))
                return null;

            var receiver = new LocalVariable("proved-owner",
                new ManagedRegister(null, "rcx"), owner);
            var access = new FieldReference(arrayField, receiver, shape.FieldOffset);
            return NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ||
                   NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayoutWithFieldlessConstructedBase(access)
                ? new Evidence(arrayField) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                InternalParameterData: [_, _],
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            method.DeclaringType is not { IsValueType: false, IsInterface: false,
                IsGenericInstance: false, Definition: { GenericContainer: null,
                    PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            (owner.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType))
            return false;
        return true;
    }

    private static bool UnchangedArguments(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.Definition?.InternalParameterData is not [var rawIndex, var rawValue] ||
            method.Parameters is not [var index, var value] ||
            index.ParameterIndex != 0 || value.ParameterIndex != 1 ||
            !ReferenceEquals(index.Definition, rawIndex) ||
            !ReferenceEquals(value.Definition, rawValue) ||
            !ReferenceEquals(index.DeclaringMethod, method) ||
            !ReferenceEquals(value.DeclaringMethod, method) ||
            index.IsRef || value.IsRef ||
            index.Name != index.DefaultName || value.Name != value.DefaultName ||
            index.Attributes != index.DefaultAttributes ||
            value.Attributes != value.DefaultAttributes ||
            index.OverrideParameterType != null || value.OverrideParameterType != null ||
            rawIndex.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            rawValue.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(value.ParameterType, app.SystemTypes.SystemBooleanType))
            return false;
        var resolver = new X64CallingConventionResolver();
        if (resolver.ReturnsViaHiddenBuffer(method))
            return false;
        var incoming = resolver.ResolveForParameters(method);
        return incoming is [ManagedRegister { Name: "rcx" },
            ManagedRegister { Name: "rdx" }, ManagedRegister { Name: "r8" },
            ManagedRegister { Name: "r9" }];
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body, PE pe)
    {
        if (body.Count != 13 || body[0].Length != 4 ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !ArrayFieldLoad(body[1], out var fieldOffset) ||
            !Registers(body[2], Code.Test_rm64_r64, NativeRegister.R9, NativeRegister.R9) ||
            !Branch(body[3], Code.Je_rel8_64, body[10].IP) ||
            !BoundsCompare(body[4], pe) ||
            !Branch(body[5], Code.Jae_rel8_64, body[12].IP) ||
            !Registers(body[6], Code.Movsxd_r64_rm32, NativeRegister.RAX, NativeRegister.EDX) ||
            !ElementStore(body[7], pe) ||
            !X64Stack28BodyProof.Stack(body[8], Mnemonic.Add) ||
            body[9].Code != Code.Retnq || body[9].OpCount != 0 ||
            !Call(body[10]) || body[11].Code != Code.Int3 ||
            !Call(body[12]))
            return null;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }
        return new Shape(fieldOffset);
    }

    private static bool ArrayFieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != NativeRegister.R9 ||
            instruction.MemoryDisplacement64 is < 16 or > 0xFF8 ||
            !Memory(instruction, 1, NativeRegister.RCX, NativeRegister.None,
                1, instruction.MemoryDisplacement64, 8))
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool BoundsCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe) &&
        Memory(instruction, 1, NativeRegister.R9, NativeRegister.None,
            1, instruction.MemoryDisplacement64, 4);

    private static bool ElementStore(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Mov_rm8_r8 &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.R8L &&
        instruction.MemoryDisplacement64 == Il2CppArrayUtils.GetFirstItemOffset(pe) &&
        (Memory(instruction, 0, NativeRegister.RAX, NativeRegister.R9,
             1, instruction.MemoryDisplacement64, 1) ||
         Memory(instruction, 0, NativeRegister.R9, NativeRegister.RAX,
             1, instruction.MemoryDisplacement64, 1));

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale, ulong offset, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == width;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
