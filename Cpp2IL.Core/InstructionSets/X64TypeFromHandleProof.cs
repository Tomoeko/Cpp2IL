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
/// Proves the complete exact-target static typeof(T) body. Its two metadata
/// slots, System.Type class-initialization guard, and managed tail target must
/// independently agree before the source type token can be emitted.
/// </summary>
internal static class X64TypeFromHandleProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Shape(ulong OnceFlag, ulong TypeSlot, ulong TypeInfoSlot,
        ulong MetadataInitializer, ulong ClassInitializer, ulong GetTypeFromHandle);

    internal sealed record Evidence(TypeAnalysisContext TargetType,
        MethodAnalysisContext GetTypeFromHandle);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryStaticTypeMethod(method, app) ||
                decoded.Count < 19 || decoded[0].IP != method.UnderlyingPointer ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var callerBindings) ||
                callerBindings is not [var bound] || !ReferenceEquals(bound, method))
                return null;

            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != method.UnderlyingPointer ||
                region.RootStart != region.Start ||
                region.End - region.Start != 89 ||
                unwind.ClassifySpan(region.Start, region.End).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    SavedRbxFrame) || !FileBacked(pe, region.Start, region.End))
                return null;

            var body = decoded.TakeWhile(instruction =>
                instruction.IP < region.End).ToArray();
            var shape = TryProveShape(body);
            if (shape == null || body[^1].NextIP != region.End ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null ||
                Enumerable.Range(1, checked((int)(region.End - region.Start) - 1))
                    .Any(offset => app.MethodsByAddress.ContainsKey(
                        region.Start + (ulong)offset)))
                return null;

            method.EnsureRawBytes();
            var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
            var bodyLength = checked((int)(region.End - region.Start));
            if (rawStart < 0 || method.RawBytes.Length < bodyLength ||
                rawStart > pe.GetRawBinaryContent().Length - bodyLength ||
                !pe.GetRawBinaryContent().Slice((int)rawStart, bodyLength)
                    .SequenceEqual(method.RawBytes.AsSpan().Slice(0, bodyLength)) ||
                !X86Utils.Iterate(method).Take(body.Length).SequenceEqual(body))
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Find authenticates the complete PE-backed native body before this binding
    // can authorize emission. Kept separate so metadata negatives reach their
    // intended predicate without replacing native bytes in a fixture binary.
    internal static Evidence? BindProvedShape(MethodAnalysisContext method,
        Shape shape)
    {
        try
        {
            var app = method.AppContext;
            if (app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                shape.TypeSlot == shape.TypeInfoSlot ||
                Overlaps(shape.OnceFlag, 1, shape.TypeSlot, 8) ||
                Overlaps(shape.OnceFlag, 1, shape.TypeInfoSlot, 8) ||
                Overlaps(shape.TypeSlot, 8, shape.TypeInfoSlot, 8) ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind,
                    shape.OnceFlag) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                    unwind, shape.TypeSlot, 8) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                    unwind, shape.TypeInfoSlot, 8) ||
                shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata ||
                !X64MetadataInitializationHelperProof.TryIdentify(app, pe,
                    unwind, shape.MetadataInitializer) ||
                shape.ClassInitializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_runtime_class_init_export ||
                shape.ClassInitializer == 0)
                return null;

            var typeUsage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                shape.TypeSlot);
            var classUsage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                shape.TypeInfoSlot);
            if (typeUsage is not { Type: MetadataUsageType.Type, IsValid: true } ||
                classUsage is not { Type: MetadataUsageType.TypeInfo,
                    IsValid: true } ||
                !ReferenceEquals(app.ResolveIl2CppType(classUsage.AsType()),
                    app.SystemTypes.SystemTypeType) ||
                !SupportedSystemType(app.SystemTypes.SystemTypeType) ||
                !app.MethodsByAddress.TryGetValue(shape.GetTypeFromHandle,
                    out var targets) || targets is not [var tail] ||
                !SupportedTail(tail, app, shape.GetTypeFromHandle))
                return null;

            var targetType = app.ResolveIl2CppType(typeUsage.AsType());
            if (!SupportedTarget(targetType, method))
                return null;
            return new Evidence(targetType, tail);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 19 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        if (!PushRbx(body[0]) || !Stack(body[1], Mnemonic.Sub) ||
            !RipCompareZero(body[2]) ||
            !Branch(body[3], Code.Jne_rel8_64, body[9].IP) ||
            !RipLea(body[4], NativeRegister.RCX) ||
            !DirectCall(body[5]) ||
            !RipLea(body[6], NativeRegister.RCX) ||
            !DirectCall(body[7]) ||
            body[5].NearBranchTarget != body[7].NearBranchTarget ||
            !RipStoreOne(body[8], body[2].IPRelativeMemoryAddress) ||
            !RipLoad(body[9], NativeRegister.RCX,
                body[6].IPRelativeMemoryAddress) ||
            !RipLoad(body[10], NativeRegister.RBX,
                body[4].IPRelativeMemoryAddress) ||
            !ClassInitCheck(body[11]) ||
            !Branch(body[12], Code.Jne_rel8_64, body[14].IP) ||
            !DirectCall(body[13]) ||
            !ZeroEdx(body[14]) ||
            !Move(body[15], NativeRegister.RCX, NativeRegister.RBX) ||
            !Stack(body[16], Mnemonic.Add) ||
            !PopRbx(body[17]) || !DirectJump(body[18]))
            return null;

        return new Shape(body[2].IPRelativeMemoryAddress,
            body[4].IPRelativeMemoryAddress,
            body[6].IPRelativeMemoryAddress,
            body[5].NearBranchTarget,
            body[13].NearBranchTarget,
            body[18].NearBranchTarget);
    }

    private static bool OrdinaryStaticTypeMethod(MethodAnalysisContext method,
        ApplicationAnalysisContext app) =>
        method.DeclaringType is { } owner &&
        X64MetadataStaticGetterProof.OrdinaryOwner(owner) &&
        owner.Definition?.GenericContainer == null &&
        ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.Parameters.Count == 0 &&
        method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        ReferenceEquals(method.ReturnType, app.SystemTypes.SystemTypeType) &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract |
            MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
            MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
        method.UnderlyingPointer != 0;

    private static bool SupportedSystemType(TypeAnalysisContext systemType) =>
        systemType.Definition is { HasCctor: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        systemType.Methods.Count(candidate => candidate.Name == ".cctor") == 1 &&
        systemType.Attributes == systemType.DefaultAttributes &&
        (systemType.Attributes & TypeAttributes.BeforeFieldInit) != 0;

    private static bool SupportedTarget(TypeAnalysisContext target,
        MethodAnalysisContext method) =>
        target.Definition is { GenericContainer: null,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        X64MetadataStaticGetterProof.OrdinaryOwner(target) &&
        target.Visibility == TypeAttributes.Public &&
        target.DeclaringType == null &&
        ReferenceEquals(target.DeclaringAssembly,
            method.DeclaringType?.DeclaringAssembly);

    private static bool SupportedTail(MethodAnalysisContext tail,
        ApplicationAnalysisContext app, ulong address)
    {
        if (tail.UnderlyingPointer != address ||
            !ReferenceEquals(tail.DeclaringType,
                app.SystemTypes.SystemTypeType) ||
            tail.Name != "GetTypeFromHandle" ||
            tail.Name != tail.DefaultName ||
            tail.Visibility != MethodAttributes.Public ||
            !tail.IsStatic || tail.IsVirtual || tail.IsVoid ||
            tail.Definition is not { GenericContainer: null,
                parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [var rawParameter] } definition ||
            !ReferenceEquals(definition.DeclaringType,
                app.SystemTypes.SystemTypeType.Definition) ||
            rawParameter.RawType is not { Type:
                Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            tail.Parameters is not [var parameter] ||
            parameter.IsRef || parameter.OverrideParameterType != null ||
            !ReferenceEquals(parameter.DefaultParameterType,
                parameter.ParameterType) ||
            parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            parameter.ParameterType is not { FullName:
                "System.RuntimeTypeHandle" } handle ||
            !ReferenceEquals(handle.DeclaringAssembly,
                app.SystemTypes.SystemTypeType.DeclaringAssembly) ||
            tail.GenericParameters.Count != 0 ||
            tail.OverrideReturnType != null ||
            !ReferenceEquals(tail.ReturnType,
                app.SystemTypes.SystemTypeType) ||
            tail.Attributes != tail.DefaultAttributes ||
            tail.ImplAttributes != tail.DefaultImplAttributes ||
            (tail.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl)) != 0 ||
            (tail.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(tail) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(tail))
            return false;

        var instanceFields = handle.Fields.Where(field => !field.IsStatic)
            .ToArray();
        return handle.IsValueType && instanceFields is [var pointer] &&
               pointer.Offset == 0 &&
               ReferenceEquals(pointer.FieldType,
                   app.SystemTypes.SystemIntPtrType) &&
               pointer.BackingData?.Field.RawFieldType is
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_I,
                       NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool Overlaps(ulong first, ulong firstLength,
        ulong second, ulong secondLength) =>
        first > ulong.MaxValue - firstLength ||
        second > ulong.MaxValue - secondLength ||
        first < second + secondLength && second < first + firstLength;

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first &&
               (ulong)(last - first) == end - start - 1 &&
               last < pe.GetRawBinaryContent().Length;
    }

    private static bool PushRbx(NativeInstruction instruction) =>
        instruction.Code == Code.Push_r64 &&
        Register(instruction, NativeRegister.RBX);

    private static bool PopRbx(NativeInstruction instruction) =>
        instruction.Code == Code.Pop_r64 &&
        Register(instruction, NativeRegister.RBX);

    private static bool Register(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction,
        Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic &&
        Register(instruction, NativeRegister.RSP) &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool RipCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 0;

    private static bool RipLea(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool DirectJump(NativeInstruction instruction) =>
        instruction.Code == Code.Jmp_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool RipStoreOne(NativeInstruction instruction,
        ulong flag) =>
        instruction.Code == Code.Mov_rm8_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == flag &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 1;

    private static bool RipLoad(NativeInstruction instruction,
        NativeRegister destination, ulong slot) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == slot;

    private static bool ClassInitCheck(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm32_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RCX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 ==
            Il2CppClassLayout.CctorFinishedOrNoCctorOffset64 &&
        instruction.MemorySize.GetSize() == 4 &&
        instruction.Op1Kind == OpKind.Immediate8to32 &&
        instruction.GetImmediate(1) == 0;

    private static bool ZeroEdx(NativeInstruction instruction) =>
        instruction.Code == Code.Xor_r32_rm32 &&
        Register(instruction, NativeRegister.EDX) &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.EDX;

    private static bool Move(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;
}
