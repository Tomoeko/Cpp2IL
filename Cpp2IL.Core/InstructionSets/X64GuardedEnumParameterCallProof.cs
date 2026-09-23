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
/// Proves one exact x64 null-guarded tail call that forwards an unchanged Int32-backed
/// enum parameter from RDX to the same managed parameter type on a field receiver.
/// </summary>
internal static class X64GuardedEnumParameterCallProof
{
    internal sealed record Evidence(MethodAnalysisContext Target, FieldAnalysisContext ReceiverField);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryVoidInstanceMethod(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var sourceParameterDefinition] ||
            method.Parameters is not [var sourceParameter] ||
            !UnchangedEnumParameter(method, sourceParameter, sourceParameterDefinition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 20 or > 48)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 8 or > 24 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[7].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(8).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[7].NextIP, region.End) ||
            !Stack(native[0], Mnemonic.Sub) || native[0].Length != 4 ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }) ||
            !FieldLoad(native[1], out var receiverOffset) ||
            !Test(native[2], NativeRegister.RCX) ||
            native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
            native[3].NearBranchTarget != native[7].IP ||
            !Zero(native[4], NativeRegister.R8D) ||
            !Stack(native[5], Mnemonic.Add) ||
            native[6].Mnemonic != Mnemonic.Jmp || native[6].Op0Kind != OpKind.NearBranch64 ||
            native[7].Code != Code.Call_rel32_64 || native[7].Op0Kind != OpKind.NearBranch64 ||
            X86RuntimeNullThrowProof.TryIdentify(app, native[7].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native.Take(8).ToArray(),
                new HashSet<ulong> { native[7].IP }) != null)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)receiverOffset &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (fields is not [{ } receiverField] ||
            receiverField.Name != receiverField.DefaultName ||
            receiverField.FieldType is not { Definition: { GenericContainer: null } } receiverType ||
            !NullCheckedCall.IsReferenceClass(receiverType))
            return null;
        var ownerLocal = new LocalVariable("proved-owner", new ManagedRegister(null, "proved-owner"), owner);
        if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(receiverField, ownerLocal, (int)receiverOffset)))
            return null;

        if (!app.MethodsByAddress.TryGetValue(native[6].NearBranchTarget, out var bindings) ||
            bindings is not [var target] ||
            !OrdinaryVoidInstanceMethod(target) ||
            !ReferenceEquals(target.DeclaringType, receiverType) ||
            !AccessibleTarget(owner, target) ||
            target.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } targetDefinition ||
            targetDefinition.InternalParameterData is not [var targetParameterDefinition] ||
            target.Parameters is not [var targetParameter] ||
            !ReferenceEquals(targetParameter.ParameterType, sourceParameter.ParameterType) ||
            !UnchangedEnumParameter(target, targetParameter, targetParameterDefinition) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target))
            return null;

        return new Evidence(target, receiverField);
    }

    private static bool OrdinaryVoidInstanceMethod(MethodAnalysisContext method) =>
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

    private static bool AccessibleTarget(TypeAnalysisContext caller, MethodAnalysisContext target)
    {
        var type = target.DeclaringType;
        if (type == null)
            return false;
        if (ReferenceEquals(type, caller))
            return true;
        var sameAssembly = ReferenceEquals(caller.DeclaringAssembly, type.DeclaringAssembly);
        var methodAccess = target.Attributes & MethodAttributes.MemberAccessMask;
        if (methodAccess != MethodAttributes.Public &&
            !(sameAssembly && (methodAccess is MethodAttributes.Assembly or MethodAttributes.FamORAssem)))
            return false;
        for (var current = type; current != null; current = current.DeclaringType)
        {
            var visibility = current.Attributes & TypeAttributes.VisibilityMask;
            if (current.DeclaringType == null)
            {
                if (visibility != TypeAttributes.Public &&
                    !(sameAssembly && visibility == TypeAttributes.NotPublic))
                    return false;
            }
            else if (visibility != TypeAttributes.NestedPublic &&
                     !(sameAssembly && (visibility is TypeAttributes.NestedAssembly or
                         TypeAttributes.NestedFamORAssem)))
                return false;
        }
        return true;
    }

    private static bool UnchangedEnumParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition definition)
    {
        var enumType = parameter.ParameterType;
        if (parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
            !ReferenceEquals(parameter.Definition, definition) || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes || parameter.OverrideParameterType != null ||
            definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            enumType.Definition is not { IsEnumType: true, GenericContainer: null } ||
            enumType.IsGenericInstance || enumType.GenericParameters.Count != 0 ||
            enumType.Attributes != enumType.DefaultAttributes ||
            !ReferenceEquals(enumType.BaseType, enumType.DefaultBaseType) ||
            !ReferenceEquals(enumType.EnumUnderlyingType, method.AppContext.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(enumType.DefaultEnumUnderlyingType, enumType.EnumUnderlyingType))
            return false;

        var backing = enumType.Fields.Where(field => !field.IsStatic).ToArray();
        return backing is [{ } value] && value.Name == "value__" &&
               value.Name == value.DefaultName && value.Attributes == value.DefaultAttributes &&
               value.Offset == value.DefaultOffset && value.OverrideFieldType == null &&
               ReferenceEquals(value.FieldType, method.AppContext.SystemTypes.SystemInt32Type) &&
               value.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                   NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool FieldLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RCX && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8 &&
               // A null owner must fault on its reference-field load. Keep the whole read
               // in the low invalid-address range used by the exact Windows target.
               offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;
}
