using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete frame-free null-guarded invocation of one captured,
/// zero-argument void delegate field. The native delegate layout and Win64
/// argument registers are specific to Unity 2021.3.35f1 Windows x64 IL2CPP.
/// </summary>
internal static class X64NullableDelegateFieldTailProof
{
    // In this target's Il2CppDelegate: Il2CppObject occupies two pointers,
    // followed by method_ptr, invoke_impl, target, method, two more pointers,
    // and invoke_impl_this. The native body must use all three linked slots.
    private const ulong InvokeImplOffset = 3 * 8;
    private const ulong MethodInfoOffset = 5 * 8;
    private const ulong InvokeThisOffset = 8 * 8;

    internal sealed record Evidence(FieldAnalysisContext Field, MethodAnalysisContext Invoke);
    internal readonly record struct Shape(int FieldOffset, ulong End);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        try
        {
            method.EnsureRawBytes();
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !EligibleCaller(method) ||
                TryProveShape(native) is not { } shape ||
                method.UnderlyingPointer == 0 || native[0].IP != method.UnderlyingPointer ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                !ClosedLeaf(method, native, shape.End, pe, unwind))
                return null;

            var owner = method.DeclaringType!;
            var candidates = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (candidates is not [{ } field] ||
                field.Name != field.DefaultName ||
                field.Attributes != field.DefaultAttributes ||
                field.OverrideFieldType != null ||
                !ReferenceEquals(field.FieldType, field.DefaultFieldType) ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } rawField ||
                !ReferenceEquals(app.ResolveIl2CppType(rawField), field.FieldType) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(field,
                        new LocalVariable("proved-owner",
                            new ManagedRegister(null, "rcx"), owner),
                        shape.FieldOffset)) ||
                BindInvoke(app, field.FieldType) is not { } invoke)
                return null;

            return new Evidence(field, invoke);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 7 || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !PointerLoad(body[0], NativeRegister.RAX, NativeRegister.RCX,
                body[0].MemoryDisplacement64) ||
            body[0].MemoryDisplacement64 is < 16 or > 0xFF8 ||
            body[0].Length is not (4 or 7) ||
            !Registers(body[1], Code.Test_rm64_r64,
                NativeRegister.RAX, NativeRegister.RAX) ||
            body[2].Code != Code.Je_rel8_64 ||
            body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[6].IP ||
            !PointerLoad(body[3], NativeRegister.RDX,
                NativeRegister.RAX, MethodInfoOffset) ||
            !PointerLoad(body[4], NativeRegister.RCX,
                NativeRegister.RAX, InvokeThisOffset) ||
            body[5].Code != Code.Jmp_rm64 ||
            body[5].FlowControl != FlowControl.IndirectBranch ||
            body[5].OpCount != 1 || body[5].Op0Kind != OpKind.Memory ||
            body[5].MemoryBase != NativeRegister.RAX ||
            body[5].MemoryIndex != NativeRegister.None ||
            body[5].MemoryDisplacement64 != InvokeImplOffset ||
            body[5].MemorySize.GetSize() != 8 ||
            body[6].Code != Code.Retnq || body[6].OpCount != 0 ||
            body[6].FlowControl != FlowControl.Return)
            return null;

        return new Shape(checked((int)body[0].MemoryDisplacement64),
            body[6].NextIP);
    }

    private static bool EligibleCaller(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.DeclaringType is { } owner &&
               X64ClassCastLookupProof.PublicOrdinaryClass(owner) &&
               method.Definition is { GenericContainer: null, parameterCount: 0,
                   RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                       NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               ReferenceEquals(definition.DeclaringType, owner.Definition) &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               !method.IsStatic && !method.IsVirtual && method.IsVoid &&
               method.Name is not (".ctor" or ".cctor") &&
               method.Name == method.DefaultName &&
               method.Parameters.Count == 0 &&
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
               ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType);
    }

    private static MethodAnalysisContext? BindInvoke(
        ApplicationAnalysisContext app, TypeAnalysisContext delegateType)
    {
        var multicast = app.GetAssemblyByName("mscorlib")?
            .GetTypeByFullName("System.MulticastDelegate");
        if (multicast == null || !delegateType.IsDelegate ||
            delegateType.IsValueType || delegateType.IsInterface ||
            delegateType.IsGenericInstance ||
            delegateType.GenericParameters.Count != 0 ||
            delegateType.Definition is not { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } rawDelegate ||
            !ReferenceEquals(delegateType.BaseType, multicast) ||
            !ReferenceEquals(delegateType.BaseType, delegateType.DefaultBaseType) ||
            delegateType.Name != delegateType.DefaultName ||
            delegateType.Namespace != delegateType.DefaultNamespace ||
            delegateType.Attributes != delegateType.DefaultAttributes)
            return null;

        var candidates = delegateType.Methods.Where(candidate =>
            candidate.Name == "Invoke").ToArray();
        if (candidates is not [var invoke] ||
            invoke.Definition is not { GenericContainer: null,
                parameterCount: 0, RawReturnType: {
                    Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, rawDelegate) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(invoke.DeclaringType, delegateType) ||
            invoke.IsStatic || !invoke.IsVirtual || !invoke.IsVoid ||
            invoke.Name != invoke.DefaultName ||
            invoke.Parameters.Count != 0 ||
            invoke.GenericParameters.Count != 0 ||
            invoke.OverrideReturnType != null ||
            invoke.Attributes != invoke.DefaultAttributes ||
            invoke.ImplAttributes != invoke.DefaultImplAttributes ||
            (invoke.Attributes & (MethodAttributes.Abstract |
                MethodAttributes.PinvokeImpl)) != 0 ||
            (invoke.ImplAttributes & MethodImplAttributes.CodeTypeMask) !=
                MethodImplAttributes.Runtime ||
            (invoke.ImplAttributes & MethodImplAttributes.ManagedMask) !=
                MethodImplAttributes.Managed ||
            !ReferenceEquals(invoke.ReturnType, app.SystemTypes.SystemVoidType))
            return null;
        return invoke;
    }

    private static bool ClosedLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native, ulong end, PE pe,
        X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        var size = end - start;
        if (start % 16 != 0 || end <= start || size is not (22 or 25) ||
            method.RawBytes.Length != (int)size ||
            end > ulong.MaxValue - 15 ||
            !X86Utils.Iterate(method).SequenceEqual(native) ||
            X86CallerExceptionRegionProof.CheckProvedGuardedTerminalIndirectBranch(
                method, native, native[5].IP, native[6].IP) != null)
            return false;

        var alignedEnd = (end + 15) & ~15UL;
        if (alignedEnd - start != 32 ||
            method.AppContext.GetAddressOfNextFunctionStart(start) != alignedEnd ||
            unwind.ClassifySpan(start, alignedEnd) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry } ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, alignedEnd) ||
            start < unwind.ImageBase ||
            alignedEnd - unwind.ImageBase > uint.MaxValue ||
            Enumerable.Range(1, 31).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(
                    start + (ulong)offset)))
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        return rawStart >= 0 && rawStart <= image.Length - 32 &&
               Enumerable.Range(0, 32).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start +
                       (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                       rawStart + offset) &&
               image.Slice((int)rawStart, (int)size)
                   .SequenceEqual(method.RawBytes.AsSpan());
    }

    private static bool PointerLoad(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.FlowControl == FlowControl.Next &&
        instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == source &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code &&
        instruction.FlowControl == FlowControl.Next &&
        instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;
}
