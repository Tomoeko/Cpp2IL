using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a closed null diamond whose scalar read has one original accessible
/// property getter. The call preserves behavior and visibility; optimized bytes
/// do not establish the original source call boundary.
/// </summary>
internal static class X64GuardedScalarAccessorProof
{
    internal const string EvidenceKey = "X64GuardedScalarAccessorProof";
    internal const string CaptureRegister = "guarded_scalar_accessor_receiver";
    internal const string ResultRegister = "guarded_scalar_accessor_result";
    internal sealed record GetterShape(NativeInstruction Load, NativeInstruction Return, int Offset, int Width);
    internal sealed record CallerShape(NativeInstruction Capture, NativeInstruction Load,
        NativeInstruction Return, NativeInstruction NullCall, int SourceOffset, int ValueOffset, int Width);
    internal sealed class InputState(List<object> metadata, byte[] caller, byte[] getter)
    {
        private readonly object[] _metadata = metadata.ToArray();
        private readonly byte[] _caller = caller;
        private readonly byte[] _getter = getter;
        internal bool Matches(InputState other) => _metadata.SequenceEqual(other._metadata) &&
            _caller.SequenceEqual(other._caller) && _getter.SequenceEqual(other._getter);
    }
    internal sealed record Proof(CallerShape? Caller, GetterShape GetterNative,
        FieldAnalysisContext? Source, FieldAnalysisContext Value, PropertyAnalysisContext Property,
        MethodAnalysisContext Getter, InputState Input)
    {
        internal bool IsDirect => Caller == null;
        internal bool Matches(Proof other) => Caller == other.Caller && GetterNative == other.GetterNative &&
            ReferenceEquals(Source, other.Source) && ReferenceEquals(Value, other.Value) &&
            ReferenceEquals(Property, other.Property) && ReferenceEquals(Getter, other.Getter) && Input.Matches(other.Input);
    }
    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);
    internal static bool WasLifted(MethodAnalysisContext method) => NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var receiver = new IsilRegister(null, "rcx");
        var result = new IsilRegister(null, ResultRegister);
        if (proof.IsDirect)
            return
            [
                new(0, OpCode.Move, result, new ISIL.MemoryOperand(receiver, null, proof.Value.Offset))
                    { NativeAddress = proof.GetterNative.Load.IP },
                new(1, OpCode.Return, result) { NativeAddress = proof.GetterNative.Return.IP }
            ];
        var capture = new IsilRegister(null, CaptureRegister);
        return
        [
            new(0, OpCode.Move, capture, new ISIL.MemoryOperand(receiver, null, proof.Source!.Offset))
                { NativeAddress = proof.Caller!.Capture.IP },
            new(1, OpCode.Call, proof.Getter, result, capture, new Immediate(0))
                { NativeAddress = proof.Caller.Load.IP, CallSemantics = CallSemantics.NullCheckedInstance },
            new(2, OpCode.Return, result) { NativeAddress = proof.Caller.Return.IP }
        ];
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext: { Binary: PE } app, DeclaringType: { } owner } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app))
            return null;
        try
        {
            if (!OrdinaryHierarchy(owner) || !OriginalMethod(method) || !OriginalAbi(method))
                return null;
            if (Getter(method) is { } direct)
                return Make(method, null, null, direct.Field, direct.Property, method, direct.Shape);
            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                TryProveCallerShape(body) is not { } shape ||
                X64UnwindProof.ForApplication(app) is not { } index ||
                !index.MatchesUnwind(method.UnderlyingPointer, body[^1].NextIP, 4, 0, [4, 0x42]) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullCall.NearBranchTarget) == null ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { shape.NullCall.IP }) != null)
                return null;
            var sources = HierarchyFields(owner).Where(field => !field.IsStatic &&
                field.Offset == shape.SourceOffset).ToArray();
            if (sources is not [var source] || !Accessible(source, owner) ||
                source.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } || !OrdinaryHierarchy(source.FieldType) ||
                !ReferenceEquals(source.FieldType, source.DefaultFieldType) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(source,
                    new LocalVariable("proved-accessor-owner", new IsilRegister(null, "rcx"), owner), source.Offset)) ||
                !Contained(source, 8))
                return null;
            var candidates = source.FieldType.Methods.Select(Getter).Where(candidate => candidate != null &&
                candidate.Shape.Offset == shape.ValueOffset && candidate.Shape.Width == shape.Width &&
                ReferenceEquals(candidate.Field.FieldType, method.ReturnType)).ToArray();
            if (candidates is not [{ } target] || !AccessibleType(target.Property.DeclaringType, owner))
                return null;
            return Make(method, shape, source, target.Field, target.Property,
                target.Property.Getter!, target.Shape);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or NullReferenceException or EndOfStreamException or KeyNotFoundException)
        {
            return null;
        }
    }

    private sealed record GetterBinding(GetterShape Shape, FieldAnalysisContext Field, PropertyAnalysisContext Property);
    private static GetterBinding? Getter(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { } owner || !OrdinaryHierarchy(owner) ||
            !OriginalMethod(method) || method.Visibility != MethodAttributes.Public ||
            (method.Attributes & MethodAttributes.SpecialName) == 0 ||
            method.DeclaringType!.Properties.Where(property => ReferenceEquals(property.Getter, method)).ToArray()
                is not [var property] || property.Definition is not { } definition ||
            !ReferenceEquals(definition.DeclaringType, method.DeclaringType.Definition) ||
            !ReferenceEquals(definition.Getter, method.Definition) ||
            !ReferenceEquals(definition.Setter, property.Setter?.Definition) ||
            property.Name != property.DefaultName || property.Attributes != property.DefaultAttributes ||
            property.OverridePropertyType != null || !ReferenceEquals(property.PropertyType, method.ReturnType) ||
            property.IsStatic || property.IsVirtual ||
            (X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) ??
             X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32)) is not { } body ||
            TryProveGetterShape(body) is not { } shape ||
            method.DeclaringType.Fields.Where(field => !field.IsStatic && field.Offset == shape.Offset).ToArray()
                is not [var field] || !ScalarField(field, shape.Width) ||
            !ReferenceEquals(field.FieldType, method.ReturnType))
            return null;
        return new GetterBinding(shape, field, property);
    }

    private static Proof Make(MethodAnalysisContext method, CallerShape? caller, FieldAnalysisContext? source,
        FieldAnalysisContext value, PropertyAnalysisContext property, MethodAnalysisContext getter, GetterShape native)
    {
        var metadata = new List<object>();
        foreach (var type in Hierarchy(method.DeclaringType!).Concat(Hierarchy(getter.DeclaringType!))
                     .Concat(value.FieldType.IsEnumType && value.FieldType.DeclaringType != null
                         ? Hierarchy(value.FieldType.DeclaringType) : Enumerable.Empty<TypeAnalysisContext>()).Distinct())
            CaptureType(type, metadata);
        if (value.FieldType.IsEnumType)
            CaptureType(value.FieldType, metadata);
        CaptureMethod(method, metadata);
        CaptureMethod(getter, metadata);
        metadata.Add(property);
        metadata.Add(property.Name);
        metadata.Add(property.Attributes);
        metadata.Add(property.PropertyType);
        metadata.Add(property.Getter!);
        metadata.Add(property.Setter ?? (object)false);
        var definition = property.Definition!;
        metadata.Add(definition.nameIndex);
        metadata.Add(definition.get.Value);
        metadata.Add(definition.set.Value);
        metadata.Add(definition.attrs);
        metadata.Add(definition.token);
        var callerLength = caller == null ? 0 : checked((int)(caller.NullCall.NextIP - method.UnderlyingPointer));
        var getterLength = checked((int)(native.Return.NextIP - getter.UnderlyingPointer));
        return new Proof(caller, native, source, value, property, getter,
            new InputState(metadata, method.RawBytes.AsSpan().Slice(0, callerLength).ToArray(),
                getter.RawBytes.AsSpan().Slice(0, getterLength).ToArray()));
    }

    private static bool OriginalMethod(MethodAnalysisContext method) =>
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I4 or
                Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                NumMods: 0, Byref: 0, Pinned: 0, Data: not null } raw } definition &&
        ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 && method.Parameters.Count == 0 &&
        method.GenericParameters.Count == 0 && !method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                 MethodImplAttributes.InternalCall)) == 0 &&
        method.BaseMethod == null && method.Overrides.Count == 0 && method.OverrideReturnType == null &&
        ReferenceEquals(method.ReturnType, method.DefaultReturnType) && raw.Type == (method.ReturnType.IsEnumType ? Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE : method.ReturnType.Type) &&
        ScalarType(method.ReturnType) != 0 && OriginalAbi(method) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false);

    private static bool OriginalAbi(MethodAnalysisContext method)
    {
        var resolver = new X64CallingConventionResolver();
        return !resolver.ReturnsViaHiddenBuffer(method) && resolver.ResolveForParameters(method) is
            [IsilRegister { Name: "rcx", Version: -1 } receiver, IsilRegister { Name: "rdx", Version: -1 } info] &&
            receiver == new IsilRegister(null, "rcx") && info == new IsilRegister(null, "rdx");
    }

    private static int ScalarType(TypeAnalysisContext type)
    {
        var system = type.AppContext.SystemTypes;
        if (ReferenceEquals(type, system.SystemBooleanType) && type.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN)
            return 8;
        if (ReferenceEquals(type, system.SystemInt32Type) && type.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
            ReferenceEquals(type, system.SystemUInt32Type) && type.Type == Il2CppTypeEnum.IL2CPP_TYPE_U4)
            return 32;
        return Enum32StorageProof.IsUnchanged(type) && type.Definition is { HasCctor: false } &&
               OriginalEnumContainingType(type) &&
               !type.Methods.Any(method => method.Name == ".cctor") &&
               type.Fields.Count == type.Definition.FieldCount && type.Fields.All(field =>
                   field.BackingData?.Field.RawFieldType is { NumMods: 0, Byref: 0, Pinned: 0 } &&
                   field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
                   field.OverrideFieldType == null && !field.UseOverrideConstantValue &&
                   field.OverrideStaticArrayInitialValue == null) ? 32 : 0;
    }

    private static bool OriginalEnumContainingType(TypeAnalysisContext type) =>
        ReferenceEquals(type.DeclaringType?.Definition, type.Definition!.DeclaringType) &&
        (type.DeclaringType == null
            ? type.Definition.DeclaringTypeIndex.IsNull
            : (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.NestedPublic &&
              OrdinaryHierarchy(type.DeclaringType));

    private static bool ScalarField(FieldAnalysisContext field, int width)
    {
        if (field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I4 or
                    Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE,
                    NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            ScalarType(field.FieldType) != width || !Contained(field, width / 8) ||
            raw.Type != (field.FieldType.IsEnumType ? Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE : field.FieldType.Type))
            return false;
        var receiver = new LocalVariable("proved-accessor-owner", new IsilRegister(null, "rcx"), field.DeclaringType);
        return field.FieldType.IsEnumType
            ? NarrowFieldEqualityProof.HasUnchangedEnum32FieldLayout(new FieldReference(field, receiver, field.Offset))
            : NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field, receiver, field.Offset), width);
    }

    private static bool Contained(FieldAnalysisContext field, int size) => field.Offset >= 16 &&
        field.DeclaringType.Definition is { } definition &&
        (ulong)field.Offset + (ulong)size <= definition.RawSizes.instance_size;
    private static bool Accessible(FieldAnalysisContext field, TypeAnalysisContext caller) =>
        field.Visibility == FieldAttributes.Public || ReferenceEquals(field.DeclaringType, caller) ||
        field.Visibility is FieldAttributes.Family or FieldAttributes.FamORAssem &&
            Hierarchy(caller).Contains(field.DeclaringType) ||
        field.Visibility is FieldAttributes.Assembly or FieldAttributes.FamANDAssem &&
            ReferenceEquals(field.DeclaringType.DeclaringAssembly, caller.DeclaringAssembly) &&
            (field.Visibility == FieldAttributes.Assembly || Hierarchy(caller).Contains(field.DeclaringType));

    private static bool AccessibleType(TypeAnalysisContext target, TypeAnalysisContext caller) =>
        target.DeclaringType == null &&
        ((target.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public ||
         (target.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.NotPublic &&
         ReferenceEquals(target.DeclaringAssembly, caller.DeclaringAssembly));

    private static IEnumerable<TypeAnalysisContext> Hierarchy(TypeAnalysisContext type)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null && seen.Add(current);)
        {
            yield return current;
            if (current.Definition is not { } definition ||
                definition.RawBaseType is { } rawBase && !ReferenceDescriptor(rawBase))
                yield break;
            current = current.BaseType;
        }
    }
    private static bool ReferenceDescriptor(Il2CppType raw) => raw is
        { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
            NumMods: 0, Byref: 0, Pinned: 0, Data: not null };
    private static IEnumerable<FieldAnalysisContext> HierarchyFields(TypeAnalysisContext type) =>
        Hierarchy(type).SelectMany(parent => parent.Fields);
    private static bool OrdinaryHierarchy(TypeAnalysisContext type)
    {
        var hierarchy = Hierarchy(type).ToArray();
        return hierarchy.Length > 0 && ReferenceEquals(hierarchy[^1], type.AppContext.SystemTypes.SystemObjectType) &&
            hierarchy.All(parent => parent.Definition is { GenericContainer: null, HasCctor: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } raw } definition &&
                parent.DeclaringType == null && definition.DeclaringTypeIndex.IsNull &&
                raw.Type == (ReferenceEquals(parent, type.AppContext.SystemTypes.SystemObjectType)
                    ? Il2CppTypeEnum.IL2CPP_TYPE_OBJECT : Il2CppTypeEnum.IL2CPP_TYPE_CLASS) &&
                !parent.IsValueType && !parent.IsInterface && !parent.IsGenericInstance &&
                parent.GenericParameters.Count == 0 && parent.Name == parent.DefaultName &&
                parent.Namespace == parent.DefaultNamespace && parent.Attributes == parent.DefaultAttributes &&
                (parent.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout &&
                (definition.RawBaseType is not { } rawBase || ReferenceDescriptor(rawBase)) &&
                ReferenceEquals(parent.BaseType, parent.DefaultBaseType) && !parent.Methods.Any(method => method.Name == ".cctor") &&
                parent.Fields.Count == definition.FieldCount && parent.Methods.Count == definition.MethodCount &&
                parent.Fields.All(field => field.BackingData?.Field.RawFieldType is
                    { NumMods: 0, Byref: 0, Pinned: 0, Data: not null } &&
                    ReferenceEquals(field.BackingData.Field.DeclaringType, definition) && ReferenceEquals(field.DeclaringType, parent) &&
                    field.Name == field.DefaultName &&
                    field.Attributes == field.DefaultAttributes && field.Offset == field.DefaultOffset &&
                    field.OverrideFieldType == null && !field.UseOverrideConstantValue &&
                    field.OverrideStaticArrayInitialValue == null && (field.Attributes & FieldAttributes.HasFieldMarshal) == 0 &&
                    HasSupportedDescriptor(field)) &&
                parent.Methods.All(method => method.Definition != null));
    }

    private static bool HasSupportedDescriptor(FieldAnalysisContext field)
    {
        var raw = field.BackingData!.Field.RawFieldType!;
        if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or
            Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
            CaptureRaw(raw, [], field.DeclaringType.AppContext);
        return true;
    }

    internal static GetterShape? TryProveGetterShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (2 or 3) || !Plain(body) || body.Count == 3 && body[0] is not
                { Code: Code.Nopw or Code.Nopd, OpCount: 0, FlowControl: FlowControl.Next } ||
            ReadWidth(body[^2], NativeRegister.RCX) is not (8 or 32) || body[^1].Code != Code.Retnq || body[^1].OpCount != 0)
            return null;
        return new GetterShape(body[^2], body[^1], (int)body[^2].MemoryDisplacement64,
            ReadWidth(body[^2], NativeRegister.RCX));
    }
    internal static CallerShape? TryProveCallerShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count < 8 || body.Count > 40 || !Plain(body) ||
            body.Skip(8).Any(instruction => instruction.Code != Code.Int3 || instruction.Length != 1) ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) || body[0].Length != 4 ||
            body[1] is not { Code: Code.Mov_r64_rm64, OpCount: 2, Op0Kind: OpKind.Register,
                Op0Register: NativeRegister.RAX, Op1Kind: OpKind.Memory, MemoryBase: NativeRegister.RCX,
                MemoryIndex: NativeRegister.None } || body[1].MemorySize.GetSize() != 8 ||
            body[1].MemoryDisplacement64 is < 16 or > int.MaxValue ||
            body[2] is not { Code: Code.Test_rm64_r64, OpCount: 2, Op0Kind: OpKind.Register,
                Op1Kind: OpKind.Register, Op0Register: NativeRegister.RAX, Op1Register: NativeRegister.RAX } ||
            body[3] is not { Mnemonic: Mnemonic.Je, OpCount: 1, Op0Kind: OpKind.NearBranch64 } ||
            body[3].NearBranchTarget != body[7].IP || ReadWidth(body[4], NativeRegister.RAX) is not (8 or 32) ||
            !X64Stack28BodyProof.Stack(body[5], Mnemonic.Add) || body[6].Code != Code.Retnq || body[6].OpCount != 0 ||
            body[7] is not { Code: Code.Call_rel32_64, OpCount: 1, Op0Kind: OpKind.NearBranch64 } ||
            body[7].NearBranchTarget == 0)
            return null;
        return new CallerShape(body[1], body[4], body[6], body[7], (int)body[1].MemoryDisplacement64,
            (int)body[4].MemoryDisplacement64, ReadWidth(body[4], NativeRegister.RAX));
    }
    private static int ReadWidth(NativeInstruction instruction, NativeRegister receiver) =>
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == receiver &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue
            ? instruction.Code == Code.Movzx_r32_rm8 && instruction.MemorySize.GetSize() == 1 ? 8 :
              instruction.Code == Code.Mov_r32_rm32 && instruction.MemorySize.GetSize() == 4 ? 32 : 0 : 0;
    private static bool Plain(IReadOnlyList<NativeInstruction> body) => body.All(instruction => !instruction.IsInvalid &&
        instruction.CodeSize == CodeSize.Code64 && !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == NativeRegister.None) &&
        !body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any();

    private static void CaptureRaw(Il2CppType raw, List<object> values, ApplicationAnalysisContext app, int depth = 0)
    {
        if (depth > 16 || depth > 0 && (raw.NumMods != 0 || raw.Byref != 0 || raw.Pinned != 0))
            throw new InvalidOperationException("Cyclic or excessively nested metadata type descriptor.");
        values.Add(raw.Bits); values.Add(raw.Datapoint); values.Add(raw.Data.Dummy); values.Add(raw.Attrs);
        values.Add(raw.Type); values.Add(raw.NumMods); values.Add(raw.Byref); values.Add(raw.Pinned); values.Add(raw.ValueType);
        if (raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_PTR)
            CapturePointedType(raw.Data.Type);
        else if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_ARRAY)
        {
            if (!HasFileBackedData(app, raw.Data.Array, 32))
                throw new InvalidOperationException("Unbound array metadata descriptor.");
            var array = raw.GetArrayType();
            if (array.rank is 0 or > 32 || array.numsizes > array.rank || array.numlobounds > array.rank)
                throw new InvalidOperationException("Unsupported array metadata shape.");
            if (!HasFileBackedData(app, array.sizes, (uint)array.numsizes * 4) ||
                !HasFileBackedData(app, array.lobounds, (uint)array.numlobounds * 4))
                throw new InvalidOperationException("Unbound array metadata dimensions.");
            var sizes = array.numsizes == 0 ? Array.Empty<int>() :
                app.Binary.ReadClassArrayAtVirtualAddress<int>(array.sizes, array.numsizes);
            var bounds = array.numlobounds == 0 ? Array.Empty<int>() :
                app.Binary.ReadClassArrayAtVirtualAddress<int>(array.lobounds, array.numlobounds);
            if (!TryCaptureArrayShape(array, sizes, bounds, values))
                throw new InvalidOperationException("Incomplete array metadata dimensions.");
            CapturePointedType(array.etype);
        }
        else if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
        {
            if (!HasFileBackedData(app, raw.Data.GenericClass, 32))
                throw new InvalidOperationException("Unbound generic metadata descriptor.");
            var generic = raw.GetGenericClass();
            values.Add(generic.TypeDefinitionIndex); values.Add(generic.V27TypePointer); values.Add(generic.CachedClass);
            values.Add(generic.Context.class_inst); values.Add(generic.Context.method_inst);
            if (generic.V27TypePointer != 0)
                CapturePointedType(generic.V27TypePointer);
            if (!HasFileBackedData(app, generic.Context.class_inst, generic.Context.class_inst == 0 ? 0U : 16U) ||
                !HasFileBackedData(app, generic.Context.method_inst, generic.Context.method_inst == 0 ? 0U : 16U))
                throw new InvalidOperationException("Unbound generic metadata instantiation.");
            CaptureArguments(generic.Context.ClassInst);
            CaptureArguments(generic.Context.MethodInst);
        }
        return;

        void CapturePointedType(ulong pointer)
        {
            if (!HasFileBackedData(app, pointer, 16))
                throw new InvalidOperationException("Unbound nested metadata type.");
            var fresh = app.Binary.ReadReadableAtVirtualAddress<Il2CppType>(pointer);
            CaptureRaw(fresh, values, app, depth + 1);
            // Registered element descriptors are mutable analysis objects too.
            // Capture their scalar facts separately from the current player bytes.
            var cached = app.Binary.GetIl2CppTypeFromPointer(pointer);
            if (cached.Bits != fresh.Bits || cached.Datapoint != fresh.Datapoint || cached.Data.Dummy != fresh.Data.Dummy ||
                cached.Attrs != fresh.Attrs || cached.Type != fresh.Type || cached.NumMods != fresh.NumMods ||
                cached.Byref != fresh.Byref || cached.Pinned != fresh.Pinned || cached.ValueType != fresh.ValueType)
                throw new InvalidOperationException("Changed cached nested metadata descriptor.");
            values.Add(cached.Bits); values.Add(cached.Datapoint); values.Add(cached.Data.Dummy); values.Add(cached.Attrs);
            values.Add(cached.Type); values.Add(cached.NumMods); values.Add(cached.Byref); values.Add(cached.Pinned); values.Add(cached.ValueType);
        }

        void CaptureArguments(Il2CppGenericInst? instance)
        {
            values.Add(instance != null);
            if (instance == null)
                return;
            if (instance.pointerCount > 64)
                throw new InvalidOperationException("Unbounded generic metadata descriptor.");
            values.Add(instance.pointerCount); values.Add(instance.pointerStart);
            if (!HasFileBackedData(app, instance.pointerStart, checked((uint)instance.pointerCount * 8)))
                throw new InvalidOperationException("Unbound generic metadata argument table.");
            foreach (var pointer in instance.Pointers)
            {
                values.Add(pointer);
                CapturePointedType(pointer);
            }
        }
    }
    internal static bool TryCaptureArrayShape(Il2CppArrayType array, IReadOnlyList<int> sizes,
        IReadOnlyList<int> bounds, List<object> values)
    {
        if (array.rank is 0 or > 32 || array.numsizes > array.rank || array.numlobounds > array.rank ||
            sizes.Count != array.numsizes || bounds.Count != array.numlobounds)
            return false;
        values.Add(array.etype); values.Add(array.rank); values.Add(array.numsizes); values.Add(array.numlobounds);
        values.Add(array.sizes); values.Add(array.lobounds);
        foreach (var size in sizes)
            values.Add(size);
        foreach (var bound in bounds)
            values.Add(bound);
        return true;
    }

    private static bool HasFileBackedData(ApplicationAnalysisContext app, ulong address, uint length)
    {
        if (length == 0)
            return true;
        if (length > 4096 || app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
            address < index.ImageBase || address - index.ImageBase > uint.MaxValue ||
            address > ulong.MaxValue - length)
            return false;
        if (index.MapReadOnlyData(address, length) >= 0)
            return true;
        var first = pe.MapVirtualAddressToRaw(address, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - length)
            return false;
        for (uint offset = 0; offset < length; offset++)
        {
            var current = address + offset;
            if (current - index.ImageBase > uint.MaxValue ||
                !index.IsReadableFileBackedRva((uint)(current - index.ImageBase)) ||
                pe.MapVirtualAddressToRaw(current, false) != first + offset)
                return false;
        }
        return true;
    }

    private static void CaptureMethod(MethodAnalysisContext method, List<object> values)
    {
        var raw = method.Definition!;
        values.Add(method); values.Add(method.Name); values.Add(method.Attributes); values.Add(method.ImplAttributes);
        values.Add(method.ReturnType); values.Add(method.UnderlyingPointer); values.Add(raw.nameIndex); values.Add(raw.token);
        values.Add(raw.flags); values.Add(raw.iflags); values.Add(raw.slot); values.Add(raw.declaringTypeIdx);
        values.Add(raw.returnTypeIdx); values.Add(raw.parameterStart); values.Add(raw.parameterCount); values.Add(raw.genericContainerIndex);
        CaptureRaw(raw.RawReturnType!, values, method.AppContext);
    }
    private static void CaptureType(TypeAnalysisContext type, List<object> values)
    {
        var raw = type.Definition!;
        values.Add(type); values.Add(type.Name); values.Add(type.Namespace); values.Add(type.Attributes);
        values.Add(type.BaseType ?? (object)false);
        values.Add(type.DeclaringType ?? (object)false); values.Add(raw.DeclaringTypeIndex);
        values.Add(raw.NameIndex); values.Add(raw.NamespaceIndex); values.Add(raw.Token);
        values.Add(raw.Flags); values.Add(raw.Bitfield); values.Add(raw.ByvalTypeIndex); values.Add(raw.ParentIndex);
        values.Add(raw.GenericContainerIndex); values.Add(raw.FirstFieldIdx); values.Add(raw.FieldCount);
        values.Add(raw.FirstMethodIdx); values.Add(raw.MethodCount); values.Add(raw.FirstPropertyId); values.Add(raw.PropertyCount);
        values.Add(raw.RawSizes.instance_size); values.Add(raw.RawSizes.native_size); values.Add(raw.RawSizes.static_fields_size);
        values.Add(raw.RawSizes.thread_static_fields_size); CaptureRaw(raw.RawType, values, type.AppContext);
        foreach (var field in type.Fields)
        {
            var definition = field.BackingData!.Field;
            values.Add(field); values.Add(field.Name); values.Add(field.Attributes); values.Add(field.Offset);
            CaptureRaw(definition.RawFieldType!, values, type.AppContext);
            var fieldType = field.FieldType;
            // Array metadata resolution creates a fresh wrapper on each query.
            // Bind sibling declarations by their canonical type and unchanged raw
            // descriptor; consumed receiver/scalar identities remain exact above.
            values.Add(fieldType.FullName); values.Add(fieldType.Type);
            if (fieldType is not WrappedTypeAnalysisContext)
                values.Add(fieldType);
            values.Add(field.DefaultConstantValue ?? DBNull.Value);
            values.Add(definition.nameIndex); values.Add(definition.token); values.Add(definition.typeIndex);
        }
    }
}
