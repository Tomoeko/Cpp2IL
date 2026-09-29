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
/// Proves a complete throw-only method from the exact Windows x64 Release player.
/// A terminal CALL is an exit only after its target is tied to the installed
/// codegen raise wrapper and that wrapper's complete effects are authenticated.
/// </summary>
internal static class X64TerminalManagedThrowProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];
    private static readonly byte[] Stack28Frame = [0x04, 0x42];

    internal sealed record Evidence(TypeAnalysisContext ExceptionType, MethodAnalysisContext Constructor);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var exception = new ISIL.Register(null, "proved_throw_exception");
        return
        [
            new(0, ISIL.OpCode.Newobj, exception, proof.ExceptionType),
            new(1, ISIL.OpCode.CallVoid, proof.Constructor, exception, new ISIL.Immediate(0)),
            new(2, ISIL.OpCode.Throw, exception),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        method.EnsureRawBytes();
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            !HasCallerIdentity(method) || decoded.Count < 17 ||
            method.RawBytes.Length < 70 ||
            method.UnderlyingPointer > ulong.MaxValue - 70 ||
            decoded[0].IP != method.UnderlyingPointer)
            return null;

        // A metadata-derived body span can continue into a later native function.
        // The unwind record bounds this closed terminal-throw body.
        var body = decoded.Take(17).ToArray();
        if (!TryProveCallerShape(body))
            return null;

        var start = method.UnderlyingPointer;
        var end = start + 70;
        var region = unwind.ClassifySpan(start, end);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End < end || region.End - end > 15 ||
            body[^1].NextIP != end ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame) ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, region.End) ||
            !FileBacked(pe, start, end, method.RawBytes.AsSpan().Slice(0, 70)) ||
            !X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End)
                .SequenceEqual(decoded.TakeWhile(instruction => instruction.IP < region.End)) ||
            Enumerable.Range(1, checked((int)(region.End - start)) - 1)
                .Any(offset => app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return null;

        var metadataTarget = body[3].NearBranchTarget;
        if (body[13].NearBranchTarget != metadataTarget ||
            metadataTarget != app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app, pe,
                unwind, metadataTarget) ||
            !X64IteratorAllocatorProof.IsAllocator(app, body[5].NearBranchTarget) ||
            !ProveNullCheck(app, pe, unwind, body[8].NearBranchTarget) ||
            !ProveRaiseWrapper(app, pe, unwind, body[16].NearBranchTarget))
            return null;

        var evidence = BindMetadata(method, pe, unwind,
            body[2].IPRelativeMemoryAddress, body[12].IPRelativeMemoryAddress,
            body[11].NearBranchTarget);
        if (evidence == null ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[16].IP }) != null)
            return null;

        return evidence;
    }

    internal static Evidence? BindMetadata(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, ulong typeSlot, ulong methodSlot, ulong constructorTarget)
    {
        var app = method.AppContext;
        if (typeSlot == methodSlot ||
            typeSlot <= methodSlot && methodSlot - typeSlot < 8 ||
            methodSlot <= typeSlot && typeSlot - methodSlot < 8 ||
            !WritableFileBacked(pe, unwind, typeSlot, 8) ||
            !WritableFileBacked(pe, unwind, methodSlot, 8) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, typeSlot, 8) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, methodSlot, 8) ||
            app.LibCpp2IlContext.GetRawTypeGlobalByAddress(typeSlot) is not
                { Type: MetadataUsageType.TypeInfo, IsValid: true } typeUsage ||
            app.ResolveIl2CppType(typeUsage.AsType()) is not { } exceptionType ||
            !HasExceptionType(exceptionType, app) ||
            app.LibCpp2IlContext.GetMethodGlobalByAddress(methodSlot) is not
                { Type: MetadataUsageType.MethodDef, IsValid: true } methodUsage ||
            !ReferenceEquals(methodUsage.AsMethod(), method.Definition) ||
            !app.MethodsByAddress.TryGetValue(constructorTarget, out var constructors) ||
            constructors is not [{ } constructor] ||
            !HasConstructor(constructor, exceptionType, app, constructorTarget))
            return null;

        return new Evidence(exceptionType, constructor);
    }

    internal static bool TryProveCallerShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 17 || !WellFormed(body))
            return false;
        return Push(body[0], NativeRegister.RBX) && Stack(body[1], Mnemonic.Sub, 0x20) &&
            RipLea(body[2], NativeRegister.RCX) && DirectCall(body[3]) &&
            Move(body[4], NativeRegister.RCX, NativeRegister.RAX) && DirectCall(body[5]) &&
            Move(body[6], NativeRegister.RCX, NativeRegister.RAX) &&
            Move(body[7], NativeRegister.RBX, NativeRegister.RAX) && DirectCall(body[8]) &&
            Zero(body[9], NativeRegister.EDX) && Move(body[10], NativeRegister.RCX, NativeRegister.RBX) &&
            DirectCall(body[11]) && RipLea(body[12], NativeRegister.RCX) &&
            DirectCall(body[13]) && Move(body[14], NativeRegister.RDX, NativeRegister.RAX) &&
            Move(body[15], NativeRegister.RCX, NativeRegister.RBX) && DirectCall(body[16]);
    }

    private static bool HasCallerIdentity(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || owner.GenericParameters.Count != 0 ||
            owner.IsGenericInstance || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.OverrideReturnType != null ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !HasUnchangedThrowSignature(method) ||
            !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [{ } unique] || !ReferenceEquals(unique, method))
            return false;
        return true;
    }

    private static bool HasUnchangedThrowSignature(MethodAnalysisContext method)
    {
        if (!method.IsStatic)
            return RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method);
        if (method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [{ } rawParameter] ||
            method.Parameters is not [{ } parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            rawParameter.RawType is not { NumMods: 0, Byref: 1, Pinned: 0 } ||
            parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.Name != parameter.DefaultName ||
            parameter.UseOverrideDefaultValue)
            return false;

        // The exact caller shape replaces RCX with the TypeInfo slot before
        // its first call. Only a static method's sole incoming byref occupies
        // RCX; an instance method passes its first parameter in RDX, which an
        // allocator callee may observe before the caller clears EDX.
        return true;
    }

    private static bool HasExceptionType(TypeAnalysisContext type, ApplicationAnalysisContext app)
    {
        var corlib = app.SystemTypes.SystemObjectType.DeclaringAssembly;
        if (!ReferenceEquals(type.DeclaringAssembly, corlib) ||
            type.Definition is not { GenericContainer: null, DeclaringType: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } ||
            type.DeclaringType != null || type.Visibility != TypeAttributes.Public ||
            type.IsValueType || type.IsInterface || type.IsAbstract || type.IsGenericInstance ||
            type.GenericParameters.Count != 0 || type.Name != type.DefaultName ||
            type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType))
            return false;
        var exception = corlib.Types.Where(candidate => candidate.Namespace == "System" &&
            candidate.Name == "Exception" && candidate.DeclaringType == null).ToArray();
        if (exception is not [{ } baseException])
            return false;
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type.BaseType; current != null && visited.Add(current); current = current.BaseType)
            if (ReferenceEquals(current, baseException))
                return true;
        return false;
    }

    private static bool HasConstructor(MethodAnalysisContext constructor,
        TypeAnalysisContext exceptionType, ApplicationAnalysisContext app, ulong target)
    {
        if (constructor.UnderlyingPointer != target ||
            !ReferenceEquals(constructor.DeclaringType, exceptionType) ||
            constructor.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, exceptionType.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            constructor.Name != ".ctor" || constructor.Name != constructor.DefaultName ||
            constructor.IsStatic || constructor.IsVirtual || constructor.Parameters.Count != 0 ||
            constructor.GenericParameters.Count != 0 || constructor.OverrideReturnType != null ||
            constructor.Visibility != MethodAttributes.Public ||
            constructor.Attributes != constructor.DefaultAttributes ||
            constructor.ImplAttributes != constructor.DefaultImplAttributes ||
            (constructor.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) !=
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) ||
            (constructor.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                           MethodImplAttributes.ManagedMask |
                                           MethodImplAttributes.InternalCall)) != 0 ||
            !ReferenceEquals(constructor.ReturnType, app.SystemTypes.SystemVoidType) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(constructor) ||
            exceptionType.Methods.Count(candidate => candidate.Name == ".ctor" &&
                candidate.Parameters.Count == 0) != 1)
            return false;
        return true;
    }

    internal static bool ProveNullCheck(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        var body = Read(pe, target, 6);
        if (body == null || !WellFormed(body) ||
            body[0].IP != target || body[0].Length != 4 || !Stack(body[0], Mnemonic.Sub, 0x28) ||
            !Test(body[1], NativeRegister.RCX) ||
            !Branch(body[2], Code.Je_rel8_64, body[5].IP) ||
            !Stack(body[3], Mnemonic.Add, 0x28) || !Return(body[4]) ||
            !DirectCall(body[5]) ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[5].NearBranchTarget) == null)
            return false;
        var region = unwind.ClassifySpan(target, body[^1].NextIP);
        return region.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               region.Start == target && region.RootStart == target &&
               region.End - body[^1].NextIP <= 16 &&
               unwind.MatchesUnwind(region.Start, region.End, 4, 0, Stack28Frame) &&
               X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, target,
                   checked((uint)(body[^1].NextIP - target))) &&
               X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) &&
               NoManagedAliases(app, region.Start, region.End) &&
               X86CallerExceptionRegionProof.Check(body, target,
                   new HashSet<ulong> { body[5].IP }, unwind.ClassifySpan) == null;
    }

    internal static bool ProveRaiseWrapper(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        if (!X64CodegenRaiseExceptionProof.TryIdentify(app, target))
            return false;
        var export = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_raise_exception");
        var entry = Read(pe, export, 3);
        var body = Read(pe, target, 12);
        if (entry == null || body == null ||
            Read(pe, body[6].NearBranchTarget, 2) is not { } clear)
            return false;

        // The reusable component owns the trace writes, exported raise ABI,
        // frame operations and relocation proof. This throw-only route retains
        // its stricter helper boundaries, trap padding and native ownership.
        var exportRegion = unwind.ClassifySpan(export, entry[^1].NextIP);
        var wrapperRegion = unwind.ClassifySpan(target, body[^1].NextIP);
        return exportRegion.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               exportRegion.Start == export && exportRegion.RootStart == export &&
               exportRegion.End - entry[^1].NextIP <= 16 &&
               X64NativePaddingProof.HasInt3Padding(pe, entry[^1].NextIP, exportRegion.End) &&
               NoManagedAliases(app, exportRegion.Start, exportRegion.End) &&
               wrapperRegion.Kind == X64UnwindProof.SpanKind.HandlerFree &&
               wrapperRegion.Start == target && wrapperRegion.RootStart == target &&
               wrapperRegion.End - body[^1].NextIP <= 16 &&
               X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, wrapperRegion.End) &&
               NoManagedAliases(app, wrapperRegion.Start, wrapperRegion.End) &&
               X64NativePaddingProof.HasInt3Padding(pe, clear[^1].NextIP, clear[0].IP + 16) &&
               NoManagedAliases(app, clear[0].IP, clear[0].IP + 16);
    }

    private static bool NoManagedAliases(ApplicationAnalysisContext app, ulong start, ulong end) =>
        end > start && end - start <= 256 &&
        Enumerable.Range(0, checked((int)(end - start))).All(offset =>
            !app.MethodsByAddress.ContainsKey(start + (ulong)offset));

    private static NativeInstruction[]? Read(PE pe, ulong address, int count)
    {
        var start = pe.GetVirtualAddressOfPrimaryExecutableSection();
        var code = pe.GetEntirePrimaryExecutableSection();
        if (address < start || address - start >= (ulong)code.Length)
            return null;
        var offset = checked((int)(address - start));
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(
            code.Slice(offset, Math.Min(count * 15, code.Length - offset)).ToArray()), address);
        var result = new NativeInstruction[count];
        var next = address;
        for (var index = 0; index < count; index++)
        {
            var instruction = decoder.Decode();
            if (instruction.IP != next || instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None ||
                !FileBacked(pe, instruction.IP, instruction.NextIP))
                return null;
            result[index] = instruction;
            next = instruction.NextIP;
        }
        return result;
    }

    private static bool FileBacked(PE pe, ulong start, ulong end,
        ReadOnlySpan<byte> expected = default)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || last < first || last >= image.Length ||
            (ulong)(last - first) != end - start - 1)
            return false;
        return expected.IsEmpty || expected.Length == (int)(end - start) &&
            image.Slice((int)first, expected.Length).SequenceEqual(expected);
    }

    private static bool WritableFileBacked(PE pe, X64UnwindProof.Index unwind,
        ulong address, uint length)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - length ||
            address + length - 1 - unwind.ImageBase > uint.MaxValue ||
            !FileBacked(pe, address, address + length))
            return false;
        for (var offset = 0U; offset < length; offset++)
            if (!unwind.IsWritableFileBackedRva((uint)(address + offset - unwind.ImageBase)))
                return false;
        return true;
    }

    private static bool WellFormed(IReadOnlyList<NativeInstruction> body) =>
        body.Count > 0 &&
        body.All(instruction => !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
            !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
            instruction.SegmentPrefix == NativeRegister.None) &&
        !body.Where((instruction, index) => index > 0 &&
            instruction.IP != body[index - 1].NextIP).Any();

    private static bool Push(NativeInstruction i, NativeRegister register) =>
        i.Code == Code.Push_r64 && i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Stack(NativeInstruction i, Mnemonic operation, ulong amount) =>
        i.Mnemonic == operation && i.Op0Kind == OpKind.Register &&
        i.Op0Register == NativeRegister.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == amount;

    private static bool Move(NativeInstruction i, NativeRegister destination, NativeRegister source) =>
        i.Code == Code.Mov_r64_rm64 && i.Op0Kind == OpKind.Register &&
        i.Op0Register == destination && i.Op1Kind == OpKind.Register &&
        i.Op1Register == source;

    private static bool Zero(NativeInstruction i, NativeRegister register) =>
        i.Code == Code.Xor_r32_rm32 && i.Op0Kind == OpKind.Register &&
        i.Op1Kind == OpKind.Register && i.Op0Register == register && i.Op1Register == register;

    private static bool Test(NativeInstruction i, NativeRegister register) =>
        i.Code == Code.Test_rm64_r64 && i.Op0Kind == OpKind.Register &&
        i.Op1Kind == OpKind.Register && i.Op0Register == register && i.Op1Register == register;

    private static bool RipLea(NativeInstruction i, NativeRegister destination) =>
        i.Code == Code.Lea_r64_m && i.Op0Kind == OpKind.Register &&
        i.Op0Register == destination && i.Op1Kind == OpKind.Memory &&
        i.MemoryBase == NativeRegister.RIP && i.MemoryIndex == NativeRegister.None;

    private static bool DirectCall(NativeInstruction i) =>
        i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
        i.NearBranchTarget != 0;

    private static bool Branch(NativeInstruction i, Code code, ulong target) =>
        i.Code == code && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Return(NativeInstruction i) => i.Code == Code.Retnq && i.OpCount == 0;
}
