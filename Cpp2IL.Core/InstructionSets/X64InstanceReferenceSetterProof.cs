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
/// Proves a frame-free instance property setter that stores its sole reference
/// argument and tail-transfers to the installed GC card marker. The native body
/// may be folded with other setters; each application method is bound to its
/// own unchanged property and field metadata before emitting managed IL.
/// </summary>
internal static class X64InstanceReferenceSetterProof
{
    internal sealed record Evidence(FieldAnalysisContext Field);
    internal sealed record Shape(int FieldOffset, ulong BarrierTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            !PossibleSetter(method))
            return null;
        method.EnsureRawBytes();
        return Find(method, X86Utils.Iterate(method).ToArray());
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !PossibleSetter(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;

        method.EnsureRawBytes();
        if (method.RawBytes.Length < 12 || decoded.Count < 3 ||
            !decoded.SequenceEqual(X86Utils.Iterate(method)) ||
            !TrySelectReachableLeaf(method, decoded, pe, unwind, out var leaf) ||
            TryProveShape(leaf) is not { } shape ||
            !FileBackedExecutablePrefix(method, pe, unwind, 12) ||
            X86CallerExceptionRegionProof.Check(method, leaf, new HashSet<ulong>()) != null ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.BarrierTarget) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
            bindings.Count > 64 ||
            Enumerable.Range(1, 11).Any(offset =>
                app.MethodsByAddress.ContainsKey(method.UnderlyingPointer + (ulong)offset)))
            return null;

        var evidence = BindMetadata(method, shape.FieldOffset);
        if (evidence == null)
            return null;

        // Reference-assembly methods can fold to the same machine code. They do
        // not supply this application's field identity. Require every alias in
        // the selected assembly to bind independently to this exact store.
        return bindings.Where(candidate => ReferenceEquals(
                candidate.DeclaringType?.DeclaringAssembly,
                method.DeclaringType?.DeclaringAssembly))
            .All(candidate => BindMetadata(candidate, shape.FieldOffset) != null)
            ? evidence : null;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 3 || body[0].Length != 4 || body[1].Length != 3 ||
            body[2].Length != 5)
            return null;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }
        if (body[0].Code != Code.Add_rm64_imm8 ||
            body[0].Op0Kind != OpKind.Register || body[0].Op0Register != Register.RCX ||
            body[0].Op1Kind != OpKind.Immediate8to64 ||
            body[0].GetImmediate(1) is < 16 or > 127 ||
            body[1].Code != Code.Mov_rm64_r64 ||
            body[1].Op0Kind != OpKind.Memory || body[1].MemoryBase != Register.RCX ||
            body[1].MemoryIndex != Register.None || body[1].MemoryDisplacement64 != 0 ||
            body[1].MemorySize.GetSize() != 8 ||
            body[1].Op1Kind != OpKind.Register || body[1].Op1Register != Register.RDX ||
            body[2].Code != Code.Jmp_rel32_64 ||
            body[2].Op0Kind != OpKind.NearBranch64 || body[2].NearBranchTarget == 0)
            return null;
        return new Shape((int)body[0].GetImmediate(1), body[2].NearBranchTarget);
    }

    private static bool TrySelectReachableLeaf(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded, PE pe, X64UnwindProof.Index unwind,
        out IReadOnlyList<NativeInstruction> leaf)
    {
        leaf = Array.Empty<NativeInstruction>();
        var start = method.UnderlyingPointer;
        if (decoded[0].IP != start || decoded[2].NextIP - start != 12 ||
            unwind.ClassifySpan(start, decoded[2].NextIP).Kind !=
                X64UnwindProof.SpanKind.NoEntry)
            return false;

        if (method.RawBytes.Length == 12 && decoded.Count == 3)
        {
            leaf = decoded;
            return true;
        }

        // A method-pointer size estimate can include the next, unrelated
        // function. Trap padding and that function's own unwind entry bound
        // the reachable tail-jump leaf without treating later code as ours.
        var index = 3;
        var paddingEnd = decoded[2].NextIP;
        while (index < decoded.Count && decoded[index].Code == Code.Int3)
        {
            if (decoded[index].IP != paddingEnd || decoded[index].Length != 1 ||
                paddingEnd - decoded[2].NextIP >= 15)
                return false;
            paddingEnd = decoded[index].NextIP;
            index++;
        }
        if (index == 3 || index == decoded.Count || decoded[index].IP != paddingEnd ||
            decoded[index].IsInvalid || decoded[index].NextIP <= paddingEnd ||
            paddingEnd - start >= (ulong)method.RawBytes.Length ||
            !FileBackedExecutablePrefix(method, pe, unwind,
                checked((int)(paddingEnd - start))) ||
            !X64NativePaddingProof.HasInt3Padding(pe, decoded[2].NextIP, paddingEnd) ||
            unwind.ClassifySpan(start, paddingEnd).Kind != X64UnwindProof.SpanKind.NoEntry ||
            unwind.ClassifySpan(paddingEnd, decoded[index].NextIP) is not
                { Kind: X64UnwindProof.SpanKind.HandlerFree,
                    Start: var nextStart, RootStart: var nextRoot } ||
            nextStart != paddingEnd || nextRoot != paddingEnd ||
            Enumerable.Range(1, checked((int)(paddingEnd - start - 1))).Any(offset =>
                method.AppContext.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return false;

        leaf = [decoded[0], decoded[1], decoded[2]];
        return true;
    }

    private static Evidence? BindMetadata(MethodAnalysisContext method, int offset)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false, PackingSizeIsDefault: true,
                ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.DeclaringType != null ||
            owner.Attributes != owner.DefaultAttributes ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var rawValue] ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.GenericParameters.Count != 0 || method.Parameters is not [var value] ||
            method.Name != method.DefaultName ||
            !method.Name.StartsWith("set_", StringComparison.Ordinal) ||
            (method.Attributes & MethodAttributes.SpecialName) == 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemVoidType) ||
            !ReferenceEquals(value.Definition, rawValue) ||
            !ReferenceEquals(value.DeclaringMethod, method) ||
            value.ParameterIndex != 0 || value.IsRef ||
            value.Attributes != value.DefaultAttributes ||
            value.OverrideParameterType != null ||
            rawValue.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawType ||
            rawType.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                                Il2CppTypeEnum.IL2CPP_TYPE_STRING))
            return null;

        var valueType = rawType.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT
            ? app.SystemTypes.SystemObjectType : app.SystemTypes.SystemStringType;
        if (!ReferenceEquals(value.ParameterType, valueType) ||
            offset + app.Binary.PointerSizeBytes > owner.Definition.RawSizes.instance_size)
            return null;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Setter, method)).ToArray();
        // A matching getter is recovered separately; its presence does not
        // change this setter's native store or field binding.
        if (properties is not [{ } property] ||
            property.Definition is not { } rawProperty ||
            !ReferenceEquals(rawProperty.Setter, definition) ||
            property.Name != property.DefaultName ||
            method.Name != "set_" + property.Name ||
            property.Attributes != property.DefaultAttributes ||
            property.OverridePropertyType != null ||
            property.IsStatic || !ReferenceEquals(property.PropertyType, valueType) ||
            rawProperty.RawPropertyType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawPropertyType ||
            rawPropertyType.Type != rawType.Type)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic &&
            field.Offset == offset && ReferenceEquals(field.FieldType, valueType) &&
            field.BackingData?.Field.RawFieldType is
                { NumMods: 0, Byref: 0, Pinned: 0 } rawField &&
            rawField.Type == rawType.Type).ToArray();
        if (fields is not [{ } stored] ||
            !ReferenceEquals(stored.DeclaringType, owner) ||
            stored.Name != stored.DefaultName ||
            stored.Attributes != stored.DefaultAttributes ||
            stored.OverrideFieldType != null || stored.Offset != stored.DefaultOffset ||
            stored.UseOverrideConstantValue ||
            stored.StaticArrayInitialValue.Length != 0 ||
            (stored.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal |
                                  FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal |
                                  FieldAttributes.HasFieldRVA)) != 0 ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new ISIL.FieldReference(stored,
                    new ISIL.LocalVariable("proved-owner", new ISIL.Register(null, "rcx"),
                        owner), offset)))
            return null;
        return new Evidence(stored);
    }

    private static bool PossibleSetter(MethodAnalysisContext method) =>
        method.Name.StartsWith("set_", StringComparison.Ordinal) &&
        !method.IsStatic && !method.IsVirtual && method.Parameters.Count == 1 &&
        (method.Attributes & MethodAttributes.SpecialName) != 0 &&
        method.Definition is { parameterCount: 1,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        definition.InternalParameterData is [{ RawType:
            { NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                    Il2CppTypeEnum.IL2CPP_TYPE_STRING } }] &&
        method.UnderlyingPointer is not (0 or ulong.MaxValue);

    private static bool FileBackedExecutablePrefix(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, int length)
    {
        var start = method.UnderlyingPointer;
        if (length < 12 || method.RawBytes.Length < length ||
            start < unwind.ImageBase || start - unwind.ImageBase > uint.MaxValue - (uint)length + 1)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(start + (ulong)length - 1, false);
        var raw = pe.GetRawBinaryContent();
        return first >= 0 && last == first + length - 1 &&
               first <= raw.Length - length &&
               method.RawBytes.AsSpan().Slice(0, length)
                   .SequenceEqual(raw.Slice((int)first, length)) &&
               Enumerable.Range(0, length).All(offset =>
                   unwind.IsExecutableRva(checked((uint)(start + (ulong)offset -
                       unwind.ImageBase))) &&
                   pe.MapVirtualAddressToRaw(start + (ulong)offset, false) ==
                       first + offset);
    }
}
