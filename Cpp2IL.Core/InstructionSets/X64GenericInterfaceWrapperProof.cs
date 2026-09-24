using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete explicit-interface wrapper whose guarded MethodRef slot
/// supplies the exact generic MethodInfo for an inherited tail call. Shared
/// native target addresses do not identify the source method on their own.
/// </summary>
internal static class X64GenericInterfaceWrapperProof
{
    internal sealed record Evidence(ConcreteGenericMethodAnalysisContext Target, int Argument);

    internal readonly record struct Shape(ulong Flag, ulong MethodSlot,
        ulong Initializer, ulong Target, int Argument);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                !HasSignature(method, app))
                return null;

            var start = method.UnderlyingPointer;
            var span = unwind.ClassifySpan(start, start + 1);
            if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                span.Start != start || span.RootStart != start ||
                span.End - start != 62 ||
                !unwind.MatchesUnwind(start, span.End, 6, 0,
                    new byte[] { 6, 0x32, 2, 0x30 }))
                return null;

            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var rawStart = pe.MapVirtualAddressToRaw(start, false);
            var bytes = pe.GetRawBinaryContent();
            if (method.RawBytes.Length != 62 || native.Length != 14 ||
                rawStart < 0 || rawStart > bytes.Length - 62 ||
                !bytes.Slice(checked((int)rawStart), 62)
                    .SequenceEqual(method.RawBytes.AsSpan()) ||
                native[0].IP != start || native[^1].NextIP != span.End ||
                native.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
                native.Where((instruction, index) => index > 0 &&
                    instruction.IP != native[index - 1].NextIP).Any() ||
                Enumerable.Range(0, 62).Any(offset =>
                    !unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                        unwind.ImageBase))) ||
                    pe.MapVirtualAddressToRaw(start + (ulong)offset, false) != rawStart + offset) ||
                Enumerable.Range(1, 61).Any(offset =>
                    app.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
                !X64NativePaddingProof.HasInt3Padding(pe, span.End, start + 64) ||
                !TryProveShape(native, out var shape) ||
                X86CallerExceptionRegionProof.Check(method, native, new HashSet<ulong>()) != null)
                return null;

            if (shape.Flag == shape.MethodSlot ||
                shape.Flag > shape.MethodSlot && shape.Flag - shape.MethodSlot < 8 ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.Flag) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind,
                    shape.MethodSlot, 8) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    shape.MethodSlot, 8) ||
                shape.Initializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata ||
                !X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app,
                    pe, unwind, shape.Initializer) ||
                app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot) is not
                    { Type: MetadataUsageType.MethodRef, IsValid: true } usage)
                return null;

            var reference = usage.AsGenericMethodRef();
            if (reference.TypeGenericParams.Length != 0 ||
                reference.MethodGenericParams.Length != 1 ||
                !ReferenceEquals(app.ResolveIl2CppType(reference.MethodGenericParams[0]),
                    method.ReturnType) ||
                app.ResolveContextForMethod(reference.BaseMethod) is not { } baseMethod ||
                !ReferenceEquals(reference.DeclaringType,
                    baseMethod.DeclaringType?.Definition) ||
                !IsAccessibleGenericTarget(baseMethod, method.DeclaringType!, app) ||
                baseMethod.UnderlyingPointer != shape.Target ||
                !app.MethodsByAddress.TryGetValue(shape.Target, out var aliases) ||
                aliases.Count(candidate => ReferenceEquals(candidate, baseMethod)) != 1 ||
                app.ResolveContextForMethod(usage) is not
                    ConcreteGenericMethodAnalysisContext inflated ||
                !ReferenceEquals(inflated.MethodRef, reference) ||
                !ReferenceEquals(inflated.BaseMethodContext, baseMethod) ||
                inflated.TypeGenericParameters.Count != 0 ||
                inflated.MethodGenericParameters.Count != 1 ||
                !ReferenceEquals(inflated.MethodGenericParameters[0], method.ReturnType) ||
                !ReferenceEquals(inflated.DeclaringType, baseMethod.DeclaringType) ||
                inflated.Parameters.Count != 1 ||
                !ReferenceEquals(inflated.Parameters[0].ParameterType,
                    app.SystemTypes.SystemInt32Type) ||
                !ReferenceEquals(inflated.ReturnType, method.ReturnType) ||
                inflated.UnderlyingPointer != 0 &&
                inflated.UnderlyingPointer != shape.Target)
                return null;

            return new Evidence(inflated, shape.Argument);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or IndexOutOfRangeException or
                                          OverflowException or NullReferenceException)
        {
            return null;
        }
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> body,
        out Shape shape)
    {
        shape = default;
        if (body.Count != 14 || !Push(body[0], NativeRegister.RBX) ||
            !Stack(body[1], Mnemonic.Sub, 0x20) ||
            !RipByteImmediate(body[2], Mnemonic.Cmp, 0, out var flag) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            body[4].Code != Code.Jne_rel8_64 || body[4].NearBranchTarget != body[8].IP ||
            !RipAddress(body[5], NativeRegister.RCX, out var slot) ||
            body[6].Code != Code.Call_rel32_64 || body[6].Op0Kind != OpKind.NearBranch64 ||
            !RipByteImmediate(body[7], Mnemonic.Mov, 1, out var writtenFlag) ||
            flag != writtenFlag ||
            !RipLoad(body[8], NativeRegister.R8, slot) ||
            body[9].Code != Code.Mov_r32_imm32 ||
            body[9].Op0Kind != OpKind.Register || body[9].Op0Register != NativeRegister.EDX ||
            body[9].Op1Kind != OpKind.Immediate32 ||
            !Move(body[10], NativeRegister.RCX, NativeRegister.RBX) ||
            !Stack(body[11], Mnemonic.Add, 0x20) ||
            !Pop(body[12], NativeRegister.RBX) ||
            body[13].Code != Code.Jmp_rel32_64 || body[13].Op0Kind != OpKind.NearBranch64)
            return false;

        shape = new Shape(flag, slot, body[6].NearBranchTarget,
            body[13].NearBranchTarget, unchecked((int)body[9].Immediate32));
        return true;
    }

    private static bool HasSignature(MethodAnalysisContext method,
        ApplicationAnalysisContext app)
    {
        if (method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            owner.Attributes != owner.DefaultAttributes ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0 } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.Name != method.DefaultName || method.IsStatic || !method.IsVirtual ||
            !method.IsFinal || !method.IsNewSlot || method.Visibility != MethodAttributes.Private ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null || method.IsVoid ||
            !NullCheckedCall.IsReferenceClass(method.ReturnType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer == 0 ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var entries) ||
            entries.Count != 1 || !ReferenceEquals(entries[0], method) ||
            method.Overrides.Count != 1 ||
            method.Overrides[0].DeclaringType is not GenericInstanceTypeAnalysisContext interfaceType ||
            !interfaceType.GenericType.IsInterface ||
            interfaceType.GenericArguments.Count != 1 ||
            !ReferenceEquals(interfaceType.GenericArguments[0], method.ReturnType) ||
            method.Overrides[0].Parameters.Count != 0 ||
            method.Overrides[0].GenericParameters.Count != 0 ||
            !ReferenceEquals(method.Overrides[0].ReturnType, method.ReturnType) ||
            !owner.InterfaceContexts.Any(candidate =>
                candidate is GenericInstanceTypeAnalysisContext instance &&
                ReferenceEquals(instance.GenericType, interfaceType.GenericType) &&
                instance.GenericArguments.Count == 1 &&
                ReferenceEquals(instance.GenericArguments[0], method.ReturnType)))
            return false;
        return true;
    }

    private static bool IsAccessibleGenericTarget(MethodAnalysisContext target,
        TypeAnalysisContext owner, ApplicationAnalysisContext app)
    {
        if (target.Definition is not { GenericContainer: not null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, target.DeclaringType?.Definition) ||
            target.Name != target.DefaultName || target.IsStatic || target.IsVirtual ||
            target.Visibility != MethodAttributes.Family ||
            target.Attributes != target.DefaultAttributes ||
            target.ImplAttributes != target.DefaultImplAttributes ||
            target.OverrideReturnType != null ||
            RuntimeNullGuardCoalescer.HasOutputOptions(target) ||
            target.Parameters.Count != 1 || target.GenericParameters.Count != 1 ||
            definition.InternalParameterData is not [{ RawType:
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } }] ||
            target.Parameters[0].OverrideParameterType != null ||
            target.ReturnType is not GenericParameterTypeAnalysisContext
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_MVAR, Index: 0 } parameter ||
            !ReferenceEquals(parameter.Owner, target) ||
            !ReferenceEquals(target.Parameters[0].ParameterType,
                app.SystemTypes.SystemInt32Type) ||
            target.DeclaringType is not { Definition: { GenericContainer: null,
                    HasCctor: false } } targetOwner ||
            targetOwner.IsValueType || targetOwner.IsInterface ||
            targetOwner.Methods.Any(candidate => candidate.Name == ".cctor"))
            return false;

        for (var ancestor = owner.BaseType; ancestor != null; ancestor = ancestor.BaseType)
            if (ReferenceEquals(ancestor, target.DeclaringType))
                return true;
        return false;
    }

    private static bool Push(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Push && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Pop(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Pop && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong amount) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool Move(NativeInstruction instruction, NativeRegister target,
        NativeRegister source) => instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == target &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool RipByteImmediate(NativeInstruction instruction, Mnemonic mnemonic,
        ulong value, out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
               instruction.Op0Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 1 &&
               instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == value;
    }

    private static bool RipAddress(NativeInstruction instruction, NativeRegister target,
        out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Lea_r64_m &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == target &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None;
    }

    private static bool RipLoad(NativeInstruction instruction, NativeRegister target,
        ulong address) => instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == target &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;
}
