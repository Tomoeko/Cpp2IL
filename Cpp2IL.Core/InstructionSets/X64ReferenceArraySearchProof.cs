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

/// <summary>Authenticates a complete captured-array first-match search and its helper exits.</summary>
internal static class X64ReferenceArraySearchProof
{
    internal const string EvidenceKey = "X64ReferenceArraySearchProof";

    internal sealed record Shape(int ArrayOffset, int KeyOffset, bool ReturnsBoolean, bool CapturesElement,
        NativeInstruction BoundsCall, NativeInstruction? NullCall);

    internal sealed record Proof(Shape Native, FieldAnalysisContext ArrayField, FieldAnalysisContext KeyField,
        X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native &&
            ReferenceEquals(ArrayField, other.ArrayField) && ReferenceEquals(KeyField, other.KeyField) &&
            Input.Matches(other.Input);
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                !OrdinaryMethod(method) || !HasExpectedAbi(method)) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveShape(body) is not { } shape ||
                shape.ReturnsBoolean != ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 4, 0, [4, 0x42]) ||
                shape.NullCall is { } nullCall && X86RuntimeNullThrowProof.TryIdentify(app, nullCall.NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app, shape.BoundsCall.NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    shape.NullCall is { } nullExit ? new HashSet<ulong> { nullExit.IP, shape.BoundsCall.IP } :
                        new HashSet<ulong> { shape.BoundsCall.IP }) != null)
                return null;

            var owner = method.DeclaringType!;
            var values = new List<object>();
            // Validate raw array chains before any lazy field-type resolution.
            if (!CaptureType(owner, values) || !CaptureType(app.SystemTypes.SystemObjectType, values)) return null;
            if (owner.Fields.Where(field => !field.IsStatic && field.Offset == shape.ArrayOffset).ToArray()
                    is not [{ } arrayField] ||
                arrayField.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY } rawArray ||
                rawArray.GetEncapsulatedType() is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS } rawElement ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
                !OrdinaryClass(element) || !ReferenceEquals(app.ResolveIl2CppType(rawElement), element) ||
                !CaptureType(element, values) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(arrayField,
                    new LocalVariable("search-owner", new ManagedRegister(null, "rcx"), owner), shape.ArrayOffset)))
                return null;

            if (element.Fields.Where(field => !field.IsStatic && field.Offset == shape.KeyOffset).ToArray()
                    is not [{ } keyField] ||
                keyField.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 } ||
                !ReferenceEquals(keyField.FieldType, app.SystemTypes.SystemInt32Type) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(keyField,
                    new LocalVariable("search-element", new ManagedRegister(null, "rdx"), element), shape.KeyOffset), 32))
                return null;

            X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
            X64SmallAggregateFieldGetterProof.CaptureRawType(method.Definition!.RawReturnType!, values);
            var parameter = method.Parameters[0];
            values.Add(parameter);
            values.Add(parameter.Name);
            values.Add(parameter.Attributes);
            values.Add(parameter.ParameterIndex);
            values.Add(parameter.Definition!.nameIndex);
            values.Add(parameter.Definition.token);
            values.Add(parameter.Definition.typeIndex);
            X64SmallAggregateFieldGetterProof.CaptureRawType(parameter.Definition.RawType!, values);
            // Both helper identities are rebound and their complete native contracts
            // are reproved above. Retain the consumed managed identity facts as well.
            foreach (var exceptionName in shape.NullCall == null ? new[] { "IndexOutOfRangeException" } :
                         new[] { "NullReferenceException", "IndexOutOfRangeException" })
            {
                if (X86RuntimeNullThrowProof.BindIdentity(app, exceptionName) is not
                    { Definition.RawReturnType.Data: not null, DeclaringType.Definition.RawType.Data: not null } constructor)
                    return null;
                X64SmallAggregateFieldGetterProof.CaptureMethod(constructor, values);
                X64SmallAggregateFieldGetterProof.CaptureRawType(constructor.Definition!.RawReturnType!, values);
                var type = constructor.DeclaringType!;
                values.AddRange([type, type.Name, type.Namespace, type.Attributes, type.BaseType!,
                    type.Definition!.Token, type.Definition.Flags, type.Definition.Bitfield]);
                X64SmallAggregateFieldGetterProof.CaptureRawType(type.Definition.RawType, values);
            }
            var offset = pe.MapVirtualAddressToRaw(body[0].IP, false);
            var length = checked((int)(body[^1].NextIP - body[0].IP));
            return new(shape, arrayField, keyField,
                new(values, pe.GetRawBinaryContent().Slice(checked((int)offset), length).ToArray()));
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

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null, HasCctor: false, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Data: not null,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance && type.GenericParameters.Count == 0 &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes && ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        ReferenceEquals(type.BaseType, type.AppContext.SystemTypes.SystemObjectType) &&
        type.Fields.Count == type.Definition.FieldCount &&
        type.Fields.Select(field => field.BackingData?.Field).SequenceEqual(type.Definition.Fields!) &&
        type.Fields.All(field =>
            ReferenceEquals(field.DeclaringType, type) &&
            ReferenceEquals(field.BackingData?.Field.DeclaringType, type.Definition)) &&
        type.Methods.Count == type.Definition.MethodCount &&
        type.Methods.Select(method => method.Definition).SequenceEqual(type.Definition.Methods!) &&
        type.Methods.All(method =>
            method.Definition != null && ReferenceEquals(method.DeclaringType, type) &&
            ReferenceEquals(method.Definition.DeclaringType, type.Definition)) &&
        !type.Methods.Any(method => method.Name == ".cctor");

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.DeclaringType is { } owner && OrdinaryClass(owner) &&
            method.Definition is { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } rawReturn,
                InternalParameterData: [{ } original] } definition &&
            ReferenceEquals(definition.DeclaringType, owner.Definition) &&
            method.Parameters is [{ } parameter] && ReferenceEquals(parameter.Definition, original) &&
            ReferenceEquals(parameter.DeclaringMethod, method) && parameter.ParameterIndex == 0 &&
            !parameter.IsRef && parameter.OverrideParameterType == null && !parameter.UseOverrideDefaultValue &&
            parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes &&
            ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) &&
            ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) &&
            original.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } &&
            !method.IsStatic && !method.IsVirtual && method.Name is not (".ctor" or ".cctor") &&
            method.Name == method.DefaultName && method.GenericParameters.Count == 0 &&
            method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) == 0 &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
            method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
            (ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) && rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
             ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) && rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) &&
            method.BaseMethod == null && method.Overrides.Count == 0 && !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
            RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
            app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) &&
            bindings is [var bound] && ReferenceEquals(bound, method);
    }

    private static bool HasExpectedAbi(MethodAnalysisContext method)
    {
        var resolver = new X64CallingConventionResolver();
        return !resolver.ReturnsViaHiddenBuffer(method) && resolver.ResolveForParameters(method) is
            [ManagedRegister { Name: "rcx" }, ManagedRegister { Name: "rdx" }, ManagedRegister { Name: "r8" }];
    }

    private static bool CaptureType(TypeAnalysisContext type, List<object> values)
    {
        if (type.Definition is not { RawType.Data: not null } definition) return false;
        values.AddRange([type, type.Name, type.Namespace, type.Attributes, type.BaseType!, definition.NameIndex,
            definition.NamespaceIndex, definition.Token, definition.Flags, definition.Bitfield, definition.ByvalTypeIndex,
            definition.ParentIndex, definition.GenericContainerIndex, definition.FirstFieldIdx, definition.FieldCount,
            definition.FirstMethodIdx, definition.MethodCount, definition.RawSizes.instance_size,
            definition.RawSizes.native_size, definition.RawSizes.static_fields_size, definition.RawSizes.thread_static_fields_size]);
        X64SmallAggregateFieldGetterProof.CaptureRawType(definition.RawType, values);
        foreach (var method in type.Methods)
        {
            if (method.Definition == null) return false;
            X64SmallAggregateFieldGetterProof.CaptureMethod(method, values);
        }
        foreach (var field in type.Fields)
        {
            if (field.BackingData?.Field is not { RawFieldType: { } raw } original ||
                !CaptureRaw(raw, values, new HashSet<Il2CppType>(), 0)) return false;
            values.AddRange([field, field.Name, field.Attributes, field.Offset, original.nameIndex, original.token,
                original.typeIndex.Value]);
        }
        // Canonical element contexts are stable; resolving an array wrapper twice is not.
        foreach (var field in type.Fields)
        {
            var storage = field.FieldType;
            while (storage is SzArrayTypeAnalysisContext array) storage = array.ElementType;
            values.AddRange([storage, storage.Name, storage.Namespace, storage.Attributes, storage.Type]);
            if (storage.Definition is { } stored)
            {
                values.AddRange([stored.NameIndex, stored.NamespaceIndex, stored.Flags, stored.Bitfield,
                    stored.RawSizes.instance_size, stored.RawSizes.native_size,
                    stored.RawSizes.static_fields_size, stored.RawSizes.thread_static_fields_size]);
                if (stored.RawType.Data == null) return false;
                X64SmallAggregateFieldGetterProof.CaptureRawType(stored.RawType, values);
            }
        }
        return true;
    }

    private static bool CaptureRaw(Il2CppType raw, List<object> values, HashSet<Il2CppType> visited, int depth)
    {
        if (depth > 16 || raw.Data == null || !visited.Add(raw) || raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0 ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or
                Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_BYREF or
                Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR)
            return false;
        X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
        return raw.Type != Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY ||
               CaptureRaw(raw.GetEncapsulatedType(), values, visited, depth + 1);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> native)
    {
        if (native.Count is < 25 or > 64 || native.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 && instruction.IP != native[index - 1].NextIP).Any())
            return null;
        var body = native.Where(instruction => !Padding(instruction)).ToArray();
        var shape = body.Length switch
        {
            25 or 29 => TryProveCapturedElementShape(body),
            28 or 30 => TryProveRepeatedElementShape(body),
            _ => null
        };
        // The initial owner read is unguarded. Its layout alone does not
        // establish the target's managed null-receiver fault behavior.
        return shape != null && X64ReferenceFieldNullComparisonProof.IsProvedNullReceiverOffset(shape.ArrayOffset, 8)
            ? shape : null;
    }

    private static Shape? TryProveRepeatedElementShape(NativeInstruction[] body)
    {
        var boolean = body.Length == 30;
        var epilogue = boolean ? 24 : 22;
        var bounds = epilogue + 2;
        var nullCall = epilogue + 4;
        if (!X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !Memory(body[1], Code.Mov_r64_rm64, NativeRegister.R8, NativeRegister.RCX, NativeRegister.None, 1, 0, offset: true) ||
            !Registers(body[2], Code.Mov_r32_rm32, NativeRegister.R9D, NativeRegister.EDX) ||
            !Registers(body[3], Code.Xor_r32_rm32, NativeRegister.EAX, NativeRegister.EAX) ||
            !Registers(body[4], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[5], Mnemonic.Je, body[21].IP) ||
            !Memory(body[6], Code.Cmp_r32_rm32, NativeRegister.EAX, NativeRegister.R8, NativeRegister.None, 1, (ulong)Il2CppArrayUtils.GetLengthOffset(8)) ||
            !Branch(body[7], Mnemonic.Jge, body[21].IP) ||
            !Registers(body[8], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[9], Mnemonic.Je, body[nullCall].IP) ||
            !Memory(body[10], Code.Cmp_r32_rm32, NativeRegister.EAX, NativeRegister.R8, NativeRegister.None, 1, (ulong)Il2CppArrayUtils.GetLengthOffset(8)) ||
            !Branch(body[11], Mnemonic.Jae, body[bounds].IP) ||
            !Registers(body[12], Code.Movsxd_r64_rm32, NativeRegister.RCX, NativeRegister.EAX) ||
            body[13].Code != Code.Cmp_rm64_imm8 || body[13].Op0Kind != OpKind.Memory ||
            !Address(body[13], NativeRegister.R8, NativeRegister.RCX, 8, (ulong)Il2CppArrayUtils.GetFirstItemOffset(8)) || body[13].Immediate8to64 != 0 ||
            !Branch(body[14], Mnemonic.Je, body[19].IP) ||
            !Registers(body[15], Code.Movsxd_r64_rm32, NativeRegister.RCX, NativeRegister.EAX) ||
            !Memory(body[16], Code.Mov_r64_rm64, NativeRegister.RDX, NativeRegister.R8, NativeRegister.RCX, 8, (ulong)Il2CppArrayUtils.GetFirstItemOffset(8)) ||
            body[17].Code != Code.Cmp_rm32_r32 || body[17].Op0Kind != OpKind.Memory ||
            body[17].Op1Kind != OpKind.Register || body[17].Op1Register != NativeRegister.R9D ||
            !Address(body[17], NativeRegister.RDX, NativeRegister.None, 1, 0, offset: true) ||
            !Branch(body[18], Mnemonic.Je, body[22].IP) ||
            body[19].Code != Code.Inc_rm32 || body[19].Op0Kind != OpKind.Register || body[19].Op0Register != NativeRegister.EAX ||
            !Branch(body[20], Mnemonic.Jmp, body[6].IP) ||
            body[21].Code != Code.Mov_r32_imm32 || body[21].Op0Register != NativeRegister.EAX || body[21].Immediate32 != uint.MaxValue ||
            boolean && (body[22].Code != Code.Shr_rm32_imm8 || body[22].Op0Kind != OpKind.Register ||
                body[22].Op0Register != NativeRegister.EAX || body[22].Immediate8 != 31 ||
                body[23].Code != Code.Xor_AL_imm8 || body[23].Immediate8 != 1) ||
            !X64Stack28BodyProof.Stack(body[epilogue], Mnemonic.Add) ||
            body[epilogue + 1].Code != Code.Retnq || body[epilogue + 1].OpCount != 0 ||
            body[bounds].Code != Code.Call_rel32_64 || body[bounds].Op0Kind != OpKind.NearBranch64 ||
            body[bounds].NearBranchTarget == 0 || body[bounds + 1].Code != Code.Int3 ||
            body[nullCall].Code != Code.Call_rel32_64 || body[nullCall].Op0Kind != OpKind.NearBranch64 ||
            body[nullCall].NearBranchTarget == 0 || body[nullCall + 1].Code != Code.Int3)
            return null;
        return new(checked((int)body[1].MemoryDisplacement64), checked((int)body[17].MemoryDisplacement64),
            boolean, false, body[bounds], body[nullCall]);
    }

    private static Shape? TryProveCapturedElementShape(NativeInstruction[] body)
    {
        var boolean = body.Length == 29;
        var key = boolean ? NativeRegister.R11D : NativeRegister.R10D;
        var length = boolean ? NativeRegister.R10D : NativeRegister.R9D;
        var guard = boolean ? 4 : 3;
        var loop = guard + 5;
        var increment = loop + 10;
        var absent = boolean ? increment + 3 : increment + 2;
        var result = boolean ? increment + 2 : increment + 3;
        var epilogue = boolean ? 25 : 21;
        var bounds = epilogue + 2;
        if (!X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            !Memory(body[1], Code.Mov_r64_rm64, NativeRegister.R8, NativeRegister.RCX, NativeRegister.None, 1, 0, offset: true) ||
            !Registers(body[2], Code.Mov_r32_rm32, key, NativeRegister.EDX) ||
            boolean && (body[3].Code != Code.Mov_r32_imm32 || body[3].Op0Register != NativeRegister.R9D ||
                body[3].Immediate32 != uint.MaxValue) ||
            !Registers(body[guard], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[guard + 1], Mnemonic.Je, body[absent].IP) ||
            !Memory(body[guard + 2], Code.Mov_r32_rm32, length, NativeRegister.R8, NativeRegister.None, 1, (ulong)Il2CppArrayUtils.GetLengthOffset(8)) ||
            !Registers(body[guard + 3], Code.Xor_r32_rm32, NativeRegister.EAX, NativeRegister.EAX) ||
            !Registers(body[guard + 4], Code.Mov_r32_rm32, NativeRegister.ECX, NativeRegister.EAX) ||
            !Registers(body[loop], Code.Cmp_r32_rm32, NativeRegister.EAX, length) ||
            !Branch(body[loop + 1], Mnemonic.Jge, body[absent].IP) ||
            !Registers(body[loop + 2], Code.Cmp_r32_rm32, NativeRegister.EAX, length) ||
            !Branch(body[loop + 3], Mnemonic.Jae, body[bounds].IP) ||
            !Registers(body[loop + 4], Code.Movsxd_r64_rm32, NativeRegister.RCX, NativeRegister.EAX) ||
            !Memory(body[loop + 5], Code.Mov_r64_rm64, NativeRegister.RDX, NativeRegister.R8, NativeRegister.RCX, 8, (ulong)Il2CppArrayUtils.GetFirstItemOffset(8)) ||
            !Registers(body[loop + 6], Code.Test_rm64_r64, NativeRegister.RDX, NativeRegister.RDX) ||
            !Branch(body[loop + 7], Mnemonic.Je, body[increment].IP) ||
            body[loop + 8].Code != Code.Cmp_rm32_r32 || body[loop + 8].Op0Kind != OpKind.Memory ||
            body[loop + 8].Op1Kind != OpKind.Register || body[loop + 8].Op1Register != key ||
            !Address(body[loop + 8], NativeRegister.RDX, NativeRegister.None, 1, 0, offset: true) ||
            !Branch(body[loop + 9], Mnemonic.Je, body[result].IP) ||
            body[increment].Code != Code.Inc_rm32 || body[increment].Op0Kind != OpKind.Register ||
            body[increment].Op0Register != NativeRegister.EAX ||
            !Branch(body[increment + 1], Mnemonic.Jmp, body[loop - 1].IP) ||
            !boolean && (body[absent].Code != Code.Mov_r32_imm32 || body[absent].Op0Register != NativeRegister.EAX ||
                body[absent].Immediate32 != uint.MaxValue) ||
            boolean && (!Registers(body[result], Code.Mov_r32_rm32, NativeRegister.R9D, NativeRegister.EAX) ||
                body[absent].Code != Code.Shr_rm32_imm8 || body[absent].Op0Register != NativeRegister.R9D ||
                body[absent].Immediate8 != 31 || body[absent + 1].Code != Code.Xor_rm8_imm8 ||
                body[absent + 1].Op0Kind != OpKind.Register || body[absent + 1].Op0Register != NativeRegister.R9L ||
                body[absent + 1].Immediate8 != 1 ||
                !Registers(body[absent + 2], Code.Movzx_r32_rm8, NativeRegister.EAX, NativeRegister.R9L)) ||
            !X64Stack28BodyProof.Stack(body[epilogue], Mnemonic.Add) ||
            body[epilogue + 1].Code != Code.Retnq || body[epilogue + 1].OpCount != 0 ||
            body[bounds].Code != Code.Call_rel32_64 || body[bounds].Op0Kind != OpKind.NearBranch64 ||
            body[bounds].NearBranchTarget == 0 || body[bounds + 1].Code != Code.Int3)
            return null;
        return new(checked((int)body[1].MemoryDisplacement64), checked((int)body[loop + 8].MemoryDisplacement64),
            boolean, true, body[bounds], null);
    }

    private static bool Padding(NativeInstruction instruction) => instruction.Mnemonic == Mnemonic.Nop ||
        instruction.Length == 2 && instruction.Mnemonic == Mnemonic.Xchg && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.AX && instruction.Op1Register == NativeRegister.AX;

    private static bool Registers(NativeInstruction instruction, Code code, NativeRegister first, NativeRegister second) =>
        (instruction.Code == code || code == Code.Mov_r32_rm32 && instruction.Code == Code.Mov_rm32_r32 ||
         code == Code.Xor_r32_rm32 && instruction.Code == Code.Xor_rm32_r32 ||
         code == Code.Cmp_r32_rm32 && instruction.Code == Code.Cmp_rm32_r32) &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == first && instruction.Op1Register == second;

    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic, ulong target) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;

    private static bool Memory(NativeInstruction instruction, Code code, NativeRegister register, NativeRegister owner,
        NativeRegister index, int scale, ulong displacement, bool offset = false) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Memory &&
        Address(instruction, owner, index, scale, displacement, offset);

    private static bool Address(NativeInstruction instruction, NativeRegister owner, NativeRegister index,
        int scale, ulong displacement, bool offset = false) =>
        instruction.MemoryBase == owner && instruction.MemoryIndex == index && instruction.MemoryIndexScale == scale &&
        (offset ? instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue : instruction.MemoryDisplacement64 == displacement);
}
