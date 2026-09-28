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
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete sealed Int32 isinst followed by a guarded unbox comparison.
/// The cold invalid-cast call is unreachable: the earlier exact class equality
/// fixes both element_class reads to the same non-enum Int32 class. This proof
/// does not assign any semantics to that call when it is reachable elsewhere.
/// </summary>
internal static class X64GuardedBoxedInt32Proof
{
    private static readonly byte[] SavedRbxRdiFrame =
        [0x0A, 0x34, 0x06, 0x00, 0x0A, 0x32, 0x06, 0x70];

    internal sealed record Shape(ulong OnceFlag, ulong TypeInfoSlot,
        ulong MetadataInitializer, ulong UnboxExport, ulong ColdCall);

    internal static bool Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not
                    { } unwind || !OrdinaryMethod(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) || bindings is not [var bound] ||
                !ReferenceEquals(bound, method))
                return false;

            var start = method.UnderlyingPointer;
            var region = unwind.ClassifySpan(start, start + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != start || region.RootStart != start ||
                region.End - start != 125 ||
                unwind.ClassifySpan(start, region.End).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(start, region.End, 10, 0,
                    SavedRbxRdiFrame) ||
                app.MethodsByAddress.Keys.Any(address =>
                    address > start && address < region.End))
                return false;

            var decoded = X86Utils.Iterate(method).TakeWhile(instruction =>
                instruction.IP < region.End).ToArray();
            if (decoded.Length != 37 || decoded[36].Code != Code.Int3 ||
                decoded[36].NextIP != region.End ||
                TryProveShape(decoded.Take(36).ToArray()) is not { } shape ||
                decoded[35].NextIP != decoded[36].IP ||
                !X64NativePaddingProof.HasInt3Padding(pe,
                    decoded[35].NextIP, region.End))
                return false;

            method.EnsureRawBytes();
            var bodyLength = checked((int)(decoded[35].NextIP - start));
            if (method.RawBytes.Length < bodyLength ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, method.RawBytes.AsSpan().Slice(0, bodyLength), start) ||
                !X86Utils.Iterate(method).Take(decoded.Length).SequenceEqual(decoded))
                return false;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    // Find authenticates the complete file-backed caller first. This separate
    // binding permits metadata and helper negative tests without patching a PE.
    internal static bool BindProvedShape(MethodAnalysisContext method, Shape shape)
    {
        try
        {
            var app = method.AppContext;
            if (!OrdinaryMethod(method) || app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                shape.TypeInfoSlot <= shape.OnceFlag &&
                shape.OnceFlag - shape.TypeInfoSlot < 8 ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind,
                    shape.OnceFlag) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                    unwind, shape.TypeInfoSlot, 8) ||
                shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata ||
                !X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app,
                    pe, unwind, shape.MetadataInitializer) ||
                !ProvesUnboxExport(app, pe, unwind, shape.UnboxExport) ||
                !ExecutableUnboundTarget(app, unwind, shape.ColdCall))
                return false;

            var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                shape.TypeInfoSlot);
            var int32 = app.SystemTypes.SystemInt32Type;
            return usage is { Type: MetadataUsageType.TypeInfo, IsValid: true } &&
                ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), int32) &&
                int32.IsValueType && int32.IsSealed && !int32.IsEnumType &&
                int32.Definition is { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } } &&
                int32.DeclaringType == null &&
                int32.GenericParameters.Count == 0 &&
                ReferenceEquals(int32.DeclaringAssembly,
                    app.SystemTypes.SystemObjectType.DeclaringAssembly) &&
                ReferenceEquals(int32.Definition.DeclaringAssembly,
                    int32.DeclaringAssembly.Definition?.Image) &&
                int32.Name == int32.DefaultName && int32.Name == "Int32" &&
                int32.Namespace == int32.DefaultNamespace &&
                int32.Namespace == "System" && int32.OverrideNamespace == null &&
                ReferenceEquals(int32.BaseType, int32.DefaultBaseType) &&
                int32.Attributes == int32.DefaultAttributes;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 36 || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !MemoryRegister(body[0], Code.Mov_rm64_r64, 0,
                NativeRegister.RSP, 8, 8, NativeRegister.RBX) ||
            !PushPop(body[1], Code.Push_r64, NativeRegister.RDI) ||
            !Stack(body[2], Code.Sub_rm64_imm8) ||
            !RipByteCompareZero(body[3]) ||
            !Registers(body[4], Code.Mov_r32_rm32,
                NativeRegister.EDI, NativeRegister.EDX) ||
            !Registers(body[5], Code.Mov_r64_rm64,
                NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[6], Code.Jne_rel8_64, body[10].IP) ||
            !RipLea(body[7], NativeRegister.RCX) ||
            !DirectCall(body[8]) ||
            !RipByteStoreOne(body[9], body[3].IPRelativeMemoryAddress) ||
            !Registers(body[10], Code.Test_rm64_r64,
                NativeRegister.RBX, NativeRegister.RBX) ||
            !Branch(body[11], Code.Je_rel8_64, body[30].IP) ||
            !RegisterMemory(body[12], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX, 0, 8) ||
            !Registers(body[13], Code.Xor_r32_rm32,
                NativeRegister.EAX, NativeRegister.EAX) ||
            !RipLoad(body[14], NativeRegister.RDX,
                body[7].IPRelativeMemoryAddress) ||
            !Registers(body[15], Code.Cmp_r64_rm64,
                NativeRegister.RCX, NativeRegister.RDX) ||
            !Registers(body[16], Code.Cmove_r64_rm64,
                NativeRegister.RAX, NativeRegister.RBX) ||
            !Registers(body[17], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[18], Code.Je_rel8_64, body[30].IP) ||
            !RegisterMemory(body[19], Code.Mov_r64_rm64,
                NativeRegister.RAX, NativeRegister.RDX,
                Il2CppClassLayout.ElementClassOffset64, 8) ||
            !MemoryRegister(body[20], Code.Cmp_rm64_r64, 0,
                NativeRegister.RCX, Il2CppClassLayout.ElementClassOffset64,
                8, NativeRegister.RAX) ||
            !Registers(body[21], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX) ||
            !Branch(body[22], Code.Jne_rel8_64, body[35].IP) ||
            !DirectCall(body[23]) ||
            !MemoryRegister(body[24], Code.Cmp_rm32_r32, 0,
                NativeRegister.RAX, 0, 4, NativeRegister.EDI) ||
            !UnaryRegister(body[25], Code.Sete_rm8, NativeRegister.AL) ||
            !ReturnEpilog(body, 26, false) ||
            !ReturnEpilog(body, 30, true) ||
            !DirectCall(body[35]))
            return null;

        // IsInstSealed compares the object's exact class with this TypeInfo.
        // On the true edge RCX == RDX and RBX is unchanged. Both element_class
        // reads therefore use one pointer and one offset without an intervening
        // call or write. The immediately following cold branch is unreachable;
        // its unsupported helper does not need an invented no-return contract.
        return new Shape(body[3].IPRelativeMemoryAddress,
            body[7].IPRelativeMemoryAddress, body[8].NearBranchTarget,
            body[23].NearBranchTarget, body[35].NearBranchTarget);
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.DeclaringType is { } owner &&
            X64MetadataStaticGetterProof.OrdinaryOwner(owner) &&
            ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
            method.Definition is { GenericContainer: null, parameterCount: 2,
                InternalParameterData: [var rawValue, var rawExpected],
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
            ReferenceEquals(definition.DeclaringType, owner.Definition) &&
            rawValue.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                NumMods: 0, Byref: 0, Pinned: 0 } &&
            rawExpected.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } &&
            method.Parameters is [var value, var expected] &&
            Parameter(value, rawValue, method, 0,
                app.SystemTypes.SystemObjectType) &&
            Parameter(expected, rawExpected, method, 1,
                app.SystemTypes.SystemInt32Type) &&
            ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) &&
            ReferenceEquals(method.DefaultReturnType, method.ReturnType) &&
            method.IsStatic && !method.IsVirtual && !method.IsVoid &&
            method.Name is not (".ctor" or ".cctor") &&
            method.Name == method.DefaultName &&
            method.GenericParameters.Count == 0 &&
            method.OverrideReturnType == null &&
            method.Attributes == method.DefaultAttributes &&
            method.ImplAttributes == method.DefaultImplAttributes &&
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) == 0 &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) == 0 &&
            !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
            RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
            method.UnderlyingPointer is not (0 or ulong.MaxValue);
    }

    private static bool Parameter(ParameterAnalysisContext parameter,
        Il2CppParameterDefinition raw, MethodAnalysisContext method, int index,
        TypeAnalysisContext type) =>
        ReferenceEquals(parameter.Definition, raw) &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        parameter.ParameterIndex == index && !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null &&
        ReferenceEquals(parameter.ParameterType, type);

    private static bool ProvesUnboxExport(ApplicationAnalysisContext app,
        PE pe, X64UnwindProof.Index unwind, ulong address)
    {
        var exported = pe.GetVirtualAddressOfExportedFunctionByName(
            "il2cpp_object_unbox");
        if (address == 0 || address != exported ||
            address != app.GetOrCreateKeyFunctionAddresses().il2cpp_object_unbox ||
            X64NativeInstructionReader.Read(pe, unwind, address, 1, 16) is not
                [var thunk] ||
            thunk.Code != Code.Jmp_rel32_64 ||
            thunk.Op0Kind != OpKind.NearBranch64 ||
            !UnwindFreeLeaf(unwind, address, thunk.NextIP))
            return false;

        var leafAddress = thunk.NearBranchTarget;
        return leafAddress != 0 && leafAddress != address &&
            X64NativeInstructionReader.Read(pe, unwind, leafAddress, 2, 16) is
                [var payload, var ret] &&
            payload.IP == leafAddress && payload.Code == Code.Lea_r64_m &&
            payload.Op0Kind == OpKind.Register &&
            payload.Op0Register == NativeRegister.RAX &&
            Address(payload, 1, NativeRegister.RCX, 16) &&
            ret.IP == payload.NextIP && ret.Code == Code.Retnq &&
            ret.OpCount == 0 &&
            UnwindFreeLeaf(unwind, leafAddress, ret.NextIP);
    }

    private static bool UnwindFreeLeaf(X64UnwindProof.Index unwind,
        ulong start, ulong end) =>
        unwind.ClassifySpan(start, end).Kind == X64UnwindProof.SpanKind.NoEntry;

    private static bool ExecutableUnboundTarget(ApplicationAnalysisContext app,
        X64UnwindProof.Index unwind, ulong address) =>
        address >= unwind.ImageBase &&
        address - unwind.ImageBase <= uint.MaxValue &&
        unwind.IsExecutableRva((uint)(address - unwind.ImageBase)) &&
        !app.MethodsByAddress.ContainsKey(address);

    private static bool ReturnEpilog(IReadOnlyList<NativeInstruction> body,
        int index, bool falseResult) =>
        RegisterMemory(body[index], Code.Mov_r64_rm64,
            NativeRegister.RBX, NativeRegister.RSP, 0x30, 8) &&
        (!falseResult || Registers(body[index + 1], Code.Xor_r8_rm8,
            NativeRegister.AL, NativeRegister.AL)) &&
        Stack(body[index + (falseResult ? 2 : 1)], Code.Add_rm64_imm8) &&
        PushPop(body[index + (falseResult ? 3 : 2)], Code.Pop_r64,
            NativeRegister.RDI) &&
        body[index + (falseResult ? 4 : 3)].Code == Code.Retnq &&
        body[index + (falseResult ? 4 : 3)].OpCount == 0;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister first, NativeRegister second) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == first &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == second;

    private static bool UnaryRegister(NativeInstruction instruction, Code code,
        NativeRegister register) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.OpCount == 1;

    private static bool Memory(NativeInstruction instruction, int operand,
        NativeRegister @base, ulong displacement, int size) =>
        Address(instruction, operand, @base, displacement) &&
        instruction.MemorySize.GetSize() == size;

    private static bool Address(NativeInstruction instruction, int operand,
        NativeRegister @base, ulong displacement) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == @base &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == displacement;

    private static bool RegisterMemory(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister @base,
        ulong displacement, int size) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        Memory(instruction, 1, @base, displacement, size);

    private static bool MemoryRegister(NativeInstruction instruction, Code code,
        int operand, NativeRegister @base, ulong displacement,
        int size, NativeRegister source) =>
        instruction.Code == code &&
        Memory(instruction, operand, @base, displacement, size) &&
        instruction.GetOpKind(1 - operand) == OpKind.Register &&
        instruction.GetOpRegister(1 - operand) == source;

    private static bool RipByteCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 &&
        RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 0;

    private static bool RipByteStoreOne(NativeInstruction instruction,
        ulong flag) =>
        instruction.Code == Code.Mov_rm8_imm8 &&
        RipMemory(instruction, 0, 1) &&
        instruction.IPRelativeMemoryAddress == flag &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 1;

    private static bool RipLea(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Code == Code.Lea_r64_m &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && RipMemory(instruction, 1, 0);

    private static bool RipLoad(NativeInstruction instruction,
        NativeRegister register, ulong slot) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && RipMemory(instruction, 1, 8) &&
        instruction.IPRelativeMemoryAddress == slot;

    private static bool RipMemory(NativeInstruction instruction,
        int operand, int size) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == size;

    private static bool PushPop(NativeInstruction instruction, Code code,
        NativeRegister register) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.OpCount == 1;

    private static bool Stack(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
