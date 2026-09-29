using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Projects one Single component from an eight-byte by-value aggregate that was
/// spilled into a private native frame. The aggregate keeps its managed type.
/// Packed SIMD, arbitrary spills and partial overlapping writes remain unproved.
/// </summary>
internal static class X64AggregateScalarOperandProof
{
    internal const string EvidenceKey = "X64AggregateScalarOperandProof.ComponentReads";
    internal const string ComparisonCaptureName = "AGGREGATE_COMPARE_READ";
    internal sealed record Shape(NativeInstruction Store, NativeInstruction Load, int ComponentOffset);
    internal sealed record Evidence(Shape Shape, FieldAnalysisContext Field, int ParameterIndex);

    internal static IReadOnlyList<Evidence>? GetEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<List<Evidence>>(EvidenceKey);

    internal static void Record(MethodAnalysisContext method, Evidence evidence)
    {
        var reads = method.GetExtraData<List<Evidence>>(EvidenceKey) ?? [];
        if (!reads.Contains(evidence))
            reads.Add(evidence);
        method.PutExtraData(EvidenceKey, reads);
    }

    internal static Evidence? Find(MethodAnalysisContext? method, NativeInstruction load)
    {
        if (method is not { Definition: not null, DeclaringType: { Definition: not null }, AppContext: { } app })
            return null;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.IsVirtual || method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.DeclaringType is not { GenericParameters.Count: 0 } ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return null;

        if (ReadBody(method) is not { } body ||
            TryFind(body, load.IP) is not { } shape || shape.Load != load)
            return null;

        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var offset = method.IsStatic ? 0 : 1;
        var register = new ISIL.Register(null, X86Utils.GetRegisterName(shape.Store.Op1Register));
        var parameters = method.Parameters.Where(parameter => parameter.ParameterIndex + offset < abi.Length &&
            abi[parameter.ParameterIndex + offset] is ISIL.Register incoming &&
            incoming.Number == register.Number).ToArray();
        if (parameters is not [{ } parameter] || parameter.IsRef || parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            parameter.Definition?.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            parameter.ParameterType is not { Definition: { IsValueType: true, IsEnumType: false,
                IsBlittable: true, IsImportOrWindowsRuntime: false, IsByRefLike: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true }, GenericParameters.Count: 0 } aggregate ||
            aggregate is GenericInstanceTypeAnalysisContext || aggregate.Attributes != aggregate.DefaultAttributes ||
            aggregate.Name != aggregate.DefaultName || aggregate.Namespace != aggregate.DefaultNamespace ||
            (aggregate.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !ReferenceEquals(aggregate.BaseType, aggregate.DefaultBaseType) ||
            TypeSizes.UnboxedSize(aggregate, 8) != 8)
            return null;

        var fields = aggregate.Fields.Where(field => !field.IsStatic).OrderBy(field => field.Offset).ToArray();
        if (fields is not [{ Offset: 0 } first, { Offset: 4 } second] ||
            fields.Any(field => field.Offset != field.DefaultOffset || field.Attributes != field.DefaultAttributes ||
                field.Name != field.DefaultName || field.OverrideFieldType != null ||
                !ReferenceEquals(field.DeclaringType, aggregate) ||
                !ReferenceEquals(field.BackingData?.Field.DeclaringType, aggregate.Definition) ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemSingleType) ||
                field.Visibility != FieldAttributes.Public && !ReferenceEquals(aggregate, method.DeclaringType) ||
                (field.Attributes & (FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
                field.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4,
                    NumMods: 0, Byref: 0, Pinned: 0 }))
            return null;
        return new Evidence(shape, shape.ComponentOffset == 0 ? first : second, parameter.ParameterIndex);
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
        => X64NativeInstructionReader.ReadRootBody(method);

    internal static Shape? TryFind(IReadOnlyList<NativeInstruction> body, ulong loadAddress)
    {
        if (body.Count is < 4 or > 128 || !Contiguous(body) ||
            body[0] is not { Code: Code.Sub_rm64_imm8 or Code.Sub_rm64_imm32,
                Op0Kind: OpKind.Register, Op0Register: NativeRegister.RSP } allocation ||
            !Clean(allocation) || allocation.GetImmediate(1) is < 8 or > 4096)
            return null;
        var frameSize = allocation.GetImmediate(1);
        var loadIndex = Enumerable.Range(1, body.Count - 1).SingleOrDefault(index => body[index].IP == loadAddress);
        if (loadIndex == 0 || !IsComponentRead(body[loadIndex], frameSize))
            return null;
        var load = body[loadIndex];
        var prefixEnd = 1;
        while (prefixEnd < body.Count && (IsAggregateStore(body[prefixEnd], frameSize) ||
                                         IsComponentLoad(body[prefixEnd], frameSize)))
            prefixEnd++;
        if (prefixEnd == body.Count || loadIndex > prefixEnd ||
            loadIndex == prefixEnd && !IsComparisonRead(load, frameSize))
            return null;
        var stores = body.Take(loadIndex).Where(instruction => IsAggregateStore(instruction, frameSize) &&
            instruction.MemoryDisplacement64 <= load.MemoryDisplacement64 &&
            instruction.MemoryDisplacement64 + 8 >= load.MemoryDisplacement64 + 4).ToArray();
        if (stores is not [var store] ||
            load.MemoryDisplacement64 - store.MemoryDisplacement64 is not (0 or 4))
            return null;

        // Before the read, only private stack stores and scalar reads are admitted.
        // No call, branch, address escape, frame alias or unknown write can intervene.
        foreach (var instruction in body.Skip(1).Take(loadIndex - 1))
            if (!IsAggregateStore(instruction, frameSize) && !IsComponentLoad(instruction, frameSize))
                return null;
        if (body.Take(loadIndex).Any(instruction => IsAggregateStore(instruction, frameSize) &&
            instruction.IP != store.IP && RangesOverlap(instruction.MemoryDisplacement64, 8,
                store.MemoryDisplacement64, 8)))
            return null;

        var information = new InstructionInfoFactory();
        var sawUse = IsComparisonRead(load, frameSize);
        foreach (var instruction in body.Skip(loadIndex + 1))
        {
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall || !Clean(instruction))
                return null;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                (instruction.Op0Kind != OpKind.NearBranch64 || instruction.NearBranchTarget <= load.IP ||
                 !body.Any(target => target.IP == instruction.NearBranchTarget)))
                return null;
            foreach (var used in information.GetInfo(instruction).GetUsedRegisters())
            {
                if (used.Register.GetFullRegister() != load.Op0Register.GetFullRegister())
                    continue;
                if (used.Access is not (OpAccess.Read or OpAccess.CondRead) ||
                    instruction.Code is not (Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32) ||
                    instruction.Op0Kind != OpKind.Register ||
                    instruction.Op1Kind != OpKind.Register && !IsComparisonRead(instruction, frameSize))
                    return null;
                sawUse = true;
            }
            // Reject every stack address escape, unbounded frame use and unknown RSP mutation.
            if (information.GetInfo(instruction).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == NativeRegister.RSP) &&
                !(instruction.IP < body[prefixEnd].IP &&
                  (IsAggregateStore(instruction, frameSize) || IsComponentLoad(instruction, frameSize))) &&
                !(instruction.IP == body[prefixEnd].IP && IsComparisonRead(instruction, frameSize)) &&
                instruction.Code != Code.Retnq &&
                !(instruction.Code is Code.Add_rm64_imm8 or Code.Add_rm64_imm32 &&
                    instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
                    instruction.GetImmediate(1) == frameSize))
                return null;
        }
        return sawUse ? new Shape(store, load, (int)(load.MemoryDisplacement64 - store.MemoryDisplacement64)) : null;
    }

    private static bool IsAggregateStore(NativeInstruction instruction, ulong frameSize) =>
        instruction.Code == Code.Mov_rm64_r64 && Clean(instruction) &&
        instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register is NativeRegister.RCX or NativeRegister.RDX or NativeRegister.R8 or NativeRegister.R9 &&
        PrivateFrameMemory(instruction, frameSize, 8);

    private static bool IsComponentLoad(NativeInstruction instruction, ulong frameSize) =>
        instruction.Code == Code.Movss_xmm_xmmm32 && Clean(instruction) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Memory &&
        PrivateFrameMemory(instruction, frameSize, 4);

    private static bool IsComparisonRead(NativeInstruction instruction, ulong frameSize) =>
        instruction.Code is Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32 && Clean(instruction) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Memory &&
        PrivateFrameMemory(instruction, frameSize, 4);

    private static bool IsComponentRead(NativeInstruction instruction, ulong frameSize) =>
        IsComponentLoad(instruction, frameSize) || IsComparisonRead(instruction, frameSize);

    private static bool PrivateFrameMemory(NativeInstruction instruction, ulong frameSize, uint width) =>
        instruction.MemoryBase == NativeRegister.RSP && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 && instruction.MemorySize.GetSize() == width &&
        (instruction.MemoryDisplacement64 <= frameSize - width ||
         // Windows x64 gives the callee four eight-byte argument home slots.
         // The intervening return-address slot is never admitted as temporary storage.
         instruction.MemoryDisplacement64 >= frameSize + 8 &&
         instruction.MemoryDisplacement64 <= frameSize + 40 - width) &&
        instruction.MemoryDisplacement64 % width == 0;

    private static bool Contiguous(IReadOnlyList<NativeInstruction> body) =>
        body.Select((instruction, index) => !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
            (index == 0 || body[index - 1].NextIP == instruction.IP)).All(valid => valid);

    private static bool Clean(NativeInstruction instruction) => !instruction.IsInvalid &&
        instruction.CodeSize == CodeSize.Code64 && !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == NativeRegister.None;

    private static bool RangesOverlap(ulong first, ulong firstWidth, ulong second, ulong secondWidth) =>
        first < second + secondWidth && second < first + firstWidth;
}
