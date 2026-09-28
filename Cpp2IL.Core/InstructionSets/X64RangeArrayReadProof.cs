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
/// Proves a complete reference-array field read indexed by the exact target's
/// integer Random.Range call. The source field is captured once, and both
/// nonreturning failure arms are authenticated before they become an array read.
/// </summary>
internal static class X64RangeArrayReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField,
        MethodAnalysisContext Range);

    internal sealed record NativeShape(int FieldOffset, ulong RangeTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not
                    { Definition: { GenericContainer: null,
                        RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                            NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
                owner.GenericParameters.Count != 0 ||
                owner.Name != owner.DefaultName ||
                owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                owner.Definition is not { PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true } ||
                !OrdinaryMethod(method, owner) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var callerBindings) ||
                callerBindings is not [var caller] ||
                !ReferenceEquals(caller, method))
                return null;

            var decoded = X86Utils.Iterate(method).ToArray();
            if (!X64ArrayGuardSiteProof.TryCompleteFileBackedRegion(method,
                    decoded, pe, unwind, out var body) ||
                TryProveShape(body, pe) is not { } shape ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 6, 0,
                    [6, 0x32, 2, 0x30]) ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[16].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[18].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[16].IP, body[18].IP }) != null ||
                BindRange(app, shape.RangeTarget) is not { } range ||
                ReferenceEquals(owner.DeclaringAssembly,
                    range.DeclaringType?.DeclaringAssembly))
                return null;

            var fields = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (fields is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, method.ReturnType))
                return null;

            var receiver = new LocalVariable("proved-owner",
                new ManagedRegister(null, "rcx"), owner);
            return NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(arrayField, receiver, shape.FieldOffset))
                ? new Evidence(arrayField, range) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method,
        TypeAnalysisContext owner)
    {
        if (method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.RawReturnType.Type is not
                (Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType))
            return false;

        if (definition.RawReturnType.Type == Il2CppTypeEnum.IL2CPP_TYPE_STRING)
        {
            if (!ReferenceEquals(method.ReturnType,
                    method.AppContext.SystemTypes.SystemStringType))
                return false;
        }
        else if (method.ReturnType is not
            { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } result ||
            result.IsValueType || result.IsInterface || result.IsGenericInstance ||
            result.GenericParameters.Count != 0 ||
            result.Name != result.DefaultName ||
            result.Namespace != result.DefaultNamespace ||
            result.Attributes != result.DefaultAttributes)
            return false;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Getter, method)).ToArray();
        if ((method.Attributes & MethodAttributes.SpecialName) == 0)
            return properties.Length == 0;
        return properties is [{ } property] && property.Setter == null &&
               property.Definition is { } rawProperty &&
               ReferenceEquals(rawProperty.Getter, definition) &&
               property.Name == property.DefaultName &&
               method.Name == "get_" + property.Name &&
               property.Attributes == property.DefaultAttributes &&
               property.OverridePropertyType == null && !property.IsStatic &&
               ReferenceEquals(property.PropertyType, method.ReturnType) &&
               rawProperty.RawPropertyType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
               raw.Type == definition.RawReturnType.Type;
    }

    private static MethodAnalysisContext? BindRange(ApplicationAnalysisContext app,
        ulong address)
    {
        // The optimizer folds the public wrapper and its private engine alias
        // to one pointer. A caller in another assembly cannot name the private
        // method; require exactly one public, signature-identical source target.
        if (address == 0 ||
            !app.MethodsByAddress.TryGetValue(address, out var aliases) ||
            aliases.Count != 2)
            return null;
        var publicMatches = aliases.Where(alias =>
            alias.Name == "Range" &&
            (alias.Attributes & MethodAttributes.MemberAccessMask) ==
                MethodAttributes.Public && ValidRangeAlias(alias, address)).ToArray();
        var privateMatches = aliases.Where(alias =>
            alias.Name == "RandomRangeInt" &&
            (alias.Attributes & MethodAttributes.MemberAccessMask) ==
                MethodAttributes.Private && ValidRangeAlias(alias, address)).ToArray();
        return publicMatches is [var range] && privateMatches is [_] &&
               !ReferenceEquals(range, privateMatches[0]) ? range : null;
    }

    private static bool ValidRangeAlias(MethodAnalysisContext alias, ulong address)
    {
        if (alias.UnderlyingPointer != address ||
            alias.DeclaringType is not
                { FullName: "UnityEngine.Random",
                    Definition: { GenericContainer: null } } random ||
            random.DeclaringAssembly.Name != "UnityEngine.CoreModule" ||
            random.Attributes != random.DefaultAttributes ||
            (random.Attributes & TypeAttributes.VisibilityMask) !=
                TypeAttributes.Public ||
            alias.IsVirtual || !alias.IsStatic ||
            alias.Name != alias.DefaultName ||
            alias.Attributes != alias.DefaultAttributes ||
            alias.ImplAttributes != alias.DefaultImplAttributes ||
            alias.Definition is not { GenericContainer: null, parameterCount: 2,
                InternalParameterData: [var first, var second],
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, random.Definition) ||
            !ReferenceEquals(alias.ReturnType,
                alias.AppContext.SystemTypes.SystemInt32Type) ||
            alias.Parameters is not [{ } min, { } max] ||
            !ValidInt32Parameter(alias, min, first, 0) ||
            !ValidInt32Parameter(alias, max, second, 1) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(alias) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(alias,
                requireUniqueBinding: false))
            return false;
        return true;
    }

    private static bool ValidInt32Parameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter,
        LibCpp2IL.Metadata.Il2CppParameterDefinition raw, int index) =>
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition, raw) &&
        parameter.ParameterIndex == index && !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null &&
        ReferenceEquals(parameter.ParameterType,
            method.AppContext.SystemTypes.SystemInt32Type) &&
        raw.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
            NumMods: 0, Byref: 0, Pinned: 0 };

    internal static NativeShape? TryProveShape(IReadOnlyList<NativeInstruction> body,
        PE pe)
    {
        if (body.Count != 20 || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Push_r64 ||
            body[0].Op0Register != NativeRegister.RBX ||
            !Stack(body[1], Mnemonic.Sub) ||
            !FieldLoad(body[2], out var fieldOffset) ||
            !Registers(body[3], Code.Test_rm64_r64,
                NativeRegister.RBX, NativeRegister.RBX) ||
            !Branch(body[4], Code.Je_rel8_64, body[16].IP) ||
            !LengthLoad(body[5], pe) ||
            !Registers(body[6], Code.Xor_r32_rm32,
                NativeRegister.R8D, NativeRegister.R8D) ||
            !Registers(body[7], Code.Xor_r32_rm32,
                NativeRegister.ECX, NativeRegister.ECX) ||
            !DirectCall(body[8]) ||
            !ExactIndexAndBoundsOrder(body, pe) ||
            !ElementLoad(body[12], pe) ||
            !Stack(body[13], Mnemonic.Add) ||
            body[14].Code != Code.Pop_r64 ||
            body[14].Op0Register != NativeRegister.RBX ||
            body[15].Code != Code.Retnq || body[15].OpCount != 0 ||
            !DirectCall(body[16]) || body[17].Code != Code.Int3 ||
            !DirectCall(body[18]) || body[19].Code != Code.Int3)
            return null;

        return new NativeShape(fieldOffset, body[8].NearBranchTarget);
    }

    private static bool ExactIndexAndBoundsOrder(
        IReadOnlyList<NativeInstruction> body, PE pe)
    {
        // CDQE only sign-extends the EAX result and leaves flags unchanged.
        // In both complete schedules CMP reads that same EAX, JAE consumes its
        // flags immediately, and the array load uses the extended RAX.
        var extensionBeforeCompare = Cdqe(body[9]) &&
            LengthCompare(body[10], pe) &&
            Branch(body[11], Code.Jae_rel8_64, body[18].IP);
        var extensionAfterBranch = LengthCompare(body[9], pe) &&
            Branch(body[10], Code.Jae_rel8_64, body[18].IP) &&
            Cdqe(body[11]);
        return extensionBeforeCompare || extensionAfterBranch;
    }

    private static bool Cdqe(NativeInstruction instruction) =>
        instruction.Code == Code.Cdqe && instruction.OpCount == 0;

    private static bool FieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != NativeRegister.RBX ||
            !Memory(instruction, 1, NativeRegister.RCX, NativeRegister.None,
                1, instruction.MemoryDisplacement64, 8) ||
            instruction.MemoryDisplacement64 is < 16 or > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool LengthLoad(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Mov_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        ArrayLengthMemory(instruction, 1, pe);

    private static bool LengthCompare(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Cmp_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EAX &&
        ArrayLengthMemory(instruction, 1, pe);

    private static bool ArrayLengthMemory(NativeInstruction instruction,
        int operand, PE pe) =>
        instruction.MemoryDisplacement64 <= uint.MaxValue &&
        Il2CppArrayUtils.IsIl2cppLengthAccessor(
            (uint)instruction.MemoryDisplacement64, pe) &&
        Memory(instruction, operand, NativeRegister.RBX,
            NativeRegister.None, 1, instruction.MemoryDisplacement64, 4);

    private static bool ElementLoad(NativeInstruction instruction, PE pe) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        Memory(instruction, 1, NativeRegister.RBX, NativeRegister.RAX,
            pe.PointerSizeBytes, Il2CppArrayUtils.GetFirstItemOffset(pe),
            pe.PointerSizeBytes);

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, NativeRegister index, int scale,
        ulong displacement, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base && instruction.MemoryIndex == index &&
        instruction.MemoryIndexScale == scale &&
        instruction.MemoryDisplacement64 == displacement &&
        instruction.MemorySize.GetSize() == size;
}
