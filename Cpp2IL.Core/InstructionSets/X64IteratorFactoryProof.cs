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
/// Proves a complete exact-target iterator factory. The allocation TypeInfo, the
/// captured owner and state fields, and the one-argument constructor are bound
/// separately from player metadata and native code. In particular, a folded call
/// to Object::.ctor is not mistaken for the iterator constructor.
/// </summary>
internal static class X64IteratorFactoryProof
{
    private static readonly byte[] SavedRbxRdiFrame = [0x0A, 0x34, 0x06, 0x00, 0x0A, 0x32, 0x06, 0x70];

    internal sealed record Evidence(TypeAnalysisContext Iterator, MethodAnalysisContext Constructor,
        FieldAnalysisContext State, FieldAnalysisContext Owner);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var result = new ISIL.Register(null, "iterator_factory_result");
        return
        [
            new(0, ISIL.OpCode.Newobj, result, proof.Iterator),
            new(1, ISIL.OpCode.CallVoid, proof.Constructor, result, new ISIL.Immediate(0)),
            new(2, ISIL.OpCode.Move, new ISIL.MemoryOperand(result, null, proof.Owner.Offset),
                new ISIL.Register(null, "rcx")),
            new(3, ISIL.OpCode.Return, result),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { } owner ||
            !HasFactorySignature(method, owner, decoded))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End - region.Start is < 112 or > 127 ||
            !unwind.MatchesUnwind(region.Start, region.End, 10, 0, SavedRbxRdiFrame))
            return null;
        var body = decoded.Take(28).ToArray();
        if (!ProveNativeShape(body, region.End, pe, unwind, app) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[27].IP }) != null ||
            Enumerable.Range(1, checked((int)(body[27].NextIP - region.Start) - 1))
                .Any(offset => app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)))
            return null;

        var slot = body[9].IPRelativeMemoryAddress;
        var flag = body[3].IPRelativeMemoryAddress;
        if (slot <= flag && flag - slot < 8 ||
            !WritableFileBacked(pe, unwind, slot, 8) ||
            !ZeroInitialized(unwind, flag) ||
            app.LibCpp2IlContext.GetRawTypeGlobalByAddress(slot) is not
                { Type: MetadataUsageType.TypeInfo, IsValid: true } usage ||
            app.ResolveIl2CppType(usage.AsType()) is not { } iterator ||
            !HasAllocatedIteratorMetadata(iterator, owner, method))
            return null;

        var stateOffset = body[20].MemoryDisplacement64;
        var ownerOffset = body[17].MemoryDisplacement64;
        if (stateOffset is < 16 or > int.MaxValue || ownerOffset is < 16 or > int.MaxValue ||
            stateOffset == ownerOffset)
            return null;
        var stateFields = iterator.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)stateOffset).ToArray();
        var ownerFields = iterator.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)ownerOffset).ToArray();
        if (stateFields is not [{ } state] || ownerFields is not [{ } capturedOwner] ||
            state.Name != state.DefaultName || capturedOwner.Name != capturedOwner.DefaultName ||
            capturedOwner.Visibility != FieldAttributes.Public ||
            (capturedOwner.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0 ||
            !ReferenceEquals(state.FieldType, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(capturedOwner.FieldType, owner) ||
            state.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } ||
            capturedOwner.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 })
            return null;
        var receiver = new ISIL.LocalVariable("proved-iterator", new ISIL.Register(null, "rax"), iterator);
        if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                new ISIL.FieldReference(state, receiver, (int)stateOffset), 32) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new ISIL.FieldReference(capturedOwner, receiver, (int)ownerOffset)))
            return null;

        var constructors = iterator.Methods.Where(candidate => candidate.Name == ".ctor" &&
            candidate.Parameters.Count == 1).ToArray();
        if (constructors is not [{ } constructor] ||
            !ProveConstructor(constructor, iterator, state, body[16].NearBranchTarget,
                pe, unwind, app))
            return null;
        return new Evidence(iterator, constructor, state, capturedOwner);
    }

    private static bool HasFactorySignature(MethodAnalysisContext method,
        TypeAnalysisContext owner, IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (owner.Definition is not { GenericContainer: null } ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.IsStatic || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters.Count != 0 ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.ReturnType.FullName != "System.Collections.IEnumerator" ||
            !method.ReturnType.IsInterface ||
            !ReferenceEquals(method.ReturnType.DeclaringAssembly,
                app.SystemTypes.SystemObjectType.DeclaringAssembly) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer == 0 || decoded.Count < 28 ||
            decoded[0].IP != method.UnderlyingPointer)
            return false;
        return true;
    }

    private static bool HasAllocatedIteratorMetadata(TypeAnalysisContext iterator,
        TypeAnalysisContext owner, MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (iterator.Definition is not { GenericContainer: null } ||
            iterator.IsValueType || iterator.IsInterface || iterator.IsGenericInstance ||
            iterator.GenericParameters.Count != 0 ||
            iterator.Name != iterator.DefaultName ||
            iterator.Namespace != iterator.DefaultNamespace ||
            iterator.Attributes != iterator.DefaultAttributes ||
            iterator.Definition.HasCctor ||
            iterator.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(iterator.BaseType, app.SystemTypes.SystemObjectType) ||
            !iterator.InterfaceContexts.Contains(method.ReturnType) ||
            !ReferenceEquals(iterator.DeclaringAssembly, owner.DeclaringAssembly) ||
            !SourceAccessibleType(iterator, owner))
            return false;
        return true;
    }

    private static bool ProveNativeShape(IReadOnlyList<NativeInstruction> body, ulong regionEnd,
        PE pe, X64UnwindProof.Index unwind, ApplicationAnalysisContext app)
    {
        if (body.Count != 28 || body[27].NextIP > regionEnd ||
            regionEnd - body[27].NextIP > 15 ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[27].NextIP, regionEnd) ||
            !FileBacked(pe, body[0].IP, body[27].NextIP) ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return false;

        var helpers = app.GetOrCreateKeyFunctionAddresses();
        return Store(body[0], NativeRegister.RSP, 8, NativeRegister.RBX, 8) &&
            Push(body[1], NativeRegister.RDI) && Stack(body[2], Mnemonic.Sub, 0x20) &&
            RipByteCompareZero(body[3]) && Move(body[4], NativeRegister.RDI, NativeRegister.RCX) &&
            Branch(body[5], Mnemonic.Jne, body[9].IP) &&
            RipLea(body[6], NativeRegister.RCX, body[9].IPRelativeMemoryAddress) &&
            Call(body[7], helpers.il2cpp_codegen_initialize_runtime_metadata) &&
            X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                body[7].NearBranchTarget) &&
            RipByteStoreOne(body[8], body[3].IPRelativeMemoryAddress) &&
            RipLoad(body[9], NativeRegister.RCX) &&
            DirectCall(body[10]) &&
            X64IteratorAllocatorProof.IsAllocator(app, body[10].NearBranchTarget) &&
            Move(body[11], NativeRegister.RBX, NativeRegister.RAX) &&
            Test(body[12], NativeRegister.RAX) &&
            Branch(body[13], Mnemonic.Je, body[27].IP) &&
            Xor(body[14], NativeRegister.EDX) &&
            Move(body[15], NativeRegister.RCX, NativeRegister.RAX) &&
            DirectCall(body[16]) &&
            FieldLea(body[17], NativeRegister.RCX, NativeRegister.RBX) &&
            Move(body[18], NativeRegister.RDX, NativeRegister.RDI) &&
            Store(body[19], NativeRegister.RCX, 0, NativeRegister.RDI, 8) &&
            StoreZero(body[20], NativeRegister.RBX) &&
            DirectCall(body[21]) &&
            X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, body[21].NearBranchTarget) &&
            Move(body[22], NativeRegister.RAX, NativeRegister.RBX) &&
            Load(body[23], NativeRegister.RBX, NativeRegister.RSP, 0x30) &&
            Stack(body[24], Mnemonic.Add, 0x20) &&
            Pop(body[25], NativeRegister.RDI) && body[26].Code == Code.Retnq &&
            body[26].OpCount == 0 && DirectCall(body[27]) &&
            X86RuntimeNullThrowProof.TryIdentify(app, body[27].NearBranchTarget) != null;
    }

    private static bool ProveConstructor(MethodAnalysisContext constructor,
        TypeAnalysisContext iterator, FieldAnalysisContext state, ulong objectCall,
        PE pe, X64UnwindProof.Index unwind, ApplicationAnalysisContext app)
    {
        if (!HasConstructorSignature(constructor, iterator, app) ||
            constructor.UnderlyingPointer == 0 ||
            !app.MethodsByAddress.TryGetValue(constructor.UnderlyingPointer, out var bindings) ||
            !bindings.Contains(constructor) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(constructor) ||
            !ProveInertObjectConstructor(app, objectCall, pe, unwind))
            return false;

        constructor.EnsureRawBytes();
        var body = X86Utils.Iterate(constructor).ToArray();
        var region = unwind.ClassifySpan(constructor.UnderlyingPointer, constructor.UnderlyingPointer + 1);
        if (body.Length != 12 || region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != constructor.UnderlyingPointer || region.RootStart != region.Start ||
            region.End != body[^1].NextIP || body[0].IP != region.Start ||
            !MatchesConstructorUnwind(unwind, region.Start, region.End) ||
            !FileBacked(pe, region.Start, region.End) ||
            !TryProveConstructorShape(body, state.Offset, objectCall) ||
            X86CallerExceptionRegionProof.Check(constructor, body, new HashSet<ulong>()) != null)
            return false;
        return true;
    }

    internal static bool MatchesConstructorUnwind(X64UnwindProof.Index unwind,
        ulong start, ulong end) =>
        unwind.MatchesUnwind(start, end, 10, 0, SavedRbxRdiFrame);

    internal static bool HasConstructorSignature(MethodAnalysisContext constructor,
        TypeAnalysisContext iterator, ApplicationAnalysisContext app)
    {
        if (constructor.IsStatic || constructor.IsVirtual || constructor.Name != ".ctor" ||
            constructor.Name != constructor.DefaultName ||
            constructor.DeclaringType != iterator || constructor.GenericParameters.Count != 0 ||
            constructor.OverrideReturnType != null || !constructor.IsVoid ||
            constructor.Visibility != MethodAttributes.Public ||
            constructor.Attributes != constructor.DefaultAttributes ||
            constructor.ImplAttributes != constructor.DefaultImplAttributes ||
            (constructor.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (constructor.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                           MethodImplAttributes.ManagedMask |
                                           MethodImplAttributes.InternalCall)) != 0 ||
            constructor.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [var parameter] ||
            parameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(constructor.Parameters[0].ParameterType, app.SystemTypes.SystemInt32Type) ||
            constructor.Parameters[0].Definition != parameter ||
            constructor.Parameters[0].IsRef ||
            constructor.Parameters[0].OverrideParameterType != null ||
            constructor.Parameters[0].Attributes != constructor.Parameters[0].DefaultAttributes)
            return false;
        return true;
    }

    internal static bool TryProveConstructorShape(IReadOnlyList<NativeInstruction> body,
        long stateOffset, ulong objectCall)
    {
        if (body.Count != 12 || stateOffset is < 16 or > int.MaxValue || objectCall == 0 ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !Store(body[0], NativeRegister.RSP, 8, NativeRegister.RBX, 8) ||
            !Push(body[1], NativeRegister.RDI) || !Stack(body[2], Mnemonic.Sub, 0x20) ||
            !Move(body[3], NativeRegister.EDI, NativeRegister.EDX) ||
            !Move(body[4], NativeRegister.RBX, NativeRegister.RCX) ||
            !Xor(body[5], NativeRegister.EDX) || !Call(body[6], objectCall) ||
            !Store(body[7], NativeRegister.RBX, (ulong)stateOffset, NativeRegister.EDI, 4) ||
            !Load(body[8], NativeRegister.RBX, NativeRegister.RSP, 0x30) ||
            !Stack(body[9], Mnemonic.Add, 0x20) || !Pop(body[10], NativeRegister.RDI) ||
            body[11].Code != Code.Retnq || body[11].OpCount != 0)
            return false;
        return true;
    }

    internal static bool ProveInertObjectConstructor(ApplicationAnalysisContext app, ulong target,
        PE pe, X64UnwindProof.Index unwind)
    {
        var candidates = app.SystemTypes.SystemObjectType.Methods.Where(candidate =>
            candidate.Name == ".ctor" && candidate.Parameters.Count == 0).ToArray();
        if (candidates is not [{ } constructor] || constructor.UnderlyingPointer != target ||
            !ReferenceEquals(constructor.DeclaringType, app.SystemTypes.SystemObjectType) ||
            constructor.IsStatic || !constructor.IsVoid ||
            constructor.Attributes != constructor.DefaultAttributes ||
            constructor.ImplAttributes != constructor.DefaultImplAttributes ||
            !app.MethodsByAddress.TryGetValue(target, out var aliases) ||
            !aliases.Contains(constructor))
            return false;
        constructor.EnsureRawBytes();
        var body = X86Utils.Iterate(constructor).ToArray();
        return constructor.RawBytes.Length == 3 && body is
                   [{ Code: Code.Retnq_imm16, Immediate16: 0 }] &&
               FileBacked(pe, target, target + 3) &&
               unwind.ClassifySpan(target, target + 3) is
                   { Kind: X64UnwindProof.SpanKind.NoEntry };
    }

    private static bool SourceAccessibleType(TypeAnalysisContext iterator, TypeAnalysisContext owner) =>
        iterator.DeclaringType == null && iterator.Visibility == TypeAttributes.Public ||
        ReferenceEquals(iterator.DeclaringType, owner) &&
        iterator.Visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedPrivate;

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
    }

    private static bool WritableFileBacked(PE pe, X64UnwindProof.Index unwind, ulong address, uint size)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - size ||
            address + size - 1 - unwind.ImageBase > uint.MaxValue ||
            !FileBacked(pe, address, address + size))
            return false;
        for (var offset = 0U; offset < size; offset++)
            if (!unwind.IsWritableFileBackedRva((uint)(address + offset - unwind.ImageBase)))
                return false;
        return true;
    }

    private static bool ZeroInitialized(X64UnwindProof.Index unwind, ulong address) =>
        address >= unwind.ImageBase && address - unwind.ImageBase <= uint.MaxValue &&
        unwind.IsWritableZeroInitializedRva((uint)(address - unwind.ImageBase));

    private static bool Move(NativeInstruction instruction, NativeRegister destination, NativeRegister source) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source &&
        instruction.Code is Code.Mov_r64_rm64 or Code.Mov_r32_rm32;

    private static bool Store(NativeInstruction instruction, NativeRegister basis, ulong offset,
        NativeRegister source, int width) => instruction.Op0Kind == OpKind.Memory &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source &&
        instruction.MemoryBase == basis && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == width &&
        instruction.Code is Code.Mov_rm64_r64 or Code.Mov_rm32_r32;

    private static bool Load(NativeInstruction instruction, NativeRegister destination,
        NativeRegister basis, ulong offset) => instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong value) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == value;

    private static bool Push(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Push_r64 && instruction.Op0Register == register;

    private static bool Pop(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Pop_r64 && instruction.Op0Register == register;

    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.NearBranchTarget != 0;

    private static bool Call(NativeInstruction instruction, ulong target) =>
        target != 0 && DirectCall(instruction) && instruction.NearBranchTarget == target;

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Test_rm64_r64 && instruction.Op0Register == register &&
        instruction.Op1Register == register;

    private static bool Xor(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Xor_r32_rm32 && instruction.Op0Register == register &&
        instruction.Op1Register == register;

    private static bool RipByteCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 && instruction.Immediate8 == 0;

    private static bool RipByteStoreOne(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 && instruction.IPRelativeMemoryAddress == address &&
        instruction.Immediate8 == 1;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination, ulong address) =>
        instruction.Code == Code.Lea_r64_m && instruction.Op0Register == destination &&
        instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
        instruction.IPRelativeMemoryAddress == address;

    private static bool RipLoad(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 8;

    private static bool FieldLea(NativeInstruction instruction, NativeRegister destination,
        NativeRegister basis) => instruction.Code == Code.Lea_r64_m &&
        instruction.Op0Register == destination && instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue;

    private static bool StoreZero(NativeInstruction instruction, NativeRegister basis) =>
        instruction.Code == Code.Mov_rm32_imm32 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == basis && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue &&
        instruction.MemorySize.GetSize() == 4 && instruction.Immediate32 == 0;
}
