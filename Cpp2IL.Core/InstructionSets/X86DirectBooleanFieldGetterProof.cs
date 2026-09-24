using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete frame-free Boolean getter whose native result is one byte
/// from an instance field of this method's own declaring class. A shared native
/// address is checked against each managed method's metadata independently.
/// </summary>
internal static class X86DirectBooleanFieldGetterProof
{
    internal sealed record Shape(int FieldOffset, ulong End);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Instruction> body)
    {
        if (Find(method, body) is not { } field)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        var result = new ISIL.Register(null, "direct_boolean_field_result");
        return
        [
            new(0, ISIL.OpCode.Move, result,
                new ISIL.MemoryOperand(receiver, null, field.Offset)) { IntegerBitWidth = 8 },
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    internal static FieldAnalysisContext? Find(MethodAnalysisContext method,
        IReadOnlyList<Instruction> body)
    {
        var shape = TryProveShape(body);
        var hasUnrelatedSuffix = false;
        if (shape == null && method.IsVirtual && body.Count > 3)
        {
            shape = TryProveShape(body.Take(2).ToArray());
            hasUnrelatedSuffix = shape != null;
        }
        if (shape == null || !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.Name != owner.DefaultName || owner.OverrideNamespace != null ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            owner.Definition is not { HasCctor: false, PackingSizeIsDefault: true,
                ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } ||
            owner.Methods.Any(candidate => candidate.Name == ".cctor") ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.IsStatic || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !HasUnchangedVirtualDispatch(method, owner, definition) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer)
            return null;

        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        if (shape.End < start ||
            (hasUnrelatedSuffix
                ? !HasProvedUnrelatedSuffix(pe, method, body, shape.End, unwind)
                : shape.End - start != (ulong)method.RawBytes.Length ||
                  !HasFileBackedBody(pe, method, shape.End)) ||
            Enumerable.Range(1, checked((int)(shape.End - start) - 1)).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)) ||
            unwind.ClassifySpan(start, shape.End) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var regionStart, End: var regionEnd } ||
            regionStart != start || regionEnd != shape.End ||
            X86CallerExceptionRegionProof.Check(method,
                hasUnrelatedSuffix ? body.Take(2).ToArray() : body, new HashSet<ulong>()) != null)
            return null;

        // The metadata resolver uses one field at a native offset. Require the
        // same unique field independently for this binding of a shared body.
        var candidates = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset).ToArray();
        if (candidates is not [{ } matched] || matched.Name != matched.DefaultName ||
            matched.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(matched.FieldType,
                method.AppContext.SystemTypes.SystemBooleanType))
            return null;
        var receiver = new ISIL.LocalVariable("proved-boolean-owner",
            new ISIL.Register(null, "rcx"), owner);
        var access = new ISIL.FieldReference(matched, receiver, shape.FieldOffset);
        return (NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(access) ||
                NarrowFieldEqualityProof.HasUnchangedByteFieldLayoutWithFieldlessConstructedBase(access))
            ? matched : null;
    }

    private static bool HasUnchangedVirtualDispatch(MethodAnalysisContext method,
        TypeAnalysisContext owner, Il2CppMethodDefinition definition)
    {
        if (!method.IsVirtual)
            return true;
        var slot = definition.slot;
        var vtable = owner.Definition?.VTable;
        if (method.IsFinal || slot == ushort.MaxValue || vtable == null ||
            slot >= vtable.Length ||
            vtable[slot] is not { Type: MetadataUsageType.MethodDef } entry ||
            entry.AsMethod() != definition ||
            owner.Definition!.InterfaceOffsets.Length != 0)
            return false;
        var baseMethod = method.BaseMethod;
        return method.IsNewSlot
            ? baseMethod == null
            : baseMethod is { IsVirtual: true, IsFinal: false, Definition: { } baseDefinition } &&
              baseDefinition.slot == slot;
    }

    private static bool HasProvedUnrelatedSuffix(PE pe, MethodAnalysisContext method,
        IReadOnlyList<Instruction> body, ulong entryEnd, X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        var fullEnd = body[^1].NextIP;
        if (body[0].IP != start || body[1].NextIP != entryEnd ||
            fullEnd <= entryEnd || fullEnd - start != (ulong)method.RawBytes.Length ||
            !HasFileBackedBody(pe, method, fullEnd))
            return false;

        // A RET closes the reachable entry. Admit only alignment INT3 bytes before
        // a distinct, fully registered handler-free native function. The model's
        // raw span can run through this adjacent unmanaged body without another
        // managed entry inside the span; its instructions do not belong to the getter.
        var nextIndex = 2;
        while (nextIndex < body.Count && body[nextIndex].Code == Code.Int3)
        {
            if (body[nextIndex].IP != entryEnd + (ulong)(nextIndex - 2) ||
                body[nextIndex].Length != 1)
                return false;
            nextIndex++;
        }
        var padding = nextIndex - 2;
        if (padding is < 1 or > 15 || nextIndex >= body.Count ||
            body[nextIndex].IP != entryEnd + (ulong)padding ||
            body[nextIndex].IP % 16 != 0 ||
            unwind.ClassifySpan(entryEnd, body[nextIndex].IP) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var gapStart, End: var gapEnd } ||
            gapStart != entryEnd || gapEnd != body[nextIndex].IP ||
            unwind.ClassifySpan(body[nextIndex].IP, fullEnd) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree, Start: var suffixStart,
                  End: var suffixEnd, RootStart: var rootStart } ||
            suffixStart != body[nextIndex].IP || suffixEnd < fullEnd ||
            rootStart != suffixStart)
            return false;
        return !Enumerable.Range(1, checked((int)(fullEnd - start) - 1)).Any(offset =>
            method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset));
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count is not (2 or 3) ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        var loadIndex = body.Count - 2;
        if (loadIndex == 1 && body[0] is not
            { Code: Code.Nopw or Code.Nopd, OpCount: 0, FlowControl: FlowControl.Next })
            return null;
        var load = body[loadIndex];
        var ret = body[^1];
        if (load.Code != Code.Movzx_r32_rm8 || load.OpCount != 2 ||
            load.Op0Kind != OpKind.Register || load.Op0Register != Register.EAX ||
            load.Op1Kind != OpKind.Memory || load.MemoryBase != Register.RCX ||
            load.MemoryIndex != Register.None || load.MemorySize.GetSize() != 1 ||
            load.MemoryDisplacement64 is < 16 or >= 0x1000 ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return null;
        return new Shape((int)load.MemoryDisplacement64, ret.NextIP);
    }

    private static bool HasFileBackedBody(PE pe, MethodAnalysisContext method, ulong end)
    {
        var start = method.UnderlyingPointer;
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var image = pe.GetRawBinaryContent();
        return first >= 0 && last >= first &&
               last - first == (long)(end - start) - 1 && last < image.Length &&
               image.Slice((int)first, (int)(end - start))
                   .SequenceEqual(method.RawBytes.AsSpan());
    }
}
