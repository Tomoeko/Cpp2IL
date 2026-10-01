using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Represents an inlined reference-field read through an original accessible
/// property getter with the same complete native semantics. The caller's MOV
/// is not evidence of a native getter call or the original source expression.
/// </summary>
internal static partial class X64GetterReferenceArgumentProof
{
    internal const string EvidenceKey = "X64GetterReferenceArgumentProof";

    internal sealed record Shape(int SourceOffset, int TargetOffset, int PayloadOffset,
        NativeInstruction NullCall, NativeInstruction Invocation);

    private sealed record GetterLeaf(MethodAnalysisContext Method, FieldAnalysisContext Field,
        NativeInstruction Load, byte[] Bytes);

    internal sealed record Proof(Shape Native, FieldAnalysisContext SourceField, FieldAnalysisContext TargetField,
        FieldAnalysisContext PayloadField, MethodAnalysisContext Getter, MethodAnalysisContext Callee,
        MethodAnalysisContext NullIdentity, X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native &&
            ReferenceEquals(SourceField, other.SourceField) && ReferenceEquals(TargetField, other.TargetField) &&
            ReferenceEquals(PayloadField, other.PayloadField) && ReferenceEquals(Getter, other.Getter) &&
            ReferenceEquals(Callee, other.Callee) && ReferenceEquals(NullIdentity, other.NullIdentity) &&
            Input.Matches(other.Input);
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                !OrdinaryMethod(method, specialName: false) || method.Parameters.Count != 0 || !method.IsVoid ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                !X64SmallAggregateFieldGetterProof.OriginalAbi(method)) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 4, 0, [4, 0x42]) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullCall.NearBranchTarget) is not { } helper ||
                X86RuntimeNullThrowProof.BindIdentity(app) is not { } nullConstructor ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { shape.NullCall.IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var values = new List<object>();
            var seen = new HashSet<TypeAnalysisContext>();
            if (!CaptureChain(owner, values, seen) ||
                FindReferenceField(owner, shape.SourceOffset) is not { } sourceField ||
                FindReferenceField(owner, shape.TargetOffset) is not { } targetField ||
                !CaptureChain(sourceField.FieldType, values, seen) ||
                !CaptureChain(targetField.FieldType, values, seen) ||
                !AccessibleType(owner, sourceField.FieldType) || !AccessibleType(owner, targetField.FieldType) ||
                FindReferenceField(sourceField.FieldType, shape.PayloadOffset) is not { } payloadField ||
                !CaptureChain(payloadField.FieldType, values, seen) || !AccessibleType(owner, payloadField.FieldType) ||
                !TryFindGetter(method, sourceField.FieldType, payloadField, values, out var leaf) ||
                !TryFindCallee(method, shape.Invocation.NearBranchTarget, targetField.FieldType,
                    payloadField.FieldType, values, out var callee) ||
                !CaptureChain(nullConstructor.DeclaringType!, values, seen)) return null;

            var nativeValues = X64NativeInvocationValues.Create(body, new HashSet<ulong> { shape.NullCall.IP });
            var sourceRegister = body[1].Op0Register;
            if (nativeValues == null ||
                !nativeValues.Matches(body[1].IP, NativeRegister.RCX, 64, new(NativeRegister.RCX)) ||
                !nativeValues.Matches(body[4].IP, NativeRegister.RCX, 64, new(NativeRegister.RCX)) ||
                !nativeValues.Matches(body[2].IP, sourceRegister, 64, new(NativeRegister.None, body[1].IP, sourceRegister)) ||
                !nativeValues.Matches(body[7].IP, sourceRegister, 64, new(NativeRegister.None, body[1].IP, sourceRegister)) ||
                !nativeValues.Matches(body[5].IP, NativeRegister.RCX, 64, new(NativeRegister.None, body[4].IP, NativeRegister.RCX)) ||
                !nativeValues.Matches(shape.Invocation.IP, NativeRegister.RCX, 64,
                    new(NativeRegister.None, body[4].IP, NativeRegister.RCX)) ||
                !nativeValues.Matches(shape.Invocation.IP, NativeRegister.RDX, 64,
                    new(NativeRegister.None, body[7].IP, NativeRegister.RDX)) ||
                !nativeValues.Matches(shape.Invocation.IP, NativeRegister.R8, 64,
                    new(NativeRegister.None, Literal: 0)) ||
                !nativeValues.HasCallFrame(shape.NullCall.IP, tail: false) ||
                !nativeValues.HasCallFrame(shape.Invocation.IP, tail: true)) return null;

            if (!CaptureMethod(method, values) || !CaptureMethod(callee, values) ||
                !CaptureMethod(nullConstructor, values)) return null;
            values.Add(helper.NativeTarget);
            var start = body[0].IP;
            var length = checked((int)(body[^1].NextIP - start));
            var raw = checked((int)pe.MapVirtualAddressToRaw(start, false));
            var bytes = pe.GetRawBinaryContent().Slice(raw, length).ToArray().Concat(leaf.Bytes).ToArray();
            return new(shape, sourceField, targetField, payloadField, leaf.Method, callee, nullConstructor,
                new(values, bytes));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static bool TryAuthenticate(MethodAnalysisContext method, out Proof proof)
    {
        proof = null!;
        if (Find(method) is not { } current) return false;
        var saved = method.GetExtraData<Proof>(EvidenceKey);
        if (NativeRecoveryProofTracker.Has(method, EvidenceKey))
        {
            if (saved == null || !saved.Matches(current)) return false;
        }
        else
        {
            if (saved != null) return false;
            method.PutExtraData(EvidenceKey, current);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        }
        proof = current;
        return true;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is < 13 or > 28 || body.Any(InvalidInstruction) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !ReferenceLoad(body[1], body[1].Op0Register, NativeRegister.RCX, out var sourceOffset) ||
            body[1].Op0Register is not (NativeRegister.RDX or NativeRegister.RAX or NativeRegister.R8 or NativeRegister.R9) ||
            !RegisterPair(body[2], Code.Test_rm64_r64, body[1].Op0Register) || !NullBranch(body[3], body[11].IP) ||
            !ReferenceLoad(body[4], NativeRegister.RCX, NativeRegister.RCX, out var targetOffset) ||
            !RegisterPair(body[5], Code.Test_rm64_r64, NativeRegister.RCX) || !NullBranch(body[6], body[11].IP) ||
            !ReferenceLoad(body[7], NativeRegister.RDX, body[1].Op0Register, out var payloadOffset) ||
            !RegisterPair(body[8], Code.Xor_r32_rm32, NativeRegister.R8D) ||
            !X64Stack28BodyProof.Stack(body[9], Mnemonic.Add) ||
            body[10] is not { Code: Code.Jmp_rel32_64, Op0Kind: OpKind.NearBranch64 } ||
            body[10].NearBranchTarget == 0 || body[10].NearBranchTarget >= body[0].IP &&
                body[10].NearBranchTarget < body[^1].NextIP ||
            body[11] is not { Code: Code.Call_rel32_64, Op0Kind: OpKind.NearBranch64 } ||
            body[11].NearBranchTarget == 0 || body.Skip(12).Any(instruction => instruction.Code != Code.Int3 || instruction.Length != 1) ||
            sourceOffset == targetOffset) return null;
        return new(sourceOffset, targetOffset, payloadOffset, body[11], body[10]);
    }

    internal static bool TryProveGetterLeaf(IReadOnlyList<NativeInstruction> body, out int fieldOffset)
    {
        fieldOffset = 0;
        if (body.Count is < 2 or > 18 || body.Any(InvalidInstruction) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any()) return false;
        var first = body[0].Code == Code.Nopw && body[0].Length == 2 && body[0].OpCount == 0 ? 1 : 0;
        return body.Count >= first + 2 &&
            ReferenceLoad(body[first], NativeRegister.RAX, NativeRegister.RCX, out fieldOffset) &&
            body[first + 1] is { Code: Code.Retnq, OpCount: 0 } &&
            body.Skip(first + 2).All(instruction => instruction.Code == Code.Int3 && instruction.Length == 1);
    }

    private static GetterLeaf? ReadGetterLeaf(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE pe || X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.IsStatic || method.Parameters.Count != 0 || method.DeclaringType == null ||
            method.Definition?.RawReturnType is not { Type: LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !OriginalDescriptor(method.Definition.RawReturnType) ||
            ResolveClass(method.AppContext, method.Definition.RawReturnType) is not { } returnType ||
            !ReferenceEquals(method.ReturnType, returnType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false)) return null;
        if (method.RawBytes.Length == 0) method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        var length = method.RawBytes.Length;
        if (length is < 5 or > 32 || start > ulong.MaxValue - (ulong)length ||
            unwind.ClassifySpan(start, start + (ulong)length).Kind != X64UnwindProof.SpanKind.NoEntry ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, method.RawBytes.AsSpan(), start) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, start, (uint)length) ||
            X64NativeInstructionReader.HasInteriorManagedEntry(method.AppContext, start, start + (ulong)length)) return null;
        var body = X86Utils.Iterate(method.RawBytes.AsSpan(), start, false).ToArray();
        if (!TryProveGetterLeaf(body, out var offset) || body[0].IP != start || body[^1].NextIP != start + (ulong)length ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
            FindReferenceField(method.DeclaringType!, offset) is not { } field ||
            !ReferenceEquals(field.FieldType, method.ReturnType)) return null;
        var first = body[0].Code == Code.Nopw ? 1 : 0;
        return new(method, field, body[first], method.RawBytes.AsSpan().ToArray());
    }

    private static bool InvalidInstruction(NativeInstruction instruction) => instruction.IsInvalid ||
        instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
        instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None;

    private static bool ReferenceLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister owner, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 || instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register != destination || instruction.Op1Kind != OpKind.Memory ||
            instruction.MemoryBase != owner || instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 || instruction.MemorySize.GetSize() != 8 ||
            instruction.MemoryDisplacement64 is < 16 or > 0x1000 - 8 || instruction.MemoryDisplacement64 % 8 != 0)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool RegisterPair(NativeInstruction instruction, Code code, NativeRegister register) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register;

    private static bool NullBranch(NativeInstruction instruction, ulong target) =>
        instruction.Code is Code.Je_rel8_64 or Code.Je_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;
}
