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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Recovers a shared zero-argument constructor body that calls the Object
/// target directly and writes one inherited Int32 field. The immediate-base
/// constructor must independently be proved inert, and every native alias
/// must have the same unchanged base and field.
/// </summary>
internal static class X64InheritedInt32ConstructorProof
{
    internal sealed record Evidence(MethodAnalysisContext BaseConstructor,
        FieldAnalysisContext Field, int Value);

    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        if (method.Name != ".ctor" ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext))
            return null;
        try
        {
            method.EnsureRawBytes();
            return Find(method, X86Utils.Iterate(method).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !X64AncestorConstructorThunkProof.OrdinaryConstructor(method) ||
                method.DeclaringType is not { } owner ||
                !X64AncestorConstructorThunkProof.OrdinaryOwner(owner) ||
                owner.Fields.Any(field => !field.IsStatic) ||
                owner.Methods.Where(candidate => candidate.Name == ".ctor")
                    .ToArray() is not [var soleConstructor] ||
                !ReferenceEquals(soleConstructor, method) ||
                owner.BaseType is not { } baseType ||
                !X64AncestorConstructorThunkProof.OrdinaryOwner(baseType) ||
                baseType.Methods.Where(candidate => candidate.Name == ".ctor")
                    .ToArray() is not [var baseConstructor] ||
                !X64AncestorConstructorThunkProof.OrdinaryConstructor(baseConstructor) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                method.UnderlyingPointer is 0 or ulong.MaxValue ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var aliases) ||
                aliases.Count < 2 ||
                aliases.Count != new HashSet<MethodAnalysisContext>(aliases).Count ||
                aliases.Any(alias => !X64AncestorConstructorThunkProof
                        .OrdinaryConstructor(alias) ||
                    !X64AncestorConstructorThunkProof.OrdinaryOwner(
                        alias.DeclaringType) ||
                    alias.DeclaringType!.Fields.Any(field => !field.IsStatic) ||
                    !ReferenceEquals(alias.DeclaringType.BaseType, baseType) ||
                    alias.DeclaringType.Methods.Count(candidate =>
                        candidate.Name == ".ctor") != 1 ||
                    alias.UnderlyingPointer != method.UnderlyingPointer ||
                    RuntimeNullGuardCoalescer.HasOutputOptions(alias) ||
                    !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(alias,
                        requireUniqueBinding: false)))
                return null;

            // The immediate base is an ordinary Object child whose complete
            // native body has already been proved to be an inert Object call.
            // A direct call in this constructor to Object cannot by itself
            // establish the managed initializer target.
            baseConstructor.EnsureRawBytes();
            var baseNative = X86Utils.Iterate(baseConstructor).ToArray();
            var objectConstructor = X64ObjectConstructorThunkProof.Find(
                baseConstructor, baseNative);
            if (objectConstructor == null ||
                !ReferenceEquals(objectConstructor.DeclaringType,
                    app.SystemTypes.SystemObjectType))
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            if (start > ulong.MaxValue - 29 || method.RawBytes.Length != 29 ||
                unwind.ClassifySpan(start, start + 29) is not
                    { Kind: X64UnwindProof.SpanKind.HandlerFree,
                        Start: var regionStart, End: var regionEnd,
                        RootStart: var rootStart } ||
                regionStart != start || regionEnd != start + 29 ||
                rootStart != start ||
                !unwind.MatchesUnwind(start, start + 29, 6, 0,
                    SavedRbxFrame) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, method.RawBytes.AsSpan(), start) ||
                Enumerable.Range(1, 28).Any(offset =>
                    app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
                return null;

            var exact = X86Utils.Iterate(method.RawBytes.AsSpan(), start, false);
            if (exact.Count != 9 || decoded.Count != 9 ||
                !decoded.SequenceEqual(exact) ||
                X86CallerExceptionRegionProof.Check(method, decoded,
                    new HashSet<ulong>()) != null)
                return null;

            var offset = decoded[5].MemoryDisplacement64;
            if (offset is < 16 or > int.MaxValue ||
                baseType.Fields.Where(field => !field.IsStatic &&
                    field.Offset == (long)offset).ToArray() is not
                    [{ } field] ||
                !ReferenceEquals(field.DeclaringType, baseType) ||
                !ReferenceEquals(field.FieldType,
                    app.SystemTypes.SystemInt32Type) ||
                field.Attributes != field.DefaultAttributes ||
                (field.Attributes & FieldAttributes.FieldAccessMask) !=
                    FieldAttributes.Public ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(field,
                        new ISIL.LocalVariable("constructor-receiver",
                            new ISIL.Register(null, "rcx"), baseType),
                        field.Offset), 32) ||
                !TryProveBody(decoded, start, objectConstructor.UnderlyingPointer,
                    offset, out var value))
                return null;

            return new Evidence(baseConstructor, field, value);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static bool TryProveBody(IReadOnlyList<NativeInstruction> body,
        ulong start, ulong objectTarget, ulong fieldOffset, out int value)
    {
        value = 0;
        if (body.Count != 9 || objectTarget == 0 ||
            body[0].IP != start || body[^1].NextIP != start + 29 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Iced.Intel.Register.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Push_r64 ||
            body[0].Op0Register != Iced.Intel.Register.RBX ||
            !StackAdjustment(body[1], Code.Sub_rm64_imm8, 0x20) ||
            body[2].Code != Code.Xor_r32_rm32 ||
            body[2].Op0Register != Iced.Intel.Register.EDX ||
            body[2].Op1Register != Iced.Intel.Register.EDX ||
            body[3].Code != Code.Mov_r64_rm64 ||
            body[3].Op0Register != Iced.Intel.Register.RBX ||
            body[3].Op1Register != Iced.Intel.Register.RCX ||
            body[4].Code != Code.Call_rel32_64 ||
            body[4].NearBranchTarget != objectTarget ||
            body[5].Code != Code.Mov_rm32_imm32 ||
            body[5].Op0Kind != OpKind.Memory ||
            body[5].MemoryBase != Iced.Intel.Register.RBX ||
            body[5].MemoryIndex != Iced.Intel.Register.None ||
            body[5].MemoryDisplacement64 != fieldOffset ||
            body[5].MemorySize.GetSize() != 4 ||
            body[5].Op1Kind != OpKind.Immediate32 ||
            !StackAdjustment(body[6], Code.Add_rm64_imm8, 0x20) ||
            body[7].Code != Code.Pop_r64 ||
            body[7].Op0Register != Iced.Intel.Register.RBX ||
            body[8].Code != Code.Retnq || body[8].OpCount != 0)
            return false;
        value = unchecked((int)body[5].Immediate32);
        return true;
    }

    private static bool StackAdjustment(NativeInstruction instruction,
        Code code, ulong amount) =>
        instruction.Code == code &&
        instruction.Op0Register == Iced.Intel.Register.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == amount;
}
