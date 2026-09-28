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
/// Recovers one literal Int32 field initializer followed by an inert Object
/// constructor tail. The caller may share its native entry with other managed
/// constructors, but its own metadata must bind the exact stored field.
/// </summary>
internal static class X64FoldedLiteralConstructorProof
{
    internal readonly record struct Shape(long FieldOffset, int Value, ulong Target, ulong End);
    internal sealed record Evidence(FieldAnalysisContext Field,
        MethodAnalysisContext BaseConstructor, int Value);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } evidence)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        return
        [
            new(0, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, evidence.Field.Offset),
                new ISIL.Immediate(evidence.Value)),
            new(1, ISIL.OpCode.CallVoid, evidence.BaseConstructor, receiver),
            new(2, ISIL.OpCode.Return),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { } owner ||
                !EligibleMethod(method, owner, app))
                return null;

            method.EnsureRawBytes();
            if (TryProveShape(decoded) is not { } shape ||
                decoded[0].IP != method.UnderlyingPointer ||
                shape.End <= method.UnderlyingPointer ||
                shape.End - method.UnderlyingPointer != 14 ||
                !ClosedLeaf(method, decoded, shape.End, pe, unwind))
                return null;

            var candidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (candidates is not [{ } state] ||
                !ReferenceEquals(state.DeclaringType, owner) ||
                !ReferenceEquals(state.FieldType, app.SystemTypes.SystemInt32Type) ||
                state.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                state.Name != state.DefaultName ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                    new ISIL.FieldReference(state,
                        new ISIL.LocalVariable("literal-constructor-receiver",
                            new ISIL.Register(null, "rcx"), owner),
                        state.Offset), 32))
                return null;

            var objectConstructors = app.SystemTypes.SystemObjectType.Methods
                .Where(candidate => candidate.Name == ".ctor" && !candidate.IsStatic &&
                    candidate.Parameters.Count == 0 && candidate.IsVoid &&
                    candidate.UnderlyingPointer == shape.Target).ToArray();
            if (objectConstructors is not [{ } objectConstructor] ||
                !X64IteratorFactoryProof.ProveInertObjectConstructor(
                    app, shape.Target, pe, unwind))
                return null;

            return new Evidence(state, objectConstructor, shape.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                      InvalidOperationException or IndexOutOfRangeException or
                                      OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count < 3 || body.Take(3).Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body[0].NextIP != body[1].IP || body[1].NextIP != body[2].IP ||
            body[0].Length != 2 || body[1].Length != 7 || body[2].Length != 5 ||
            body[0].Mnemonic != Mnemonic.Xor || body[0].FlowControl != FlowControl.Next ||
            body[0].OpCount != 2 || body[0].Op0Kind != OpKind.Register ||
            body[0].Op1Kind != OpKind.Register ||
            body[0].Op0Register != NativeRegister.EDX ||
            body[0].Op1Register != NativeRegister.EDX ||
            body[1].Code != Code.Mov_rm32_imm32 ||
            body[1].FlowControl != FlowControl.Next || body[1].OpCount != 2 ||
            body[1].Op0Kind != OpKind.Memory || body[1].Op1Kind != OpKind.Immediate32 ||
            body[1].MemoryBase != NativeRegister.RCX ||
            body[1].MemoryIndex != NativeRegister.None ||
            body[1].MemorySize.GetSize() != 4 ||
            body[1].MemoryDisplacement64 is < 16 or > int.MaxValue ||
            body[2].Code != Code.Jmp_rel32_64 ||
            body[2].FlowControl != FlowControl.UnconditionalBranch ||
            body[2].OpCount != 1 || body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget == 0)
            return null;
        return new Shape((long)body[1].MemoryDisplacement64,
            unchecked((int)body[1].Immediate32), body[2].NearBranchTarget,
            body[2].NextIP);
    }

    private static bool EligibleMethod(MethodAnalysisContext method,
        TypeAnalysisContext owner, ApplicationAnalysisContext app)
    {
        if (method.Name != ".ctor" || method.Name != method.DefaultName ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) !=
                (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var aliases) ||
            aliases.Count(candidate => ReferenceEquals(candidate, method)) != 1)
            return false;

        return !owner.IsValueType && !owner.IsInterface && !owner.IsGenericInstance &&
               owner.GenericParameters.Count == 0 &&
               owner.Name == owner.DefaultName &&
               owner.Namespace == owner.DefaultNamespace &&
               owner.Attributes == owner.DefaultAttributes &&
               ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
               ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
               owner.InterfaceContexts.Count == 0 &&
               owner.Definition is { GenericContainer: null, HasCctor: false,
                   PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               (owner.Attributes & TypeAttributes.LayoutMask) !=
                   TypeAttributes.ExplicitLayout &&
               owner.Methods.Count(candidate => candidate.Name == ".ctor") == 1 &&
               owner.Methods.All(candidate => candidate.Name != ".cctor");
    }

    private static bool ClosedLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded, ulong end, PE pe,
        X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        if (start % 16 != 0 || start > ulong.MaxValue - 16 ||
            end != start + 14 || method.RawBytes.Length < 14 ||
            decoded.Count < 3 ||
            !decoded.SequenceEqual(X86Utils.Iterate(method)) ||
            end <= start ||
            X86CallerExceptionRegionProof.Check(method,
                decoded.Take(3).ToArray(), new HashSet<ulong>()) != null)
            return false;

        var next = start + 16;
        if (unwind.ClassifySpan(start, next).Kind !=
                X64UnwindProof.SpanKind.NoEntry ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, next) ||
            Enumerable.Range(1, 15).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            start < unwind.ImageBase || next - unwind.ImageBase > uint.MaxValue)
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawStart > image.Length - 16 ||
            !image.Slice(checked((int)rawStart), 14)
                .SequenceEqual(method.RawBytes.AsSpan().Slice(0, 14)) ||
            Enumerable.Range(0, 16).Any(offset =>
                !unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                    unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(start + (ulong)offset, false) !=
                    rawStart + offset))
            return false;

        if (method.RawBytes.Length == 14)
            return decoded.Count == 3 &&
                   method.AppContext.GetAddressOfNextFunctionStart(start) == next &&
                   method.AppContext.MethodsByAddress.ContainsKey(next);

        if (method.RawBytes.Length <= 16 || decoded.Count < 6 ||
            decoded[3].IP != end || decoded[3].Code != Code.Int3 ||
            decoded[4].IP != end + 1 || decoded[4].Code != Code.Int3 ||
            decoded[5].IP != next || decoded[5].IsInvalid ||
            decoded[5].NextIP <= next ||
            decoded[5].NextIP - start > (ulong)method.RawBytes.Length)
            return false;
        return unwind.HasFunctionEntryAt(next, decoded[5].NextIP);
    }
}
