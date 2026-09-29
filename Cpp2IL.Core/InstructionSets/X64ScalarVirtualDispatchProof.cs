using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves a complete scalar virtual invocation, optionally followed by one result store.</summary>
internal static class X64ScalarVirtualDispatchProof
{
    internal const string EvidenceKey = "X64ScalarVirtualDispatchProof";
    internal const string ResultRegister = "scalar_virtual_dispatch_result";
    internal sealed record Shape(NativeInstruction Invoke, NativeInstruction Exit,
        NativeInstruction NullCall, NativeInstruction? Store, ulong SlotOffset, bool IsTail);
    internal sealed class InputState(List<object> values, byte[] bytes, byte[] cachedBytes)
    {
        private readonly object[] _values = values.ToArray();
        private readonly byte[] _bytes = bytes;
        private readonly byte[] _cachedBytes = cachedBytes;
        internal bool Matches(InputState other) => _values.SequenceEqual(other._values) && _bytes.SequenceEqual(other._bytes) &&
            _cachedBytes.SequenceEqual(other._cachedBytes);
    }
    internal sealed record Proof(Shape Native, MethodAnalysisContext Contract,
        TypeAnalysisContext ReceiverType, FieldAnalysisContext? StoredField, InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && ReferenceEquals(Contract, other.Contract) &&
            ReferenceEquals(ReceiverType, other.ReceiverType) && ReferenceEquals(StoredField, other.StoredField) &&
            Input.Matches(other.Input);
    }
    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var result = new IsilRegister(null, ResultRegister);
        var body = new List<IsilInstruction>
        {
            new(0, OpCode.Call, proof.Contract, result, new IsilRegister(null, "rcx"), new IsilRegister(null, "rdx"), new Immediate(0))
                { NativeAddress = proof.Native.Invoke.IP, CallSemantics = CallSemantics.VirtualDispatch }
        };
        if (proof.StoredField is { } field)
            body.Add(new(body.Count, OpCode.Move, new ISIL.MemoryOperand(new IsilRegister(null, "r8"), null, field.Offset), result)
                { NativeAddress = proof.Native.Store!.Value.IP });
        body.Add(new(body.Count, OpCode.Return, result) { NativeAddress = proof.Native.Exit.IP });
        return body;
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext.Binary: PE } || !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext))
            return null;
        try
        {
            if (!Caller(method) || X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveShape(body) is not { } shape || X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
                (shape.IsTail ? body[0].NextIP - method.UnderlyingPointer != 4 :
                    body[0].NextIP - method.UnderlyingPointer != 2 || body[1].NextIP - method.UnderlyingPointer != 6) ||
                !unwind.MatchesUnwind(method.UnderlyingPointer, body[^1].NextIP, shape.IsTail ? (byte)4 : (byte)6,
                    0, shape.IsTail ? new byte[] { 4, 0x42 } : new byte[] { 6, 0x32, 2, 0x30 }) ||
                X86RuntimeNullThrowProof.TryIdentify(method.AppContext, shape.NullCall.NearBranchTarget) == null)
                return null;
            var receiver = method.Parameters[0].ParameterType;
            var entrySize = (ulong)Il2CppVirtualInvokeLayout.EntrySize(8);
            if (shape.SlotOffset < (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset ||
                (shape.SlotOffset - (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset) % entrySize != 0)
                return null;
            var slot = (shape.SlotOffset - (ulong)Il2CppVirtualInvokeLayout.X64VTableOffset) / entrySize;
            var hierarchy = Hierarchy(receiver);
            if (hierarchy == null || slot >= (ulong)receiver.Definition!.VTable.Length ||
                receiver.Definition.VTable[(int)slot] is not { Type: MetadataUsageType.MethodDef } usage ||
                method.AppContext.ResolveContextForMethod(usage) is not { } contract ||
                !Contract(contract, hierarchy, (int)slot) || !ReferenceEquals(usage.AsMethod(), contract.Definition))
                return null;
            FieldAnalysisContext? stored = null;
            if (shape.Store is { } store)
            {
                if (method.Parameters.Count != 3 || Hierarchy(method.Parameters[2].ParameterType) is not { } sinkHierarchy ||
                    !SafeSinkStorage(sinkHierarchy) ||
                    sinkHierarchy.SelectMany(type => type.Fields).Where(field => !field.IsStatic && field.Offset ==
                        (long)store.MemoryDisplacement64).ToArray() is not [var field] || !StoreField(field, method))
                    return null;
                stored = field;
            }
            else if (method.Parameters.Count != 2)
                return null;
            var values = new List<object>();
            CaptureMethod(method, values);
            CaptureMethod(contract, values);
            foreach (var type in hierarchy.Concat(Hierarchy(method.DeclaringType!)!).Concat(stored == null ? [] :
                         Hierarchy(method.Parameters[2].ParameterType)!).Distinct())
                CaptureType(type, values);
            if (stored != null)
            {
                CaptureField(stored, values);
                foreach (var neighbor in Hierarchy(method.Parameters[2].ParameterType)!.SelectMany(type => type.Fields)
                             .Where(field => !field.IsStatic && field.BackingData!.Field.RawFieldType!.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
                    EnumIntegerStorageProof.Capture(EnumIntegerStorageProof.Find(neighbor.FieldType)!, values);
            }
            var length = checked((int)(body[^1].NextIP - method.UnderlyingPointer));
            var pe = (PE)method.AppContext.Binary;
            var offset = checked((int)pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false));
            return new Proof(shape, contract, receiver, stored, new InputState(values,
                pe.GetRawBinaryContent().Slice(offset, length).ToArray(), method.RawBytes.AsSpan().ToArray()));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or NullReferenceException or
                                          EndOfStreamException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (10 or 17) || !Plain(body) || body[^1].Code != Code.Int3 || body[^1].Length != 1 ||
            body[^2] is not { Code: Code.Call_rel32_64, OpCount: 1, Op0Kind: OpKind.NearBranch64 })
            return null;
        var tail = body.Count == 10;
        var first = tail ? 1 : 3;
        if (tail ? !Stack(body[0], Mnemonic.Sub, 0x28) :
            !Unary(body[0], Code.Push_r64, NativeRegister.RBX) || !Stack(body[1], Mnemonic.Sub, 0x20) ||
            !Move(body[2], NativeRegister.RBX, NativeRegister.R8))
            return null;
        if (!Test(body[first], NativeRegister.RCX) || !Branch(body[first + 1], body[^2].IP) ||
            !Load(body[first + 2], NativeRegister.R8, NativeRegister.RCX, 0) ||
            !Load(body[first + 3], NativeRegister.RAX, NativeRegister.R8, body[first + 3].MemoryDisplacement64) ||
            body[first + 3].MemoryDisplacement64 > ulong.MaxValue - 8 ||
            !Load(body[first + 4], NativeRegister.R8, NativeRegister.R8, body[first + 3].MemoryDisplacement64 + 8))
            return null;
        if (tail)
        {
            if (!Stack(body[6], Mnemonic.Add, 0x28) || !Unary(body[7], Code.Jmp_rm64, NativeRegister.RAX))
                return null;
            return new Shape(body[7], body[7], body[8], null, body[4].MemoryDisplacement64, true);
        }
        if (!Unary(body[8], Code.Call_rm64, NativeRegister.RAX) || !Test(body[9], NativeRegister.RBX) ||
            !Branch(body[10], body[15].IP) || !Store(body[11]) || !Stack(body[12], Mnemonic.Add, 0x20) ||
            !Unary(body[13], Code.Pop_r64, NativeRegister.RBX) || body[14].Code != Code.Retnq || body[14].OpCount != 0)
            return null;
        return new Shape(body[8], body[14], body[15], body[11], body[6].MemoryDisplacement64, false);
    }

    private static bool Caller(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { } owner || Hierarchy(owner) == null ||
            !ReferenceEquals(owner.BaseType, method.AppContext.SystemTypes.SystemObjectType) || !OriginalMethod(method) ||
            !method.IsStatic || method.IsVirtual || method.BaseMethod != null || method.Overrides.Count != 0 ||
            method.Parameters.Count is not (2 or 3) ||
            method.Parameters[0].Definition!.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } receiver ||
            Hierarchy(method.Parameters[0].ParameterType) == null || receiver.Data.Dummy !=
                method.Parameters[0].ParameterType.Definition!.RawType.Data.Dummy ||
            method.Parameters[1].Definition!.RawType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
            !ReferenceEquals(method.Parameters[1].ParameterType, method.AppContext.SystemTypes.SystemInt32Type) ||
            method.Parameters.Count == 3 && (method.Parameters[2].Definition!.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } sink || Hierarchy(method.Parameters[2].ParameterType) == null ||
                sink.Data.Dummy != method.Parameters[2].ParameterType.Definition!.RawType.Data.Dummy) ||
            method.UnderlyingPointer == 0 || !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var binding] || !ReferenceEquals(binding, method))
            return false;
        var slots = new X64CallingConventionResolver().ResolveForParameters(method);
        return slots.Length == method.Parameters.Count + 1 && slots.Select((slot, index) => slot is IsilRegister register && register ==
            new IsilRegister(null, index switch { 0 => "rcx", 1 => "rdx", 2 => "r8", _ => "r9" })).All(value => value);
    }

    private static bool Contract(MethodAnalysisContext target, IReadOnlyList<TypeAnalysisContext> hierarchy, int slot) =>
        target.DeclaringType != null && hierarchy.Contains(target.DeclaringType) && OriginalMethod(target) &&
        target.Visibility == MethodAttributes.Public && !target.IsStatic && target.IsVirtual && !target.IsFinal &&
        target.Definition!.slot == slot && target.Parameters is [var parameter] &&
        parameter.Definition!.RawType!.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 &&
        ReferenceEquals(parameter.ParameterType, target.AppContext.SystemTypes.SystemInt32Type);

    private static bool OriginalMethod(MethodAnalysisContext method) =>
        method.Definition is { GenericContainer: null, RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
            NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) &&
        definition.parameterCount == method.Parameters.Count && definition.InternalParameterData?.Length == method.Parameters.Count &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                 MethodImplAttributes.InternalCall)) == 0 &&
        method.GenericParameters.Count == 0 && method.OverrideReturnType == null &&
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        method.Parameters.Select((parameter, index) => ReferenceEquals(parameter.DeclaringMethod, method) &&
            ReferenceEquals(parameter.Definition, definition.InternalParameterData[index]) && parameter.ParameterIndex == index &&
            parameter.Definition!.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } && parameter.OverrideParameterType == null &&
            ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) && parameter.Name == parameter.DefaultName &&
            parameter.Attributes == ParameterAttributes.None && parameter.Attributes == parameter.DefaultAttributes &&
            parameter.OverrideAttributes == null && !parameter.UseOverrideDefaultValue).All(value => value);

    internal static TypeAnalysisContext[]? Hierarchy(TypeAnalysisContext start)
    {
        var types = new List<TypeAnalysisContext>();
        var seen = new HashSet<TypeAnalysisContext>();
        for (var type = start; type != null; type = type.BaseType)
        {
            if (!seen.Add(type) || types.Count == 32 || type.Definition is not { GenericContainer: null, HasCctor: false,
                    IsImportOrWindowsRuntime: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                    RawType: { NumMods: 0, Byref: 0, Pinned: 0 } raw } definition ||
                raw.Type != (ReferenceEquals(type, start.AppContext.SystemTypes.SystemObjectType) ?
                    Il2CppTypeEnum.IL2CPP_TYPE_OBJECT : Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
                definition.RawBaseType is { } parent && parent is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, NumMods: 0, Byref: 0, Pinned: 0 } ||
                type.IsValueType || type.IsInterface || type.IsGenericInstance || type.GenericParameters.Count != 0 ||
                type.DeclaringType != null || !definition.DeclaringTypeIndex.IsNull || type.Name != type.DefaultName ||
                type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
                (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
                (type.Attributes & TypeAttributes.Import) != 0 || !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
                type.Methods.Count != definition.MethodCount || type.Fields.Count != definition.FieldCount ||
                type.Methods.Any(method => method.Definition == null || !ReferenceEquals(method.Definition.DeclaringType, definition) ||
                    method.Name == ".cctor"))
                return null;
            types.Add(type);
        }
        return types.Count > 0 && ReferenceEquals(types[^1], start.AppContext.SystemTypes.SystemObjectType) ? types.ToArray() : null;
    }

    internal static bool StoreField(FieldAnalysisContext field, MethodAnalysisContext caller) =>
        field.Visibility == FieldAttributes.Public && !field.IsStatic && field.OverrideFieldType == null &&
        field.Attributes == field.DefaultAttributes && (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.HasFieldMarshal)) == 0 &&
        field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } &&
        ReferenceEquals(field.FieldType, caller.AppContext.SystemTypes.SystemInt32Type) && field.Offset >= 16 &&
        (ulong)field.Offset + 4 <= field.DeclaringType.Definition!.RawSizes.instance_size &&
        NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field,
            new LocalVariable("proved-sink", new IsilRegister(null, "r8"), caller.Parameters[2].ParameterType), field.Offset), 32);

    // Establish raw storage families before the shared overlap proof resolves
    // sibling wrappers. Arrays, constructed types and pointers need a separate
    // finite descriptor proof and remain outside this bounded sink family.
    internal static bool SafeSinkStorage(IEnumerable<TypeAnalysisContext> hierarchy)
    {
        foreach (var field in hierarchy.SelectMany(type => type.Fields).Where(field => !field.IsStatic))
        {
            if (field.OverrideFieldType != null || field.BackingData?.Field.RawFieldType is not
                    { NumMods: 0, Byref: 0, Pinned: 0 } raw)
                return false;
            if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS)
            {
                if (raw.AsClass().RawType.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS || Hierarchy(field.FieldType) == null)
                    return false;
            }
            else if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            {
                if (EnumIntegerStorageProof.Find(field.FieldType) == null)
                    return false;
            }
            else if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING or
                     Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
                     Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or
                     Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or
                     Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
                     Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
                     Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8 or
                     Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U)
            {
                if (!ReferenceEquals(field.FieldType, field.AppContext.SystemTypes.GetPrimitive(raw.Type)))
                    return false;
            }
            else
                return false;
        }
        return true;
    }

    internal static void CaptureMethod(MethodAnalysisContext method, List<object> values)
    {
        var raw = method.Definition!;
        values.AddRange([method, method.DeclaringType!, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            method.UnderlyingPointer, raw.nameIndex, raw.token, raw.flags, raw.iflags, raw.slot, raw.returnTypeIdx,
            raw.declaringTypeIdx, raw.parameterStart, raw.parameterCount, raw.genericContainerIndex]);
        CaptureRaw(raw.RawReturnType!, values);
        foreach (var parameter in method.Parameters)
        {
            values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterType,
                parameter.Definition!.nameIndex, parameter.Definition.token, parameter.Definition.typeIndex]);
            CaptureRaw(parameter.Definition.RawType!, values);
        }
        values.Add(method.BaseMethod ?? (object)false);
        foreach (var overridden in method.Overrides)
            values.Add(overridden);
        values.Add(method.Overrides.Count);
    }
    internal static void CaptureType(TypeAnalysisContext type, List<object> values)
    {
        var raw = type.Definition!;
        values.AddRange([type, type.Name, type.Namespace, type.Attributes, type.BaseType ?? (object)false,
            raw.NameIndex, raw.NamespaceIndex, raw.Flags, raw.Bitfield, raw.Token, raw.ParentIndex, raw.DeclaringTypeIndex,
            raw.ByvalTypeIndex, raw.GenericContainerIndex, raw.FirstMethodIdx, raw.MethodCount, raw.VtableStart, raw.VtableCount,
            raw.RawSizes.instance_size, raw.RawSizes.native_size]);
        CaptureRaw(raw.RawType, values);
        if (raw.RawBaseType != null)
            CaptureRaw(raw.RawBaseType, values);
        for (var index = 0; index < raw.VtableCount; index++)
            values.Add(type.AppContext.Metadata.VTableMethodIndices[raw.VtableStart + index]);
        foreach (var entry in raw.VTable)
            values.Add(entry == null ? false : entry.Type == MetadataUsageType.MethodDef ? (object)entry.AsMethod() : entry);
        values.Add(raw.VTable.Length);
        foreach (var field in type.Fields)
        {
            values.AddRange([field, field.Name, field.Attributes, field.Offset,
                field.OverrideFieldType ?? (object)false, field.UseOverrideConstantValue, field.OverrideStaticArrayInitialValue ?? (object)false,
                field.BackingData!.Field.nameIndex, field.BackingData.Field.typeIndex, field.BackingData.Field.token]);
            CaptureRaw(field.BackingData.Field.RawFieldType!, values);
        }
    }
    internal static void CaptureField(FieldAnalysisContext field, List<object> values)
    {
        values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType, field.BackingData!.Field.nameIndex,
            field.BackingData.Field.typeIndex, field.BackingData.Field.token]);
        CaptureRaw(field.BackingData.Field.RawFieldType!, values);
    }
    internal static void CaptureRaw(Il2CppType raw, List<object> values) => values.AddRange(
        [raw.Bits, raw.Datapoint, raw.Data.Dummy, raw.Type, raw.Attrs, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType]);

    private static bool Plain(IReadOnlyList<NativeInstruction> body) => body.All(instruction => !instruction.IsInvalid &&
        instruction.CodeSize == CodeSize.Code64 && !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == NativeRegister.None) &&
        body.Select((instruction, index) => index == 0 || instruction.IP == body[index - 1].NextIP).All(value => value);
    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong amount) => instruction.Mnemonic == mnemonic &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && instruction.GetImmediate(1) == amount;
    private static bool Unary(NativeInstruction instruction, Code code, NativeRegister register) => instruction.Code == code &&
        instruction.OpCount == 1 && instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;
    private static bool Move(NativeInstruction instruction, NativeRegister destination, NativeRegister source) =>
        instruction.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;
    private static bool Test(NativeInstruction instruction, NativeRegister register) => instruction.Code == Code.Test_rm64_r64 &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Register == register;
    private static bool Branch(NativeInstruction instruction, ulong target) => instruction.Mnemonic == Mnemonic.Je &&
        instruction.OpCount == 1 && instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;
    private static bool Load(NativeInstruction instruction, NativeRegister destination, NativeRegister source, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == source &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == 8;
    private static bool Store(NativeInstruction instruction) => instruction.Code == Code.Mov_rm32_r32 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RBX && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue && instruction.MemorySize.GetSize() == 4 &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == NativeRegister.EAX;
}
