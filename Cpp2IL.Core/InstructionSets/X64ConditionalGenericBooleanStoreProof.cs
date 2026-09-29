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
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A complete nullable, fluent field store on a closed reference-generic instance.
/// The native leaf, rather than a comparison inferred from its managed signature,
/// establishes both early exits and the order and width of every write.
/// </summary>
internal static class X64ConditionalGenericBooleanStoreProof
{
    internal sealed record Shape(int ConditionOffset, int BooleanOffset,
        int? IntegerOffset, ulong End);

    internal sealed record Evidence(GenericInstanceTypeAnalysisContext Receiver,
        FieldAnalysisContext Condition, FieldAnalysisContext BooleanTarget,
        FieldAnalysisContext? IntegerTarget, FieldAnalysisContext? AggregateValue);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryCaller(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                !OriginalSignature(method, out var receiver, out var aggregateValue) ||
                !OriginalAbi(method) ||
                ReadBody(method, pe, unwind) is not { } body ||
                TryProveShape(body) is not { } shape ||
                !HasUnchangedConstructedLayout(receiver) ||
                BindField(receiver, shape.ConditionOffset,
                    Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, writable: false,
                    method) is not { } condition ||
                BindField(receiver, shape.BooleanOffset,
                    Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, writable: true,
                    method) is not { } booleanTarget ||
                ReferenceEquals(condition, booleanTarget))
                return null;

            FieldAnalysisContext? integerTarget = null;
            if (shape.IntegerOffset is { } integerOffset)
            {
                integerTarget = BindField(receiver, integerOffset,
                    Il2CppTypeEnum.IL2CPP_TYPE_I4, writable: true, method);
                if (integerTarget == null || aggregateValue == null ||
                    ReferenceEquals(integerTarget, condition) ||
                    ReferenceEquals(integerTarget, booleanTarget))
                    return null;
            }
            else if (aggregateValue != null)
                return null;

            return new Evidence(receiver, condition, booleanTarget,
                integerTarget, aggregateValue);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or
            OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    // Only the exact-target-validated return-copy-first ordering is admitted.
    // A return copy at the terminal needs its own controlled native build.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (7 or 8) ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        var pair = body.Count == 8;
        var terminalIndex = body.Count - 1;
        var terminal = body[terminalIndex];
        if (!ReturnCopy(body[0]) ||
            body[^1].Code != Code.Retnq || body[^1].OpCount != 0 ||
            body[^1].FlowControl != FlowControl.Return ||
            !SelfTest(body[1], NativeRegister.RCX) ||
            !ZeroBranch(body[2], terminal.IP) ||
            !ByteZeroCompare(body[3], out var conditionOffset) ||
            !ZeroBranch(body[4], terminal.IP))
            return null;

        var firstStoreIndex = 5;
        int? integerOffset = null;
        if (pair)
        {
            if (!Store(body[firstStoreIndex], 4, NativeRegister.EDX,
                    out var offset))
                return null;
            integerOffset = offset;
            firstStoreIndex++;
        }
        if (firstStoreIndex + 1 != terminalIndex ||
            !Store(body[firstStoreIndex], 1,
                pair ? NativeRegister.R8L : NativeRegister.DL,
                out var booleanOffset) ||
            conditionOffset == booleanOffset ||
            integerOffset is { } written &&
            (conditionOffset >= written && conditionOffset < written + 4 ||
             booleanOffset >= written && booleanOffset < written + 4))
            return null;

        return new Shape(conditionOffset, booleanOffset, integerOffset,
            body[^1].NextIP);
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind)
    {
        method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        foreach (var count in new[] { 7, 8 })
        {
            if (X64NativeInstructionReader.Read(pe, unwind, start, count, 64)
                is not { } read)
                continue;
            var body = read.ToArray();
            if (TryProveShape(body) is not { } shape ||
                shape.End <= start || shape.End - start > 64 ||
                unwind.ClassifySpan(start, shape.End) is not
                    { Kind: X64UnwindProof.SpanKind.NoEntry,
                        Start: var provedStart, End: var provedEnd } ||
                provedStart != start || provedEnd != shape.End ||
                method.AppContext.MethodsByAddress.Keys.Any(address =>
                    address > start && address < shape.End) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null)
                continue;
            var length = checked((int)(shape.End - start));
            var rawOffset = pe.MapVirtualAddressToRaw(start, false);
            var image = pe.GetRawBinaryContent();
            if (rawOffset < 0 || rawOffset > image.Length - length ||
                method.RawBytes.Length < length)
                continue;
            var original = image.Slice(checked((int)rawOffset), length);
            if (!method.RawBytes.AsSpan().Slice(0, length).SequenceEqual(original) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, original, start) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, start,
                    checked((uint)length)))
                continue;
            return body;
        }
        return null;
    }

    private static bool OrdinaryCaller(MethodAnalysisContext method)
    {
        var owner = method.DeclaringType;
        return method.IsStatic && !method.IsVirtual && !method.IsVoid &&
               method.Name is not (".ctor" or ".cctor") &&
               method.Name == method.DefaultName &&
               method.GenericParameters.Count == 0 &&
               method.BaseMethod == null && method.Overrides.Count == 0 &&
               method.OverrideReturnType == null &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract |
                   MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                   MethodImplAttributes.ManagedMask |
                   MethodImplAttributes.InternalCall)) == 0 &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
               owner is { Definition: { GenericContainer: null,
                   PackingSizeIsDefault: true,
                   ClassSizeIsDefault: true,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 } } } &&
               owner.GenericParameters.Count == 0 && !owner.IsGenericInstance &&
               owner.Name == owner.DefaultName &&
               owner.Namespace == owner.DefaultNamespace &&
               owner.Attributes == owner.DefaultAttributes &&
               ReferenceEquals(owner.BaseType, owner.DefaultBaseType) &&
               method.Definition is { GenericContainer: null } definition &&
               ReferenceEquals(definition.DeclaringType, owner.Definition);
    }

    private static bool OriginalSignature(MethodAnalysisContext method,
        out GenericInstanceTypeAnalysisContext receiver,
        out FieldAnalysisContext? aggregateValue)
    {
        receiver = null!;
        aggregateValue = null;
        var app = method.AppContext;
        if (method.Definition is not { parameterCount: 2 or 3,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } rawReturn } definition ||
            method.Parameters.Count != definition.parameterCount ||
            definition.InternalParameterData?.Length != definition.parameterCount ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !ReferenceEquals(method.ReturnType.Definition,
                rawReturn.AsClass()) ||
            !ReferenceEquals(app.ResolveIl2CppType(rawReturn), method.ReturnType) ||
            method.ReturnType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } returnType ||
            method.Parameters is not [var first, ..] ||
            !OriginalParameter(method, first, 0,
                Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST) ||
            first.ParameterType is not GenericInstanceTypeAnalysisContext
                { HasUnchangedOriginalRawType: true } constructed ||
            !ReferenceEquals(constructed.OriginalRawType,
                first.Definition!.RawType) ||
            constructed.IsValueType || constructed.GenericType.IsInterface ||
            constructed.GenericType.IsValueType ||
            constructed.GenericType.Definition is not { HasCctor: false } ||
            !UnchangedReturnBase(constructed.GenericType, returnType))
            return false;
        receiver = constructed;

        if (definition.parameterCount == 2)
            return OriginalParameter(method, method.Parameters[1], 1,
                       Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) &&
                   ReferenceEquals(method.Parameters[1].ParameterType,
                       app.SystemTypes.SystemBooleanType);

        var value = method.Parameters[1];
        var boolean = method.Parameters[2];
        if (!OriginalParameter(method, value, 1,
                Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE) ||
            !OriginalParameter(method, boolean, 2,
                Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN) ||
            !ReferenceEquals(boolean.ParameterType,
                app.SystemTypes.SystemBooleanType) ||
            BindAggregateValue(value.ParameterType, method) is not { } field)
            return false;
        aggregateValue = field;
        return true;
    }

    private static bool OriginalParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, int index,
        Il2CppTypeEnum kind) =>
        parameter.ParameterIndex == index &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition,
            method.Definition?.InternalParameterData?[index]) &&
        parameter.Name == parameter.DefaultName &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideAttributes == null &&
        parameter.OverrideParameterType == null &&
        !parameter.UseOverrideDefaultValue && !parameter.IsRef &&
        ReferenceEquals(parameter.ParameterType,
            parameter.DefaultParameterType) &&
        parameter.Definition?.RawType is { NumMods: 0, Byref: 0,
            Pinned: 0 } raw && raw.Type == kind;

    private static bool UnchangedReturnBase(TypeAnalysisContext genericDefinition,
        TypeAnalysisContext returnType)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var type = genericDefinition.BaseType; type != null;
            type = type.BaseType)
        {
            if (!seen.Add(type) || type.IsValueType || type.IsInterface ||
                type.IsGenericInstance || type.GenericParameters.Count != 0 ||
                type.Attributes != type.DefaultAttributes ||
                !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
                type.Definition is not { PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true } ||
                type.Fields.Any(field => !field.IsStatic))
                return false;
            if (ReferenceEquals(type, returnType))
                return true;
        }
        return false;
    }

    private static bool HasUnchangedConstructedLayout(
        GenericInstanceTypeAnalysisContext receiver)
    {
        var definition = receiver.GenericType;
        if (definition.Definition is not { HasCctor: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            definition.Attributes != definition.DefaultAttributes ||
            (definition.Attributes & TypeAttributes.LayoutMask) ==
                TypeAttributes.ExplicitLayout ||
            definition.Name != definition.DefaultName ||
            definition.Namespace != definition.DefaultNamespace ||
            definition.Fields.Count == 0 ||
            definition.Definition is not { IsImportOrWindowsRuntime: false,
                HasInlineArray: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } ||
            definition.Definition.Fields?.Length != definition.Fields.Count ||
            !definition.Fields.Select(field => field.BackingData?.Field)
                .SequenceEqual(definition.Definition.Fields) ||
            definition.Fields.Any(field => field.IsStatic ||
                field.Name != field.DefaultName ||
                field.Attributes != field.DefaultAttributes ||
                field.Offset != field.DefaultOffset ||
                field.OverrideFieldType != null ||
                field.BackingData?.Field.RawFieldType is not
                    { NumMods: 0, Byref: 0, Pinned: 0 } ||
                !HasCanonicalFieldStorage(field, receiver)))
            return false;
        return true;
    }

    // The shared generic layout helper computes primitive sizes by full name.
    // Before using its projected offsets here, bind every field to the canonical
    // primitive context or an original reference/VAR descriptor. A user-defined
    // value type with a framework-looking name cannot supply a storage size.
    private static bool HasCanonicalFieldStorage(FieldAnalysisContext field,
        GenericInstanceTypeAnalysisContext receiver)
    {
        var raw = field.BackingData?.Field.RawFieldType;
        if (raw == null)
            return false;
        var app = receiver.AppContext;
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VAR)
            return field.FieldType is GenericParameterTypeAnalysisContext
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_VAR } parameter &&
                   ReferenceEquals(parameter.Owner, receiver.GenericType) &&
                   receiver.GenericArguments.All(argument => !argument.IsValueType);
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS)
            return !field.FieldType.IsValueType &&
                   !raw.AsClass().IsValueType &&
                   ReferenceEquals(app.ResolveIl2CppType(raw), field.FieldType);
        if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
            Il2CppTypeEnum.IL2CPP_TYPE_STRING)
            return ReferenceEquals(field.FieldType,
                app.SystemTypes.GetPrimitive(raw.Type));
        if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
            Il2CppTypeEnum.IL2CPP_TYPE_I1 or
            Il2CppTypeEnum.IL2CPP_TYPE_U1 or
            Il2CppTypeEnum.IL2CPP_TYPE_I2 or
            Il2CppTypeEnum.IL2CPP_TYPE_U2 or
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or
            Il2CppTypeEnum.IL2CPP_TYPE_U4 or
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or
            Il2CppTypeEnum.IL2CPP_TYPE_U8 or
            Il2CppTypeEnum.IL2CPP_TYPE_R4 or
            Il2CppTypeEnum.IL2CPP_TYPE_R8 or
            Il2CppTypeEnum.IL2CPP_TYPE_I or
            Il2CppTypeEnum.IL2CPP_TYPE_U)
            return ReferenceEquals(field.FieldType,
                app.SystemTypes.GetPrimitive(raw.Type));
        return false;
    }

    private static FieldAnalysisContext? BindField(
        GenericInstanceTypeAnalysisContext receiver, int offset,
        Il2CppTypeEnum kind, bool writable, MethodAnalysisContext method)
    {
        var definition = receiver.GenericType;
        var field = GenericInstanceFieldLayout.FindFieldAtOffset(receiver,
            offset);
        var expected = kind == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN
            ? method.AppContext.SystemTypes.SystemBooleanType
            : method.AppContext.SystemTypes.SystemInt32Type;
        if (field == null || !ReferenceEquals(field.DeclaringType,
                definition) || !ReferenceEquals(field.FieldType, expected) ||
            field.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            raw.Type != kind || field.Name != field.DefaultName ||
            field.Attributes != field.DefaultAttributes ||
            field.Offset != field.DefaultOffset ||
            field.IsStatic || field.OverrideFieldType != null ||
            (field.Attributes & (FieldAttributes.Literal |
                FieldAttributes.HasFieldMarshal |
                FieldAttributes.HasFieldRVA |
                FieldAttributes.HasDefault)) != 0 ||
            writable && (field.Attributes & FieldAttributes.InitOnly) != 0 ||
            // The recovery method is separate from the generic owner, so a
            // direct field access must be verifiable and source-accessible.
            field.Visibility != FieldAttributes.Public ||
            !ReferenceEquals(field.BackingData.Field.DeclaringType,
                definition.Definition) ||
            definition.Fields.Count(candidate =>
                ReferenceEquals(candidate, field)) != 1)
            return null;
        return field;
    }

    private static FieldAnalysisContext? BindAggregateValue(
        TypeAnalysisContext aggregate, MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (aggregate.IsGenericInstance || aggregate.IsEnumType ||
            aggregate.GenericParameters.Count != 0 ||
            aggregate.Attributes != aggregate.DefaultAttributes ||
            aggregate.Name != aggregate.DefaultName ||
            aggregate.Namespace != aggregate.DefaultNamespace ||
            !ReferenceEquals(aggregate.BaseType,
                app.SystemTypes.SystemValueTypeType) ||
            aggregate.Definition is not { IsValueType: true,
                IsEnumType: false, IsBlittable: true,
                IsImportOrWindowsRuntime: false,
                IsByRefLike: false, HasCctor: false,
                PackingSizeIsDefault: true,
                ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                RawSizes.native_size: 4 } ||
            (aggregate.Attributes & TypeAttributes.LayoutMask) !=
                TypeAttributes.SequentialLayout ||
            TypeSizes.UnboxedSize(aggregate, 8) != 4 ||
            aggregate.Fields.Where(field => !field.IsStatic).ToArray()
                is not [var value] ||
            value.Offset != 0 || value.Offset != value.DefaultOffset ||
            value.Name != value.DefaultName ||
            value.Attributes != value.DefaultAttributes ||
            value.Visibility != FieldAttributes.Public ||
            value.OverrideFieldType != null ||
            !ReferenceEquals(value.DeclaringType, aggregate) ||
            !ReferenceEquals(value.BackingData?.Field.DeclaringType,
                aggregate.Definition) ||
            !ReferenceEquals(value.FieldType,
                app.SystemTypes.SystemInt32Type) ||
            value.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 })
            return null;
        return value;
    }

    private static bool OriginalAbi(MethodAnalysisContext method)
    {
        var resolver = new X64CallingConventionResolver();
        var abi = resolver.ResolveForParameters(method);
        var expected = method.Parameters.Count == 2
            ? new[] { "rcx", "rdx", "r8" }
            : new[] { "rcx", "rdx", "r8", "r9" };
        return !resolver.ReturnsViaHiddenBuffer(method) &&
               abi.Length == expected.Length &&
               abi.Select((operand, index) => operand is IsilRegister register &&
                   register == new IsilRegister(null, expected[index])).All(valid => valid);
    }

    private static bool ReturnCopy(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.RCX &&
        instruction.FlowControl == FlowControl.Next;

    private static bool SelfTest(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Code == Code.Test_rm64_r64 &&
        instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register &&
        instruction.FlowControl == FlowControl.Next;

    private static bool ZeroBranch(NativeInstruction instruction,
        ulong target) =>
        instruction.Code is Code.Je_rel8_64 or Code.Je_rel32_64 &&
        instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target &&
        instruction.FlowControl == FlowControl.ConditionalBranch;

    private static bool ByteZeroCompare(NativeInstruction instruction,
        out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Cmp_rm8_imm8 ||
            instruction.OpCount != 2 ||
            instruction.Op0Kind != OpKind.Memory ||
            instruction.Op1Kind != OpKind.Immediate8 ||
            instruction.Immediate8 != 0 ||
            !InstanceMemory(instruction, 1, out offset) ||
            instruction.FlowControl != FlowControl.Next)
            return false;
        return true;
    }

    private static bool Store(NativeInstruction instruction, int width,
        NativeRegister source, out int offset)
    {
        offset = 0;
        var code = width == 1 ? Code.Mov_rm8_r8 :
            Code.Mov_rm32_r32;
        return instruction.Code == code &&
               instruction.OpCount == 2 &&
               instruction.Op0Kind == OpKind.Memory &&
               instruction.Op1Kind == OpKind.Register &&
               instruction.Op1Register == source &&
               InstanceMemory(instruction, width, out offset) &&
               instruction.FlowControl == FlowControl.Next;
    }

    private static bool InstanceMemory(NativeInstruction instruction,
        int width, out int offset)
    {
        offset = 0;
        if (instruction.MemoryBase != NativeRegister.RCX ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryIndexScale != 1 ||
            instruction.MemorySize.GetSize() != width ||
            instruction.MemoryDisplacement64 is < 16 or > 4096)
            return false;
        offset = checked((int)instruction.MemoryDisplacement64);
        return true;
    }
}
