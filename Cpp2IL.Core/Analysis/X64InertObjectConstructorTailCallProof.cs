using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a constructor's terminal call when unrelated methods share the inert
/// Object constructor body, including aliases with a hidden return buffer.
/// The complete caller must only store literals into its own instance fields
/// before tail jumping with the unchanged receiver and null MethodInfo.
/// </summary>
internal static class X64InertObjectConstructorTailCallProof
{
    internal static MethodAnalysisContext? Find(MethodAnalysisContext caller,
        ISIL.Instruction call, ulong target,
        IReadOnlyList<MethodAnalysisContext> aliases)
    {
        try
        {
            var app = caller.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                caller.DeclaringType is not { } owner ||
                !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
                !X64AncestorConstructorThunkProof.OrdinaryConstructor(caller) ||
                !X64AncestorConstructorThunkProof.OrdinaryOwner(owner) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(caller) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(
                    caller, requireUniqueBinding: false) ||
                call.OpCode != Cpp2IL.Core.ISIL.OpCode.CallVoid ||
                call.DeferredCallReturns == null ||
                call.Operands.Count < 3 ||
                call.Operands[0] is not Immediate destination ||
                destination.UnsignedValue != target ||
                call.Operands[1] is not LocalVariable
                    { IsThis: true, Type: { } receiverType } ||
                !ReferenceEquals(receiverType, owner) ||
                caller.ParameterLocals.Count == 0 ||
                !ReferenceEquals(call.Operands[1], caller.ParameterLocals[0]) ||
                call.Operands[2] is not Immediate { Value: 0 } ||
                app.InstructionSet.CallingConventionResolver is not { } convention ||
                !convention.HasRawArgumentLayout(call, app) ||
                aliases.Count < 2 ||
                aliases.Count != new HashSet<MethodAnalysisContext>(aliases).Count ||
                aliases.Any(alias => alias.Name == ".ctor" &&
                    ReferenceEquals(alias.DeclaringType, owner) ||
                    convention.ReturnsViaHiddenBuffer(alias) &&
                    (ReferenceEquals(alias.DeclaringType, owner) ||
                     ReferenceEquals(alias.DeclaringType,
                         app.SystemTypes.SystemObjectType))))
                return null;

            var constructors = app.SystemTypes.SystemObjectType.Methods
                .Where(method => method.Name == ".ctor" && !method.IsStatic &&
                    method.Parameters.Count == 0 && method.IsVoid &&
                    method.UnderlyingPointer == target).ToArray();
            if (constructors is not [{ } baseConstructor] ||
                aliases.Count(method => ReferenceEquals(method, baseConstructor)) != 1 ||
                !X64IteratorFactoryProof.ProveInertObjectConstructor(
                    app, target, pe, unwind))
                return null;

            caller.EnsureRawBytes();
            var start = caller.UnderlyingPointer;
            if (start == 0 || caller.RawBytes.Length is < 11 or > 128 ||
                start > ulong.MaxValue - (ulong)caller.RawBytes.Length)
                return null;
            var end = start + (ulong)caller.RawBytes.Length;
            if (unwind.ClassifySpan(start, end) is not
                    { Kind: X64UnwindProof.SpanKind.NoEntry,
                        Start: var spanStart,
                        End: var spanEnd } ||
                spanStart != start || spanEnd != end ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(
                    pe, unwind, caller.RawBytes.AsSpan(), start) ||
                app.MethodsByAddress.Keys.Any(address =>
                    address > start && address < end))
                return null;

            var native = X86Utils.Iterate(caller.RawBytes.AsSpan(), start, false);
            var receiver = (LocalVariable)call.Operands[1];
            if (native.Count is < 3 or > 18 ||
                native[0].IP != start || native[^1].NextIP != end ||
                native[^1].IP != call.NativeAddress ||
                !native.Zip(native.Skip(1), (before, after) =>
                    before.NextIP == after.IP).All(connected => connected) ||
                native.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None) ||
                !IsZeroMethodInfo(native[0]) ||
                native.Skip(1).Take(native.Count - 2).Any(instruction =>
                    !IsOwnLiteralStore(instruction, owner, receiver)) ||
                !IsTailTo(native[^1], target) ||
                X86CallerExceptionRegionProof.Check(caller, native,
                    new HashSet<ulong>()) != null)
                return null;

            return baseConstructor;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    private static bool IsZeroMethodInfo(NativeInstruction instruction) =>
        instruction.Code == Code.Xor_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EDX &&
        instruction.Op1Register == NativeRegister.EDX;

    private static bool IsOwnLiteralStore(NativeInstruction instruction,
        TypeAnalysisContext owner, LocalVariable receiver)
    {
        var width = instruction.Code switch
        {
            Code.Mov_rm8_imm8 => 8,
            Code.Mov_rm16_imm16 => 16,
            Code.Mov_rm32_imm32 => 32,
            Code.Mov_rm64_imm32 => 64,
            _ => 0,
        };
        if (width == 0 || instruction.FlowControl != FlowControl.Next ||
            instruction.Op0Kind != OpKind.Memory ||
            instruction.MemoryBase != NativeRegister.RCX ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemorySize.GetSize() != width / 8 ||
            instruction.MemoryDisplacement64 is < 16 or > int.MaxValue)
            return false;

        var offset = (long)instruction.MemoryDisplacement64;
        var fields = owner.Fields.Where(field =>
            !field.IsStatic && field.Offset == offset).ToArray();
        if (fields is not [{ } field])
            return false;
        var reference = new FieldReference(field, receiver, (int)offset);
        // A native immediate stores float bits, while the generic IL Move path
        // treats an immediate as an integer value. Leave those stores unresolved
        // until the emitter has a proved bit-preserving conversion.
        return NarrowFieldEqualityProof.HasUnchangedFieldLayout(reference, width);
    }

    private static bool IsTailTo(NativeInstruction instruction, ulong target) =>
        instruction.Code == Code.Jmp_rel32_64 &&
        instruction.FlowControl == FlowControl.UnconditionalBranch &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;
}
