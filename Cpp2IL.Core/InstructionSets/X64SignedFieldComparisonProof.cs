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

/// <summary>Proves an ordered, signed three-way comparison of two guarded Int32 fields.</summary>
internal static class X64SignedFieldComparisonProof
{
    internal const string EvidenceKey = "X64SignedFieldComparisonProof";
    internal sealed record Shape(int FieldOffset, bool CapturesFields, NativeInstruction NullCall);
    internal sealed record Proof(Shape Native, FieldAnalysisContext Field, MethodAnalysisContext NullConstructor,
        X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && ReferenceEquals(Field, other.Field) &&
            ReferenceEquals(NullConstructor, other.NullConstructor) && Input.Matches(other.Input);
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                !OrdinaryMethod(method) || !HasExpectedAbi(method) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
                bindings.Any(candidate => !OrdinaryMethod(candidate) || !HasExpectedAbi(candidate) ||
                    !ReferenceEquals(candidate.Parameters[0].ParameterType, method.Parameters[0].ParameterType)))
                return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 4, 0, [4, 0x42]) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullCall.NearBranchTarget) == null ||
                X86RuntimeNullThrowProof.BindIdentity(app) is not { } constructor ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { shape.NullCall.IP }) != null)
                return null;

            var item = method.Parameters[0].ParameterType;
            if (item.Fields.Where(field => !field.IsStatic && field.Offset == shape.FieldOffset).ToArray()
                    is not [{ } field] || field.Name != field.DefaultName || field.OverrideFieldType != null ||
                (field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public ||
                field.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemInt32Type) ||
                !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field,
                    new LocalVariable("comparison-first", new ManagedRegister(null, "rdx"), item), shape.FieldOffset), 32))
                return null;

            var values = new List<object>();
            var seen = new HashSet<TypeAnalysisContext>();
            foreach (var type in bindings.Select(candidate => candidate.DeclaringType!).Append(item)
                         .Append(constructor.DeclaringType!))
                if (!CaptureChain(type, values, seen)) return null;
            foreach (var bound in bindings)
            {
                X64SmallAggregateFieldGetterProof.CaptureMethod(bound, values);
                X64SmallAggregateFieldGetterProof.CaptureRawType(bound.Definition!.RawReturnType!, values);
                foreach (var parameter in bound.Parameters)
                {
                    values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterIndex,
                        parameter.Definition!.nameIndex, parameter.Definition.token, parameter.Definition.typeIndex]);
                    X64SmallAggregateFieldGetterProof.CaptureRawType(parameter.Definition.RawType!, values);
                }
            }
            X64SmallAggregateFieldGetterProof.CaptureMethod(constructor, values);
            if (constructor.Definition?.RawReturnType is not { Data: not null } rawReturn) return null;
            X64SmallAggregateFieldGetterProof.CaptureRawType(rawReturn, values);
            var length = checked((int)(body[^1].NextIP - body[0].IP));
            var offset = checked((int)pe.MapVirtualAddressToRaw(body[0].IP, false));
            return new(shape, field, constructor, new(values, pe.GetRawBinaryContent().Slice(offset, length).ToArray()));
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

    private static bool OrdinaryMethod(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { } owner || !OrdinaryClass(owner) ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) || method.BaseMethod != null || method.Overrides.Count != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 },
                InternalParameterData: [{ } first, { } second] } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || method.Parameters.Count != 2)
            return false;
        for (var index = 0; index < 2; index++)
        {
            var parameter = method.Parameters[index];
            if (!ReferenceEquals(parameter.Definition, index == 0 ? first : second) ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != index ||
                parameter.Definition!.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } ||
                parameter.IsRef || parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
                parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                !ReferenceEquals(parameter.ParameterType, method.Parameters[0].ParameterType) ||
                !OrdinaryClass(parameter.ParameterType))
                return false;
        }
        return true;
    }

    private static bool HasExpectedAbi(MethodAnalysisContext method)
    {
        var resolver = new X64CallingConventionResolver();
        return !resolver.ReturnsViaHiddenBuffer(method) && resolver.ResolveForParameters(method) is
            [ManagedRegister { Name: "rcx" }, ManagedRegister { Name: "rdx" },
                ManagedRegister { Name: "r8" }, ManagedRegister { Name: "r9" }];
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } raw } &&
        (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
         raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT && ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType)) &&
        !type.IsValueType && !type.IsInterface && !type.IsGenericInstance && type.GenericParameters.Count == 0 &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace && type.Attributes == type.DefaultAttributes &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) && HasOriginalEnclosingChain(type) &&
        type.Fields.Count == type.Definition.FieldCount &&
        type.Fields.Select(field => field.BackingData?.Field).SequenceEqual(type.Definition.Fields!) &&
        type.Methods.Count == type.Definition.MethodCount &&
        type.Methods.Select(method => method.Definition).SequenceEqual(type.Definition.Methods!);

    private static bool HasOriginalEnclosingChain(TypeAnalysisContext type)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.DeclaringType)
            if (!visited.Add(current) || current.Definition is not { } definition ||
                current.IsGenericInstance || current.GenericParameters.Count != 0 ||
                !ReferenceEquals(current.DeclaringType?.Definition, definition.DeclaringType) ||
                current.DeclaringType == null && !definition.DeclaringTypeIndex.IsNull)
                return false;
        return true;
    }

    private static bool CaptureChain(TypeAnalysisContext type, List<object> values, HashSet<TypeAnalysisContext> seen)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!visited.Add(current) || !OrdinaryClass(current)) return false;
            if (!seen.Add(current)) continue;
            var definition = current.Definition!;
            values.AddRange([current, current.Name, current.Namespace, current.Attributes, current.BaseType!,
                current.DeclaringAssembly, (object?)current.DeclaringType ?? DBNull.Value, definition.DeclaringTypeIndex,
                definition.NameIndex, definition.NamespaceIndex, definition.Token, definition.Flags, definition.Bitfield,
                definition.ByvalTypeIndex, definition.ParentIndex, definition.GenericContainerIndex,
                definition.FirstFieldIdx, definition.FieldCount, definition.FirstMethodIdx, definition.MethodCount,
                definition.RawSizes.instance_size, definition.RawSizes.native_size,
                definition.RawSizes.static_fields_size, definition.RawSizes.thread_static_fields_size]);
            X64SmallAggregateFieldGetterProof.CaptureRawType(definition.RawType, values);
            foreach (var member in current.Methods)
            {
                if (member.Name != member.DefaultName || member.Attributes != member.DefaultAttributes ||
                    member.ImplAttributes != member.DefaultImplAttributes || member.Definition is not { } original ||
                    !ReferenceEquals(member.DeclaringType, current) || !ReferenceEquals(original.DeclaringType, definition))
                    return false;
                // Unused sibling return types may resolve to fresh array wrappers.
                // Retain the original member identity and declaration facts without
                // making its body or a wrapper allocation part of this comparison.
                values.AddRange([member, member.Name, member.Attributes, member.ImplAttributes, member.UnderlyingPointer,
                    original.nameIndex, original.token, original.flags, original.iflags, original.declaringTypeIdx,
                    original.returnTypeIdx, original.parameterStart, original.parameterCount, original.genericContainerIndex]);
            }
            foreach (var field in current.Fields)
            {
                if (field.BackingData?.Field is not { RawFieldType: { Data: not null } raw } original ||
                    field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                    field.Offset != field.DefaultOffset || field.OverrideFieldType != null ||
                    !ReferenceEquals(field.DeclaringType, current) || !ReferenceEquals(original.DeclaringType, definition))
                    return false;
                values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType.FullName,
                    field.FieldType.Type, original.nameIndex, original.token, original.typeIndex]);
                X64SmallAggregateFieldGetterProof.CaptureRawType(raw, values);
            }
            if (current.DeclaringType is { } enclosing && !CaptureChain(enclosing, values, seen)) return false;
        }
        return visited.Contains(type.AppContext.SystemTypes.SystemObjectType);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 19 || body[0].IP == 0 || !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            !Registers(body[1], Code.Test_rm64_r64, NativeRegister.RDX, NativeRegister.RDX) ||
            !Branch(body[2], Code.Je_rel8_64, body[17].IP) ||
            !Registers(body[3], Code.Test_rm64_r64, NativeRegister.R8, NativeRegister.R8) ||
            !Branch(body[4], Code.Je_rel8_64, body[17].IP) ||
            body[17].Code != Code.Call_rel32_64 || body[17].OpCount != 1 || body[17].Op0Kind != OpKind.NearBranch64 ||
            body[17].NearBranchTarget == 0 || body[18].Code != Code.Int3 || body[18].OpCount != 0)
            return null;

        if (TryProveCapturedFields(body) is { } captured) return captured;
        if (
            !Field(body[5], Code.Mov_r32_rm32, NativeRegister.EAX, NativeRegister.R8, out var offset) ||
            !Field(body[6], Code.Cmp_rm32_r32, NativeRegister.EAX, NativeRegister.RDX, out var firstOffset) ||
            firstOffset != offset || !Branch(body[7], Code.Jge_rel8_64, body[11].IP) ||
            body[8].Code != Code.Mov_r32_imm32 || body[8].OpCount != 2 ||
            body[8].Op0Kind != OpKind.Register || body[8].Op0Register != NativeRegister.EAX ||
            body[8].Op1Kind != OpKind.Immediate32 || body[8].Immediate32 != uint.MaxValue ||
            !X64Stack28BodyProof.Stack(body[9], Mnemonic.Add) || !Return(body[10]) ||
            !Field(body[11], Code.Mov_r32_rm32, NativeRegister.ECX, NativeRegister.R8, out var secondOffset) ||
            secondOffset != offset ||
            !(Registers(body[12], Code.Xor_r32_rm32, NativeRegister.EAX, NativeRegister.EAX) ||
              Registers(body[12], Code.Xor_rm32_r32, NativeRegister.EAX, NativeRegister.EAX)) ||
            !Field(body[13], Code.Cmp_rm32_r32, NativeRegister.ECX, NativeRegister.RDX, out var lastOffset) ||
            lastOffset != offset || body[14].Code != Code.Setg_rm8 || body[14].OpCount != 1 ||
            body[14].Op0Kind != OpKind.Register || body[14].Op0Register != NativeRegister.AL ||
            !X64Stack28BodyProof.Stack(body[15], Mnemonic.Add) || !Return(body[16]))
            return null;
        return new(offset, false, body[17]);
    }

    private static Shape? TryProveCapturedFields(IReadOnlyList<NativeInstruction> body)
    {
        if (!Field(body[5], Code.Mov_r32_rm32, NativeRegister.ECX, NativeRegister.RDX, out var offset) ||
            !Field(body[6], Code.Mov_r32_rm32, NativeRegister.EDX, NativeRegister.R8, out var secondOffset) ||
            secondOffset != offset || !CompareCaptured(body[7]) || !Branch(body[8], Code.Jl_rel8_64, body[14].IP) ||
            !(Registers(body[9], Code.Xor_r32_rm32, NativeRegister.EAX, NativeRegister.EAX) ||
              Registers(body[9], Code.Xor_rm32_r32, NativeRegister.EAX, NativeRegister.EAX)) ||
            !CompareCaptured(body[10]) || body[11].Code != Code.Setg_rm8 || body[11].OpCount != 1 ||
            body[11].Op0Kind != OpKind.Register || body[11].Op0Register != NativeRegister.AL ||
            !X64Stack28BodyProof.Stack(body[12], Mnemonic.Add) || !Return(body[13]) ||
            body[14].Code != Code.Mov_r32_imm32 || body[14].OpCount != 2 ||
            body[14].Op0Kind != OpKind.Register || body[14].Op0Register != NativeRegister.EAX ||
            body[14].Op1Kind != OpKind.Immediate32 || body[14].Immediate32 != uint.MaxValue ||
            !X64Stack28BodyProof.Stack(body[15], Mnemonic.Add) || !Return(body[16]))
            return null;
        return new(offset, true, body[17]);

        static bool CompareCaptured(NativeInstruction instruction) =>
            Registers(instruction, Code.Cmp_r32_rm32, NativeRegister.ECX, NativeRegister.EDX) ||
            Registers(instruction, Code.Cmp_rm32_r32, NativeRegister.ECX, NativeRegister.EDX);
    }

    private static bool Field(NativeInstruction instruction, Code code, NativeRegister value, NativeRegister owner,
        out int offset)
    {
        offset = 0;
        var load = code == Code.Mov_r32_rm32;
        if (instruction.Code != code || instruction.OpCount != 2 ||
            instruction.GetOpKind(load ? 0 : 1) != OpKind.Register ||
            instruction.GetOpRegister(load ? 0 : 1) != value ||
            instruction.GetOpKind(load ? 1 : 0) != OpKind.Memory || instruction.MemoryBase != owner ||
            instruction.MemoryIndex != NativeRegister.None || instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != 4 || instruction.MemoryDisplacement64 is < 16 or > int.MaxValue ||
            instruction.MemoryDisplacement64 % 4 != 0)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return true;
    }

    private static bool Registers(NativeInstruction instruction, Code code, NativeRegister first, NativeRegister second) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op0Register == first && instruction.Op1Register == second;
    private static bool Branch(NativeInstruction instruction, Code code, ulong target) => instruction.Code == code &&
        instruction.OpCount == 1 && instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;
    private static bool Return(NativeInstruction instruction) => instruction.Code == Code.Retnq && instruction.OpCount == 0;
}
