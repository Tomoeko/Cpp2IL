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
/// Binds an exact metadata/class-init guarded constructor to its immediate
/// managed base. A second folded constructor body and an inert Object thunk
/// establish the complete native tail despite two constructor aliases.
/// </summary>
internal static class X64GuardedBaseConstructorProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(MethodAnalysisContext BaseConstructor);

    internal readonly record struct Shape(ulong OnceFlag, ulong TypeInfoSlot,
        ulong MetadataInitializer, ulong ClassInitializer, ulong Tail);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { } owner ||
                !X64ClassCastLookupProof.PublicOrdinaryClass(owner) ||
                !OrdinaryConstructor(method, owner, app) ||
                owner.BaseType is not { } immediateBase ||
                !ExplicitInitializerClass(immediateBase) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    owner.DeclaringAssembly, immediateBase.DeclaringAssembly) ||
                immediateBase.BaseType is not { } foldedBase ||
                !X64ClassCastLookupProof.PublicOrdinaryClass(foldedBase) ||
                foldedBase.BaseType is not { } guardedAncestor ||
                !ExplicitInitializerClass(guardedAncestor) ||
                !ReferenceEquals(guardedAncestor.BaseType,
                    app.SystemTypes.SystemObjectType) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    immediateBase.DeclaringAssembly, foldedBase.DeclaringAssembly) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    foldedBase.DeclaringAssembly, guardedAncestor.DeclaringAssembly) ||
                !OnlyConstructor(immediateBase, app, out var baseConstructor) ||
                !OnlyConstructor(foldedBase, app, out var foldedConstructor) ||
                !OnlyConstructor(guardedAncestor, app, out var ancestorConstructor) ||
                !TryFindBody(method, pe, unwind, out var caller) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var callerBindings) || callerBindings is not [var callerBound] ||
                !ReferenceEquals(callerBound, method) ||
                !BindGuard(method, pe, unwind, caller, immediateBase) ||
                !ReferenceEquals(baseConstructor.DeclaringType, immediateBase) ||
                baseConstructor.UnderlyingPointer != caller.Tail ||
                foldedConstructor.UnderlyingPointer != caller.Tail ||
                !ExactlyTwoAliases(app, caller.Tail, baseConstructor,
                    foldedConstructor) ||
                !TryFindBody(baseConstructor, pe, unwind, out var folded) ||
                !TryFindBody(foldedConstructor, pe, unwind,
                    out var foldedAlias) ||
                foldedAlias != folded ||
                !BindGuard(baseConstructor, pe, unwind, folded,
                    guardedAncestor) ||
                !BindGuard(foldedConstructor, pe, unwind, foldedAlias,
                    guardedAncestor) ||
                Overlaps(caller.OnceFlag, 1, folded.OnceFlag, 1) ||
                Overlaps(caller.OnceFlag, 1, folded.TypeInfoSlot, 8) ||
                Overlaps(caller.TypeInfoSlot, 8, folded.OnceFlag, 1) ||
                Overlaps(caller.TypeInfoSlot, 8, folded.TypeInfoSlot, 8))
                return null;

            ancestorConstructor.EnsureRawBytes();
            var ancestorBody = X86Utils.Iterate(ancestorConstructor).ToArray();
            var objectConstructor = X64ObjectConstructorThunkProof.Find(
                ancestorConstructor, ancestorBody);
            if (objectConstructor == null ||
                objectConstructor.UnderlyingPointer != folded.Tail ||
                ancestorBody.Length != 2 ||
                ancestorBody[1].NearBranchTarget != folded.Tail)
                return null;

            return new Evidence(baseConstructor);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryFindBody(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, out Shape shape)
    {
        shape = default;
        if (method.UnderlyingPointer is 0 or ulong.MaxValue)
            return false;
        var start = method.UnderlyingPointer;
        var span = unwind.ClassifySpan(start, start + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            span.Start != start || span.RootStart != start || span.End - start != 73 ||
            unwind.ClassifySpan(start, span.End).Kind !=
                X64UnwindProof.SpanKind.HandlerFree ||
            !unwind.MatchesUnwind(start, span.End, 6, 0, SavedRbxFrame))
            return false;

        method.EnsureRawBytes();
        var body = X86Utils.Iterate(method).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(span.End - 1, false);
        if (method.RawBytes.Length != 73 || body.Length != 17 ||
            rawStart < 0 || rawEnd - rawStart != 72 ||
            rawEnd >= pe.GetRawBinaryContent().Length ||
            !pe.GetRawBinaryContent().Slice(checked((int)rawStart), 73)
                .SequenceEqual(method.RawBytes.AsSpan()) ||
            body[0].IP != start || body[^1].NextIP != span.End ||
            Enumerable.Range(0, 73).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                    unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) !=
                    rawStart + offset) ||
            Enumerable.Range(1, 72).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong>()) != null ||
            TryProveShape(body) is not { } proved)
            return false;

        shape = proved;
        return true;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 17 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !PushRbx(body[0]) || !Stack(body[1], Mnemonic.Sub) ||
            !RipCompareZero(body[2], out var flag) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[4], body[8].IP) ||
            !RipAddress(body[5], NativeRegister.RCX, out var slot) ||
            !DirectCall(body[6]) ||
            !RipStoreOne(body[7], flag) ||
            !RipLoad(body[8], NativeRegister.RCX, slot) ||
            !ClassInitCheck(body[9]) ||
            !Branch(body[10], body[12].IP) ||
            !DirectCall(body[11]) ||
            !ZeroEdx(body[12]) ||
            !Move(body[13], NativeRegister.RCX, NativeRegister.RBX) ||
            !Stack(body[14], Mnemonic.Add) || !PopRbx(body[15]) ||
            !DirectJump(body[16]))
            return null;

        return new Shape(flag, slot, body[6].NearBranchTarget,
            body[11].NearBranchTarget, body[16].NearBranchTarget);
    }

    private static bool BindGuard(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, Shape shape,
        TypeAnalysisContext guardedClass)
    {
        var app = method.AppContext;
        if (Overlaps(shape.OnceFlag, 1, shape.TypeInfoSlot, 8) ||
            !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.OnceFlag) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                unwind, shape.TypeInfoSlot, 8) ||
            shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentify(app, pe,
                unwind, shape.MetadataInitializer) ||
            shape.ClassInitializer == 0 ||
            shape.ClassInitializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_runtime_class_init_export ||
            shape.ClassInitializer != pe.GetVirtualAddressOfExportedFunctionByName(
                "il2cpp_runtime_class_init"))
            return false;

        var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
            shape.TypeInfoSlot);
        return usage is { Type: MetadataUsageType.TypeInfo, IsValid: true } &&
               ReferenceEquals(app.ResolveIl2CppType(usage.AsType()),
                   guardedClass);
    }

    private static bool ExplicitInitializerClass(TypeAnalysisContext type) =>
        X64ClassCastLookupProof.PublicCastAncestor(type) &&
        type.Definition?.HasCctor == true &&
        (type.Attributes & TypeAttributes.BeforeFieldInit) == 0 &&
        type.Methods.Count(method => method.Name == ".cctor") == 1;

    private static bool OnlyConstructor(TypeAnalysisContext owner,
        ApplicationAnalysisContext app, out MethodAnalysisContext constructor)
    {
        constructor = null!;
        var constructors = owner.Methods.Where(method => method.Name == ".ctor")
            .ToArray();
        if (constructors is not [var selected] ||
            !OrdinaryConstructor(selected, owner, app))
            return false;
        constructor = selected;
        return true;
    }

    private static bool OrdinaryConstructor(MethodAnalysisContext method,
        TypeAnalysisContext owner, ApplicationAnalysisContext app) =>
        method.Name == ".ctor" && method.Name == method.DefaultName &&
        method.Visibility == MethodAttributes.Public &&
        !method.IsStatic && !method.IsVirtual && method.IsVoid &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.SpecialName |
            MethodAttributes.RTSpecialName)) ==
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        (method.Attributes & (MethodAttributes.Abstract |
            MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
            MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall)) == 0 &&
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding: false);

    private static bool ExactlyTwoAliases(ApplicationAnalysisContext app,
        ulong address, MethodAnalysisContext first, MethodAnalysisContext second) =>
        app.MethodsByAddress.TryGetValue(address, out var bindings) &&
        bindings.Count == 2 && !ReferenceEquals(first, second) &&
        bindings.Count(candidate => ReferenceEquals(candidate, first)) == 1 &&
        bindings.Count(candidate => ReferenceEquals(candidate, second)) == 1;

    private static bool Overlaps(ulong first, ulong firstLength,
        ulong second, ulong secondLength) =>
        first > ulong.MaxValue - firstLength ||
        second > ulong.MaxValue - secondLength ||
        first < second + secondLength && second < first + firstLength;

    private static bool PushRbx(NativeInstruction instruction) =>
        instruction.Code == Code.Push_r64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RBX;

    private static bool PopRbx(NativeInstruction instruction) =>
        instruction.Code == Code.Pop_r64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RBX;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool RipCompareZero(NativeInstruction instruction,
        out ulong flag)
    {
        flag = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Cmp_rm8_imm8 &&
               instruction.Op0Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 1 &&
               instruction.Op1Kind == OpKind.Immediate8 &&
               instruction.Immediate8 == 0;
    }

    private static bool RipAddress(NativeInstruction instruction,
        NativeRegister destination, out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Lea_r64_m &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == destination &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None;
    }

    private static bool RipStoreOne(NativeInstruction instruction, ulong flag) =>
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
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
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
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.EDX;

    private static bool Move(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, ulong target) =>
        instruction.Code == Code.Jne_rel8_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool DirectJump(NativeInstruction instruction) =>
        instruction.Code == Code.Jmp_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
