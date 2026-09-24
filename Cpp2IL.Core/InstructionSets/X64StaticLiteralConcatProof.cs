using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the complete exact-target static string-plus-literal tail caller.
/// Its guarded metadata initialization is accepted only for an independently
/// authenticated StringLiteral arm and one exact player metadata slot.
/// </summary>
internal static class X64StaticLiteralConcatProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(MethodAnalysisContext Concat, string Literal);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var result = new ISIL.Register(null, "static_literal_concat_result");
        return
        [
            new(0, ISIL.OpCode.Call, proof.Concat, result, new ISIL.Register(null, "rcx"),
                new ISIL.StringLiteral(proof.Literal), new ISIL.Immediate(0)),
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            !HasSignature(method, app) || decoded.Count < 14 ||
            decoded[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var callerBindings) ||
            callerBindings is not [{ } uniqueCaller] ||
            !ReferenceEquals(uniqueCaller, method))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End - region.Start != 60 ||
            !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
            !FileBacked(pe, region.Start, region.End))
            return null;

        var withinRegion = decoded.TakeWhile(instruction => instruction.IP < region.End).ToArray();
        if (withinRegion.Length != 14 || withinRegion[^1].NextIP != region.End ||
            !TryProveShape(withinRegion) ||
            X86CallerExceptionRegionProof.Check(method, withinRegion, new HashSet<ulong>()) != null ||
            Enumerable.Range(1, checked((int)(region.End - region.Start) - 1)).Any(offset =>
                app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)))
            return null;

        var flag = withinRegion[2].IPRelativeMemoryAddress;
        var slot = withinRegion[5].IPRelativeMemoryAddress;
        if (!ZeroInitialized(unwind, flag) ||
            !WritableFileBacked(pe, unwind, slot, 8) ||
            slot <= flag && flag - slot < 8 ||
            app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot) is not
                { Type: MetadataUsageType.StringLiteral, IsValid: true } usage ||
            app.LibCpp2IlContext.GetLiteralByAddress(slot) is not { } literal ||
            literal != usage.AsLiteral() ||
            !X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe, unwind,
                withinRegion[6].NearBranchTarget) ||
            !app.MethodsByAddress.TryGetValue(withinRegion[13].NearBranchTarget,
                out var targets) || targets is not [{ } concat] ||
            concat.UnderlyingPointer != withinRegion[13].NearBranchTarget ||
            !X64LiteralConcatProof.ProveConcat(concat, app))
            return null;

        return new Evidence(concat, literal);
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 14 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return false;

        return body[0].Code == Code.Push_r64 && Register(body[0], NativeRegister.RBX) &&
            Stack(body[1], Mnemonic.Sub, 0x20) &&
            RipCompareZero(body[2]) &&
            Move(body[3], NativeRegister.RBX, NativeRegister.RCX) &&
            Branch(body[4], Code.Jne_rel8_64, body[8].IP) &&
            RipLea(body[5], NativeRegister.RCX) &&
            DirectCall(body[6]) &&
            RipStoreOne(body[7], body[2].IPRelativeMemoryAddress) &&
            RipLoad(body[8], NativeRegister.RDX, body[5].IPRelativeMemoryAddress) &&
            Zero(body[9], NativeRegister.R8D) &&
            Move(body[10], NativeRegister.RCX, NativeRegister.RBX) &&
            Stack(body[11], Mnemonic.Add, 0x20) &&
            body[12].Code == Code.Pop_r64 && Register(body[12], NativeRegister.RBX) &&
            body[13].Code == Code.Jmp_rel32_64 &&
            body[13].Op0Kind == OpKind.NearBranch64 &&
            body[13].NearBranchTarget != 0;
    }

    private static bool HasSignature(MethodAnalysisContext method,
        ApplicationAnalysisContext app)
    {
        if (method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || owner.GenericParameters.Count != 0 ||
            owner.IsGenericInstance || owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            (owner.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.AutoLayout ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [var rawParameter] } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            !method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemStringType) ||
            !ReferenceEquals(parameter.DefaultParameterType, app.SystemTypes.SystemStringType) ||
            parameter.IsRef || parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemStringType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer == 0)
            return false;
        return true;
    }

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
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

    private static bool ZeroInitialized(X64UnwindProof.Index unwind, ulong address) =>
        address >= unwind.ImageBase && address - unwind.ImageBase <= uint.MaxValue &&
        unwind.IsWritableZeroInitializedRva((uint)(address - unwind.ImageBase));

    private static bool Register(NativeInstruction instruction, NativeRegister register) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong amount) =>
        instruction.Mnemonic == mnemonic && Register(instruction, NativeRegister.RSP) &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool RipCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m && Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool RipStoreOne(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == address &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool RipLoad(NativeInstruction instruction, NativeRegister destination,
        ulong address) => instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;

    private static bool Move(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) => instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && Register(instruction, register) &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
