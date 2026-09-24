using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
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
/// Binds a complete metadata-guarded constructor tail call to its immediate
/// constructed generic base. The MethodRef slot disambiguates a folded native
/// target shared by unrelated empty constructors.
/// </summary>
internal static class X64GenericBaseConstructorProof
{
    internal sealed record Evidence(ConcreteGenericMethodAnalysisContext BaseConstructor);

    internal readonly record struct Shape(ulong Flag, ulong MethodSlot,
        ulong Initializer, ulong Target);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryConstructor(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.BaseType is not GenericInstanceTypeAnalysisContext genericBase ||
            genericBase.GenericType.Definition is not { GenericContainer: not null,
                HasCctor: false, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } ||
            genericBase.GenericType.IsValueType || genericBase.GenericType.IsInterface ||
            genericBase.GenericType.Methods.Any(candidate => candidate.Name == ".cctor") ||
            genericBase.GenericType.Name != genericBase.GenericType.DefaultName ||
            genericBase.GenericType.Namespace != genericBase.GenericType.DefaultNamespace ||
            genericBase.GenericType.Attributes != genericBase.GenericType.DefaultAttributes ||
            genericBase.GenericArguments.Count == 0 ||
            genericBase.GenericArguments.Count != genericBase.GenericType.GenericParameters.Count ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var span = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != method.UnderlyingPointer || span.RootStart != span.Start ||
            span.End - span.Start != 57 ||
            !unwind.MatchesUnwind(span.Start, span.End, 6, 0,
                new byte[] { 6, 0x32, 2, 0x30 }))
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < span.End).ToArray();
        var first = pe.MapVirtualAddressToRaw(span.Start, false);
        var last = pe.MapVirtualAddressToRaw(span.End - 1, false);
        if (method.RawBytes.Length != 57 || native.Length != 13 ||
            first < 0 || last - first != 56 || last >= pe.GetRawBinaryContent().Length ||
            !pe.GetRawBinaryContent().Slice(checked((int)first), 57)
                .SequenceEqual(method.RawBytes.AsSpan()) ||
            native[0].IP != span.Start || native[^1].NextIP != span.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            Enumerable.Range(0, 57).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(span.Start + (ulong)offset - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(span.Start + (ulong)offset, false) != first + offset) ||
            Enumerable.Range(1, 56).Any(offset =>
                app.MethodsByAddress.ContainsKey(span.Start + (ulong)offset)) ||
            !X64NativePaddingProof.HasInt3Padding(pe, span.End, span.Start + 64) ||
            !TryProveShape(native, out var shape) ||
            X86CallerExceptionRegionProof.Check(method, native, new HashSet<ulong>()) != null)
            return null;

        if (shape.Flag == shape.MethodSlot ||
            shape.Flag > shape.MethodSlot && shape.Flag - shape.MethodSlot < 8 ||
            !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.Flag) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.MethodSlot, 8) ||
            shape.Initializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app, pe, unwind,
                shape.Initializer) ||
            app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot) is not
                { Type: MetadataUsageType.MethodRef, IsValid: true } usage)
            return null;

        try
        {
            var reference = usage.AsGenericMethodRef();
            if (reference.MethodGenericParams.Length != 0 ||
                reference.TypeGenericParams.Length != genericBase.GenericArguments.Count ||
                !ReferenceEquals(reference.DeclaringType, genericBase.GenericType.Definition) ||
                reference.TypeGenericParams.Where((argument, index) =>
                    !ReferenceEquals(app.ResolveIl2CppType(argument),
                        genericBase.GenericArguments[index])).Any() ||
                app.ResolveContextForMethod(reference.BaseMethod) is not { } baseDefinition ||
                !OrdinaryConstructor(baseDefinition) ||
                !ReferenceEquals(baseDefinition.DeclaringType, genericBase.GenericType) ||
                baseDefinition.UnderlyingPointer != shape.Target ||
                baseDefinition.Definition is not { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } baseRaw ||
                !ReferenceEquals(baseRaw.DeclaringType, genericBase.GenericType.Definition) ||
                (baseRaw.InternalParameterData?.Length ?? 0) != 0 ||
                genericBase.GenericType.Methods.Count(candidate =>
                    candidate.Name == ".ctor" && candidate.Parameters.Count == 0) != 1 ||
                !app.MethodsByAddress.TryGetValue(shape.Target, out var targets) ||
                targets.Count(candidate => ReferenceEquals(candidate, baseDefinition)) != 1 ||
                app.ResolveContextForMethod(usage) is not ConcreteGenericMethodAnalysisContext
                { BaseMethodContext: var resolvedBase } inflated ||
                !ReferenceEquals(resolvedBase, baseDefinition) ||
                inflated.DeclaringType is not GenericInstanceTypeAnalysisContext inflatedType ||
                !ReferenceEquals(inflatedType.GenericType, genericBase.GenericType) ||
                !inflatedType.GenericArguments.SequenceEqual(genericBase.GenericArguments) ||
                inflated.Parameters.Count != 0 || inflated.MethodGenericParameters.Count != 0 ||
                !ReferenceEquals(inflated.ReturnType, app.SystemTypes.SystemVoidType))
                return null;
            return new Evidence(inflated);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or NullReferenceException)
        {
            return null;
        }
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> body, out Shape shape)
    {
        shape = default;
        if (body.Count != 13 || !Push(body[0], NativeRegister.RBX) ||
            !Stack(body[1], Mnemonic.Sub, 0x20) ||
            !RipByteImmediate(body[2], Mnemonic.Cmp, 0, out var flag) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            body[4].Code != Code.Jne_rel8_64 || body[4].NearBranchTarget != body[8].IP ||
            !RipAddress(body[5], NativeRegister.RCX, out var slot) ||
            body[6].Code != Code.Call_rel32_64 || body[6].Op0Kind != OpKind.NearBranch64 ||
            !RipByteImmediate(body[7], Mnemonic.Mov, 1, out var writtenFlag) ||
            flag != writtenFlag ||
            !RipLoad(body[8], NativeRegister.RDX, slot) ||
            !Move(body[9], NativeRegister.RCX, NativeRegister.RBX) ||
            !Stack(body[10], Mnemonic.Add, 0x20) ||
            !Pop(body[11], NativeRegister.RBX) ||
            body[12].Code != Code.Jmp_rel32_64 || body[12].Op0Kind != OpKind.NearBranch64)
            return false;

        shape = new Shape(flag, slot, body[6].NearBranchTarget, body[12].NearBranchTarget);
        return true;
    }

    private static bool OrdinaryConstructor(MethodAnalysisContext method) =>
        method.Name == ".ctor" && method.Name == method.DefaultName &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) ==
        (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

    private static bool Push(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Push && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Pop(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Pop && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong amount) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool Move(NativeInstruction instruction, NativeRegister target,
        NativeRegister source) => instruction.Mnemonic == Mnemonic.Mov &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == target && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool RipByteImmediate(NativeInstruction instruction, Mnemonic mnemonic,
        ulong value, out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
               instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 1 &&
               instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == value;
    }

    private static bool RipAddress(NativeInstruction instruction, NativeRegister target,
        out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Lea_r64_m && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == target && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None;
    }

    private static bool RipLoad(NativeInstruction instruction, NativeRegister target,
        ulong address) => instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == target &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;
}
