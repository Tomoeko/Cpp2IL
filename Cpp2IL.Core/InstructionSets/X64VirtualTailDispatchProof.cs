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
/// Recovers the complete frame-free virtual tail with an unchanged receiver and
/// a MethodInfo pointer paired with the same vtable slot as the jump target.
/// The exact slot must name one zero-argument virtual method on the caller's
/// declaring class; arbitrary indirect jumps remain unsupported.
/// </summary>
internal static class X64VirtualTailDispatchProof
{
    internal readonly record struct Shape(ulong MethodPointerOffset, ulong End);
    internal sealed record Evidence(MethodAnalysisContext Target, int Slot);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        method.EnsureRawBytes();
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            !EligibleCaller(method) || TryProveShape(native) is not { } shape ||
            method.UnderlyingPointer == 0 || native[0].IP != method.UnderlyingPointer ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            !ClosedLeaf(method, native, shape.End, 17, pe, unwind))
            return null;

        if (!TryResolveOwnerSlot(method, shape.MethodPointerOffset,
                out var owner, out var target, out var slot) ||
            !EligibleTarget(target, owner, slot))
            return null;
        return new Evidence(target, slot);
    }

    internal static bool TryResolveOwnerSlot(MethodAnalysisContext method,
        ulong offset, out TypeAnalysisContext owner, out MethodAnalysisContext target,
        out int slot)
    {
        owner = null!;
        target = null!;
        slot = 0;
        var app = method.AppContext;
        if (offset < (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset ||
            (offset - (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset) %
                (ulong)Il2CppVirtualInvokeLayout.EntrySize(8) != 0)
            return false;
        var slotValue = (offset - (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset) /
            (ulong)Il2CppVirtualInvokeLayout.EntrySize(8);
        if (slotValue > int.MaxValue ||
            method.DeclaringType is not { Definition: { } ownerDefinition } resolvedOwner ||
            slotValue >= (ulong)ownerDefinition.VTable.Length ||
            !EligibleOwnerDispatchLayout(resolvedOwner, (int)slotValue) ||
            ownerDefinition.VTable[(int)slotValue] is not
                { Type: MetadataUsageType.MethodDef } entry ||
            app.ResolveContextForMethod(entry) is not { } resolvedTarget ||
            !ReferenceEquals(entry.AsMethod(), resolvedTarget.Definition))
            return false;
        owner = resolvedOwner;
        target = resolvedTarget;
        slot = (int)slotValue;
        return true;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 3 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body[0].NextIP != body[1].IP || body[1].NextIP != body[2].IP ||
            body[2].MemoryDisplacement64 > ulong.MaxValue - 8 ||
            !PointerLoad(body[0], NativeRegister.RAX, NativeRegister.RCX, 0) ||
            !PointerLoad(body[1], NativeRegister.RDX, NativeRegister.RAX,
                body[2].MemoryDisplacement64 + 8) ||
            body[2].Mnemonic != Mnemonic.Jmp ||
            body[2].FlowControl != FlowControl.IndirectBranch ||
            body[2].OpCount != 1 || body[2].Op0Kind != OpKind.Memory ||
            body[2].MemoryBase != NativeRegister.RAX ||
            body[2].MemoryIndex != NativeRegister.None ||
            body[2].MemorySize.GetSize() != 8)
            return null;
        return new Shape(body[2].MemoryDisplacement64, body[2].NextIP);
    }

    internal static bool EligibleCaller(MethodAnalysisContext method)
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
               method.Name == method.DefaultName && method.Parameters.Count == 0 &&
               method.GenericParameters.Count == 0 && method.OverrideReturnType == null &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
               ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType);
    }

    private static bool EligibleOwnerDispatchLayout(TypeAnalysisContext owner,
        int selectedSlot)
    {
        try
        {
            var app = owner.AppContext;
            var definition = owner.Definition!;
            var offsets = definition.InterfaceOffsets;
            // An override shares a slot with an ancestor declaration. The
            // vtable entry alone cannot identify which declaration appeared at
            // the original managed callsite.
            if (owner.BaseType is not { Definition: { } baseDefinition } ||
                selectedSlot < baseDefinition.VTable.Length)
                return false;
            if (ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
                offsets.Length == 0 && definition.InterfaceOffsetsCount == 0 &&
                definition.InterfacesCount == 0 && definition.RawInterfaces.Length == 0 &&
                owner.InterfaceContexts.Count == 0)
                return true;

            // Inherited interface offsets can be remapped in a derived vtable.
            // Admit only interface-free ancestors, then exclude every interval
            // introduced directly by this owner from the selected class slot.
            if (X64ScalarVirtualDispatchProof.Hierarchy(owner) is not { } hierarchy ||
                hierarchy.Skip(1).Any(type => type.Definition is not { } ancestor ||
                    ancestor.InterfacesCount != 0 || ancestor.InterfaceOffsetsCount != 0 ||
                    ancestor.RawInterfaces.Length != 0 || ancestor.InterfaceOffsets.Length != 0 ||
                    type.InterfaceContexts.Count != 0))
                return false;
            var rawInterfaces = definition.RawInterfaces;
            var interfaces = owner.InterfaceContexts;
            if (rawInterfaces.Length != offsets.Length ||
                rawInterfaces.Length != interfaces.Count ||
                rawInterfaces.Length != definition.InterfacesCount ||
                offsets.Length != definition.InterfaceOffsetsCount ||
                rawInterfaces.Length > 8)
                return false;

            var seen = new HashSet<TypeAnalysisContext>();
            for (var index = 0; index < offsets.Length; index++)
            {
                var raw = rawInterfaces[index];
                var offset = offsets[index];
                if (raw is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                    offset.Type is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } offsetType ||
                    interfaces[index] is not { } contract ||
                    !ReferenceEquals(contract, app.ResolveIl2CppType(raw)) ||
                    !ReferenceEquals(contract, app.ResolveIl2CppType(offsetType)) ||
                    !seen.Add(contract) || !contract.IsInterface ||
                    contract.IsGenericInstance || contract.GenericParameters.Count != 0 ||
                    contract.Name != contract.DefaultName ||
                    contract.Namespace != contract.DefaultNamespace ||
                    contract.Attributes != contract.DefaultAttributes ||
                    contract.Definition is not { GenericContainer: null,
                        RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                            NumMods: 0, Byref: 0, Pinned: 0 },
                        RawInterfaces.Length: 0 } contractDefinition ||
                    contract.Methods.Count is < 1 or > 64 ||
                    contract.Methods.Count != contractDefinition.MethodCount ||
                    contract.Methods.Any(method => method.IsStatic || !method.IsVirtual ||
                        !ReferenceEquals(method.DeclaringType, contract) ||
                        method.Definition is not { } definition ||
                        !ReferenceEquals(definition.DeclaringType, contractDefinition)) ||
                    !contract.Methods.Select(method => (int)method.Definition!.slot)
                        .OrderBy(slot => slot).SequenceEqual(Enumerable.Range(0, contract.Methods.Count)) ||
                    offset.offset < 0 ||
                    (long)offset.offset + contract.Methods.Count >
                        definition.VTable.Length ||
                    selectedSlot >= offset.offset &&
                    selectedSlot < (long)offset.offset + contract.Methods.Count)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool EligibleTarget(MethodAnalysisContext target,
        TypeAnalysisContext owner, int slot)
    {
        var app = target.AppContext;
        return ReferenceEquals(target.DeclaringType, owner) &&
               target.Definition is { GenericContainer: null, parameterCount: 0,
                   RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                       NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               ReferenceEquals(definition.DeclaringType, owner.Definition) &&
               definition.slot == slot &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               target.IsVirtual && !target.IsStatic && !target.IsFinal &&
               (target.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               target.Name == target.DefaultName && target.Parameters.Count == 0 &&
               target.GenericParameters.Count == 0 && target.OverrideReturnType == null &&
               target.Attributes == target.DefaultAttributes &&
               target.ImplAttributes == target.DefaultImplAttributes &&
               (target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(target) &&
               ReferenceEquals(target.ReturnType, app.SystemTypes.SystemVoidType) &&
               RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target);
    }

    internal static bool ClosedLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native, ulong end, int expectedLength, PE pe,
        X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        if (expectedLength is not (17 or 19) || start % 16 != 0 ||
            end <= start || end - start != (ulong)expectedLength ||
            end > ulong.MaxValue - 15 ||
            method.RawBytes.Length != expectedLength ||
            !X86Utils.Iterate(method).SequenceEqual(native) ||
            X86CallerExceptionRegionProof.CheckProvedTerminalIndirectBranch(
                method, native, native[^1].IP) != null)
            return false;
        var alignedEnd = (end + 15) & ~15UL;
        if (alignedEnd - start != 32 ||
            method.AppContext.GetAddressOfNextFunctionStart(start) != alignedEnd ||
            unwind.ClassifySpan(start, alignedEnd) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry } ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, alignedEnd) ||
            start < unwind.ImageBase || alignedEnd - unwind.ImageBase > uint.MaxValue ||
            Enumerable.Range(1, 31).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return false;

        var rawStart = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        return rawStart >= 0 && rawStart <= image.Length - 32 &&
               Enumerable.Range(0, 32).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start + (ulong)offset - unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) == rawStart + offset) &&
               image.Slice((int)rawStart, expectedLength)
                   .SequenceEqual(method.RawBytes.AsSpan());
    }

    private static bool PointerLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.FlowControl == FlowControl.Next &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == source && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == 8;
}
