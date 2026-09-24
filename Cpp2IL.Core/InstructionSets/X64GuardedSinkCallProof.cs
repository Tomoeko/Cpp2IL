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
/// Authenticates an exact-target instance void(Exception) wrapper that loads a
/// string literal, initializes an explicitly initialized sink class, and tail
/// calls its static void(string, Exception) method.
/// </summary>
internal static class X64GuardedSinkCallProof
{
    private const int BodyLength = 93;
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(string Literal, MethodAnalysisContext SinkMethod);

    internal readonly record struct Shape(ulong OnceFlag, ulong TypeInfoSlot,
        ulong LiteralSlot, ulong MetadataInitializer, ulong ClassInitializer,
        ulong Tail);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryWrapper(method, app) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var callerBindings) ||
                callerBindings is not [var bound] ||
                !ReferenceEquals(bound, method) ||
                !TryReadBody(method, pe, unwind, out var body) ||
                TryProveShape(body) is not { } shape)
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

    private static bool TryReadBody(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, out NativeInstruction[] body)
    {
        body = [];
        var start = method.UnderlyingPointer;
        if (start is 0 or ulong.MaxValue || start > ulong.MaxValue - BodyLength)
            return false;

        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End - start != BodyLength ||
            unwind.ClassifySpan(start, region.End).Kind !=
                X64UnwindProof.SpanKind.HandlerFree ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, SavedRbxFrame) ||
            !X64NativePaddingProof.HasInt3Padding(pe, region.End,
                checked((region.End + 15) & ~15UL)))
            return false;

        method.EnsureRawBytes();
        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        if (method.RawBytes.Length < BodyLength || rawStart < 0 ||
            rawStart > image.Length - BodyLength ||
            !image.Slice((int)rawStart, BodyLength).SequenceEqual(
                method.RawBytes.AsSpan().Slice(0, BodyLength)) ||
            Enumerable.Range(0, BodyLength).Any(offset =>
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) !=
                    rawStart + offset ||
                !unwind.IsExecutableRva(checked((uint)(start +
                    (ulong)offset - unwind.ImageBase)))) ||
            Enumerable.Range(1, BodyLength - 1).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(
                    start + (ulong)offset)))
            return false;

        body = X86Utils.Iterate(method.RawBytes.AsSpan().Slice(0, BodyLength),
            start, is32Bit: false).ToArray();
        return body.Length == 20 && body[0].IP == start &&
               body[^1].NextIP == region.End &&
               X86CallerExceptionRegionProof.Check(method, body,
                   new HashSet<ulong>()) == null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 20 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !PushRbx(body[0]) || !Stack(body[1], Mnemonic.Sub) ||
            !RipCompareZero(body[2], out var flag) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RDX) ||
            !Branch(body[4], body[10].IP) ||
            !RipAddress(body[5], out var typeSlot) ||
            !DirectCall(body[6]) ||
            !RipAddress(body[7], out var literalSlot) ||
            !DirectCall(body[8]) ||
            body[6].NearBranchTarget != body[8].NearBranchTarget ||
            !RipStoreOne(body[9], flag) ||
            !RipLoad(body[10], typeSlot) ||
            !ClassInitCheck(body[11]) ||
            !Branch(body[12], body[14].IP) ||
            !DirectCall(body[13]) ||
            !RipLoad(body[14], literalSlot) ||
            !ZeroR8d(body[15]) ||
            !Move(body[16], NativeRegister.RDX, NativeRegister.RBX) ||
            !Stack(body[17], Mnemonic.Add) || !PopRbx(body[18]) ||
            !DirectJump(body[19]))
            return null;

        return new Shape(flag, typeSlot, literalSlot,
            body[6].NearBranchTarget, body[13].NearBranchTarget,
            body[19].NearBranchTarget);
    }

    // Find establishes the PE-backed body first. Keeping metadata binding
    // separate lets fixture negatives exercise each identity requirement.
    internal static Evidence? BindProvedShape(MethodAnalysisContext method,
        Shape shape)
    {
        try
        {
            var app = method.AppContext;
            if (app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryWrapper(method, app) ||
                Overlaps(shape.OnceFlag, 1, shape.TypeInfoSlot, 8) ||
                Overlaps(shape.OnceFlag, 1, shape.LiteralSlot, 8) ||
                Overlaps(shape.TypeInfoSlot, 8, shape.LiteralSlot, 8) ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind,
                    shape.OnceFlag) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                    unwind, shape.TypeInfoSlot, 8) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    shape.TypeInfoSlot, 8) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe,
                    unwind, shape.LiteralSlot, 8) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    shape.LiteralSlot, 8) ||
                shape.MetadataInitializer == 0 ||
                shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata ||
                !X64MetadataInitializationHelperProof.TryIdentify(app, pe,
                    unwind, shape.MetadataInitializer) ||
                !X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(
                    app, pe, unwind, shape.MetadataInitializer) ||
                shape.ClassInitializer == 0 ||
                shape.ClassInitializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_runtime_class_init_export ||
                shape.ClassInitializer != pe.GetVirtualAddressOfExportedFunctionByName(
                    "il2cpp_runtime_class_init"))
                return null;

            var typeUsage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(
                shape.TypeInfoSlot);
            var literalUsage = app.LibCpp2IlContext.GetLiteralGlobalByAddress(
                shape.LiteralSlot);
            if (typeUsage is not { Type: MetadataUsageType.TypeInfo,
                    IsValid: true } ||
                literalUsage is not { Type: MetadataUsageType.StringLiteral,
                    IsValid: true } ||
                app.ResolveIl2CppType(typeUsage.AsType()) is not { } sink ||
                !ExplicitInitializerSink(sink) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    method.DeclaringType!.DeclaringAssembly,
                    sink.DeclaringAssembly) ||
                !app.MethodsByAddress.TryGetValue(shape.Tail, out var targets) ||
                targets is not [var tail] ||
                !SinkMethod(tail, sink, app, shape.Tail))
                return null;

            return new Evidence(literalUsage.AsLiteral(), tail);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryWrapper(MethodAnalysisContext method,
        ApplicationAnalysisContext app)
    {
        if (method.DeclaringType is not { } owner ||
            !X64ClassCastLookupProof.PublicOrdinaryClass(owner) ||
            !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [var rawParameter] } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            rawParameter.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.Visibility != MethodAttributes.Public ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.ParameterType,
                app.SystemTypes.SystemExceptionType) ||
            !ReferenceEquals(parameter.DefaultParameterType,
                app.SystemTypes.SystemExceptionType) ||
            parameter.IsRef || parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType,
                app.SystemTypes.SystemVoidType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer == 0)
            return false;
        return true;
    }

    private static bool ExplicitInitializerSink(TypeAnalysisContext sink) =>
        X64ClassCastLookupProof.PublicCastAncestor(sink) &&
        sink.Definition?.HasCctor == true &&
        (sink.Attributes & TypeAttributes.BeforeFieldInit) == 0 &&
        sink.Methods.Count(method => method.Name == ".cctor") == 1;

    private static bool SinkMethod(MethodAnalysisContext method,
        TypeAnalysisContext sink, ApplicationAnalysisContext app, ulong address)
    {
        if (!ReferenceEquals(method.DeclaringType, sink) ||
            method.UnderlyingPointer != address ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [var firstRaw, var secondRaw] } definition ||
            !ReferenceEquals(definition.DeclaringType, sink.Definition) ||
            firstRaw.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            secondRaw.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Name != method.DefaultName ||
            method.Visibility != MethodAttributes.Public ||
            !method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Parameters is not [var first, var second] ||
            !ReferenceEquals(first.Definition, firstRaw) ||
            !ReferenceEquals(second.Definition, secondRaw) ||
            !ReferenceEquals(first.ParameterType,
                app.SystemTypes.SystemStringType) ||
            !ReferenceEquals(second.ParameterType,
                app.SystemTypes.SystemExceptionType) ||
            !ReferenceEquals(first.DefaultParameterType,
                app.SystemTypes.SystemStringType) ||
            !ReferenceEquals(second.DefaultParameterType,
                app.SystemTypes.SystemExceptionType) ||
            first.IsRef || second.IsRef ||
            first.OverrideParameterType != null ||
            second.OverrideParameterType != null ||
            first.Attributes != first.DefaultAttributes ||
            second.Attributes != second.DefaultAttributes ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType,
                app.SystemTypes.SystemVoidType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall)) != 0)
            return false;
        return true;
    }

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
        instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool RipCompareZero(NativeInstruction instruction,
        out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Cmp_rm8_imm8 &&
               instruction.Op0Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 1 &&
               instruction.Op1Kind == OpKind.Immediate8 &&
               instruction.Immediate8 == 0;
    }

    private static bool RipAddress(NativeInstruction instruction,
        out ulong address)
    {
        address = instruction.IPRelativeMemoryAddress;
        return instruction.Code == Code.Lea_r64_m &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RCX &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RIP &&
               instruction.MemoryIndex == NativeRegister.None;
    }

    private static bool RipStoreOne(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == address &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 1;

    private static bool RipLoad(NativeInstruction instruction, ulong slot) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RCX &&
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

    private static bool ZeroR8d(NativeInstruction instruction) =>
        instruction.Code == Code.Xor_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.R8D &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.R8D;

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
