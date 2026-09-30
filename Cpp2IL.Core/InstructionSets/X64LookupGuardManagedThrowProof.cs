using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete lookup-or-throw body. The producer's class initialization,
/// null-checked lookup, string read, current-culture Int32 formatting, allocation,
/// constructor and nonreturning raise are independently bound in their native order.
/// </summary>
internal static class X64LookupGuardManagedThrowProof
{
    private static readonly X64CallingConventionResolver CallingConventions = new();

    internal sealed record Shape(ulong OnceFlag, ulong ProducerTypeInfoSlot,
        ulong LiteralSlot, ulong ExceptionTypeInfoSlot, ulong MethodDefSlot,
        ulong MetadataInitializer, ulong ClassInitializer, ulong Producer,
        ulong Lookup, ulong IntegerFormatter, ulong Concat, ulong Allocator,
        ulong NullGuard, ulong Constructor, ulong Raiser, ulong RuntimeNullThrow,
        int StringOffset, ulong NullCallIp, ulong RaiseCallIp,
        byte AllocationEnd, byte RbxSaveEnd, byte RdiSaveEnd);

    internal sealed record Evidence(MethodAnalysisContext Producer,
        MethodAnalysisContext Lookup, FieldAnalysisContext StringField,
        MethodAnalysisContext? StringGetter, MethodAnalysisContext IntegerFormatter,
        MethodAnalysisContext Concat, string Literal, TypeAnalysisContext ExceptionType,
        MethodAnalysisContext Constructor);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !Caller(method))
                return null;

            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } root)
                return null;
            var body = root.TakeWhile((instruction, index) =>
                index < 57 || instruction.Code != Code.Int3).ToArray();
            if (TryProveShape(body) is not { } shape ||
                root.Skip(body.Length).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, root[^1].NextIP) ||
                !unwind.MatchesUnwind(root[0].IP, root[^1].NextIP,
                    shape.RdiSaveEnd, 0,
                    [shape.RdiSaveEnd, 0x74, 0x04, 0x00,
                     shape.RbxSaveEnd, 0x34, 0x06, 0x00,
                     shape.AllocationEnd, 0x42]) ||
                root[^1].NextIP > ulong.MaxValue - 15 ||
                !X64NativePaddingProof.HasInt3Padding(pe, root[^1].NextIP,
                    (root[^1].NextIP + 15) & ~15UL) ||
                X64NativeInstructionReader.HasInteriorManagedEntry(app,
                    root[^1].NextIP - 1, (root[^1].NextIP + 15) & ~15UL) ||
                X86CallerExceptionRegionProof.Check(method, root,
                    new HashSet<ulong> { shape.NullCallIp, shape.RaiseCallIp }) != null)
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static Evidence? BindProvedShape(MethodAnalysisContext method, Shape shape)
    {
        var app = method.AppContext;
        if (!Caller(method) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            !Slots(pe, unwind, shape) ||
            shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe,
                unwind, shape.MetadataInitializer) ||
            !X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app, pe,
                unwind, shape.MetadataInitializer) ||
            shape.ClassInitializer == 0 ||
            shape.ClassInitializer != X64PeExportProof.Find(pe, unwind,
                "il2cpp_runtime_class_init") ||
            !X64IteratorAllocatorProof.IsAllocator(app, shape.Allocator) ||
            !X64TerminalManagedThrowProof.ProveNullCheck(app, pe, unwind, shape.NullGuard) ||
            !X64CodegenRaiseExceptionProof.TryIdentify(app, shape.Raiser) ||
            X86RuntimeNullThrowProof.TryIdentify(app, shape.RuntimeNullThrow) == null ||
            !UniqueMethod(app, shape.Producer, out var producer) ||
            !Producer(producer, method, shape.ProducerTypeInfoSlot) ||
            !UniqueMethod(app, shape.Lookup, out var lookup) ||
            !Lookup(lookup, method, producer.ReturnType) ||
            !BindStringRead(method, lookup.ReturnType, shape.StringOffset,
                out var field, out var getter) ||
            !UniqueMethod(app, shape.IntegerFormatter, out var formatter) ||
            !IntegerFormatter(formatter, app) ||
            !UniqueMethod(app, shape.Concat, out var concat) ||
            !OriginalMethod(concat) || !X64LiteralConcatProof.ProveConcat(concat, app) ||
            !Abi(concat, "rcx", "rdx", "r8") ||
            app.LibCpp2IlContext.GetLiteralGlobalByAddress(shape.LiteralSlot) is not
                { Type: MetadataUsageType.StringLiteral, IsValid: true } literalUsage ||
            app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodDefSlot) is not
                { Type: MetadataUsageType.MethodDef, IsValid: true } frameUsage ||
            !ReferenceEquals(frameUsage.AsMethod(), method.Definition) ||
            app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.ExceptionTypeInfoSlot) is not
                { Type: MetadataUsageType.TypeInfo, IsValid: true } exceptionUsage ||
            app.ResolveIl2CppType(exceptionUsage.AsType()) is not { } exceptionType ||
            !ExceptionType(exceptionType, app) ||
            !UniqueMethod(app, shape.Constructor, out var constructor) ||
            !StringConstructor(constructor, exceptionType, app))
            return null;

        return new Evidence(producer, lookup, field, getter, formatter, concat,
            literalUsage.AsLiteral(), exceptionType, constructor);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 57 || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !MemoryRegister(body[0], Code.Mov_rm32_r32, NativeRegister.RSP,
                0x10, NativeRegister.EDX, 4, store: true) ||
            !Stack(body[1], Code.Sub_rm64_imm8) ||
            !RipCompare(body[2]) || !Branch(body[3], Code.Jne_rel8_64, body[7].IP) ||
            !RipAddress(body[4]) || !Call(body[5]) ||
            !RipStoreOne(body[6], body[2].IPRelativeMemoryAddress) ||
            !RipLoad(body[7], body[4].IPRelativeMemoryAddress) ||
            !ClassInitCheck(body[8]) ||
            !Branch(body[9], Code.Jne_rel8_64, body[11].IP) || !Call(body[10]) ||
            !Zero(body[11], NativeRegister.ECX) || !Call(body[12]) ||
            !TestRax(body[13]) || !Branch(body[14], Code.Je_rel8_64, body[24].IP) ||
            !MemoryRegister(body[15], Code.Mov_r32_rm32, NativeRegister.RSP,
                0x38, NativeRegister.EDX, 4, store: false) ||
            !Zero(body[16], NativeRegister.R8D) ||
            !Move(body[17], NativeRegister.RCX, NativeRegister.RAX) || !Call(body[18]) ||
            !TestRax(body[19]) || !Branch(body[20], Code.Je_rel8_64, body[26].IP) ||
            !FieldRead(body[21]) || !Stack(body[22], Code.Add_rm64_imm8) ||
            body[23].Code != Code.Retnq || body[23].OpCount != 0 ||
            !Call(body[24]) || body[25].Code != Code.Int3 || body[25].OpCount != 0 ||
            !MemoryRegister(body[26], Code.Mov_rm64_r64, NativeRegister.RSP,
                0x30, NativeRegister.RBX, 8, store: true) ||
            !StackAddress(body[27], 0x38) || !Zero(body[28], NativeRegister.EDX) ||
            !MemoryRegister(body[29], Code.Mov_rm64_r64, NativeRegister.RSP,
                0x20, NativeRegister.RDI, 8, store: true) ||
            !Call(body[30]) || !RipAddress(body[31]) ||
            !Move(body[32], NativeRegister.RBX, NativeRegister.RAX) ||
            !Call(body[33], body[5].NearBranchTarget) ||
            !Move(body[34], NativeRegister.RCX, NativeRegister.RAX) ||
            !Zero(body[35], NativeRegister.R8D) ||
            !Move(body[36], NativeRegister.RDX, NativeRegister.RBX) || !Call(body[37]) ||
            !RipAddress(body[38]) || !Move(body[39], NativeRegister.RBX, NativeRegister.RAX) ||
            !Call(body[40], body[5].NearBranchTarget) ||
            !Move(body[41], NativeRegister.RCX, NativeRegister.RAX) || !Call(body[42]) ||
            !Move(body[43], NativeRegister.RCX, NativeRegister.RAX) ||
            !Move(body[44], NativeRegister.RDI, NativeRegister.RAX) || !Call(body[45]) ||
            !Zero(body[46], NativeRegister.R8D) ||
            !Move(body[47], NativeRegister.RDX, NativeRegister.RBX) ||
            !Move(body[48], NativeRegister.RCX, NativeRegister.RDI) || !Call(body[49]) ||
            !RipAddress(body[50]) || !Call(body[51], body[5].NearBranchTarget) ||
            !Move(body[52], NativeRegister.RDX, NativeRegister.RAX) ||
            !Move(body[53], NativeRegister.RCX, NativeRegister.RDI) || !Call(body[54]) ||
            !MemoryRegister(body[55], Code.Mov_r64_rm64, NativeRegister.RSP,
                0x20, NativeRegister.RDI, 8, store: false) ||
            !MemoryRegister(body[56], Code.Mov_r64_rm64, NativeRegister.RSP,
                0x30, NativeRegister.RBX, 8, store: false) ||
            body[29].NextIP - body[0].IP > byte.MaxValue)
            return null;

        return new Shape(body[2].IPRelativeMemoryAddress,
            body[4].IPRelativeMemoryAddress, body[31].IPRelativeMemoryAddress,
            body[38].IPRelativeMemoryAddress, body[50].IPRelativeMemoryAddress,
            body[5].NearBranchTarget, body[10].NearBranchTarget,
            body[12].NearBranchTarget, body[18].NearBranchTarget,
            body[30].NearBranchTarget, body[37].NearBranchTarget,
            body[42].NearBranchTarget, body[45].NearBranchTarget,
            body[49].NearBranchTarget, body[54].NearBranchTarget,
            body[24].NearBranchTarget, checked((int)body[21].MemoryDisplacement64),
            body[24].IP, body[54].IP,
            checked((byte)(body[1].NextIP - body[0].IP)),
            checked((byte)(body[26].NextIP - body[0].IP)),
            checked((byte)(body[29].NextIP - body[0].IP)));
    }

    private static bool Caller(MethodAnalysisContext method) =>
        method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        OriginalMethod(method) && !method.IsStatic && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") &&
        Return(method, method.AppContext.SystemTypes.SystemStringType,
            Il2CppTypeEnum.IL2CPP_TYPE_STRING) &&
        Parameters(method, method.AppContext.SystemTypes.SystemInt32Type) &&
        Abi(method, "rcx", "rdx", "r8");

    private static bool Producer(MethodAnalysisContext producer,
        MethodAnalysisContext caller, ulong slot)
    {
        var owner = producer.DeclaringType;
        return owner != null && OrdinaryClass(owner) && OriginalMethod(producer) &&
            producer.IsStatic && !producer.IsVirtual && producer.Visibility == MethodAttributes.Public &&
            producer.Name is not (".ctor" or ".cctor") && Parameters(producer) &&
            owner.Definition?.HasCctor == true &&
            (owner.Attributes & TypeAttributes.BeforeFieldInit) == 0 &&
            owner.Methods.Where(method => method.Name == ".cctor").ToArray() is [var initializer] &&
            OriginalMethod(initializer, requireUnique: false) && initializer.IsStatic && !initializer.IsVirtual &&
            (initializer.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) ==
                (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
            Return(initializer, caller.AppContext.SystemTypes.SystemVoidType, Il2CppTypeEnum.IL2CPP_TYPE_VOID) &&
            Parameters(initializer) && Abi(initializer, "rcx") &&
            ReturnClass(producer) && Abi(producer, "rcx") &&
            Referenced(caller, owner) && Referenced(caller, producer.ReturnType) &&
            caller.AppContext.LibCpp2IlContext.GetRawTypeGlobalByAddress(slot) is
                { Type: MetadataUsageType.TypeInfo, IsValid: true } usage &&
            ReferenceEquals(caller.AppContext.ResolveIl2CppType(usage.AsType()), owner);
    }

    private static bool Lookup(MethodAnalysisContext lookup,
        MethodAnalysisContext caller, TypeAnalysisContext receiver) =>
        lookup.DeclaringType is { } owner && OrdinaryClass(owner) &&
        HasLookupReceiverBase(receiver, owner) &&
        OriginalMethod(lookup) && !lookup.IsStatic && !lookup.IsVirtual &&
        lookup.Visibility == MethodAttributes.Public &&
        lookup.Name is not (".ctor" or ".cctor") &&
        ReturnClass(lookup) && Referenced(caller, owner) && Referenced(caller, lookup.ReturnType) &&
        Parameters(lookup, caller.AppContext.SystemTypes.SystemInt32Type) &&
        Abi(lookup, "rcx", "rdx", "r8");

    private static bool IntegerFormatter(MethodAnalysisContext method,
        ApplicationAnalysisContext app) =>
        ReferenceEquals(method.DeclaringType, app.SystemTypes.SystemInt32Type) &&
        method.DeclaringType is { Name: "Int32", Namespace: "System" } integer &&
        integer.Name == integer.DefaultName && integer.Namespace == integer.DefaultNamespace &&
        integer.Attributes == integer.DefaultAttributes && integer.IsValueType &&
        ReferenceEquals(integer.BaseType, integer.DefaultBaseType) &&
        method.DeclaringType.DeclaringAssembly == app.SystemTypes.SystemObjectType.DeclaringAssembly &&
        method.Name == "ToString" && OriginalMethod(method) &&
        !method.IsStatic && method.Visibility == MethodAttributes.Public &&
        Parameters(method) && Return(method, app.SystemTypes.SystemStringType,
            Il2CppTypeEnum.IL2CPP_TYPE_STRING) && Abi(method, "rcx", "rdx");

    private static bool ExceptionType(TypeAnalysisContext type, ApplicationAnalysisContext app)
    {
        if (!OrdinaryClass(type) || type.IsAbstract || type.Namespace != "System" ||
            type.DeclaringType != null ||
            !ReferenceEquals(X86RuntimeNullThrowProof.BindIdentity(app, type.Name)?.DeclaringType, type))
            return false;
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = type.BaseType; current != null && seen.Add(current); current = current.BaseType)
            if (ReferenceEquals(current, app.SystemTypes.SystemExceptionType))
                return true;
        return false;
    }

    private static bool StringConstructor(MethodAnalysisContext constructor,
        TypeAnalysisContext type, ApplicationAnalysisContext app) =>
        ReferenceEquals(constructor.DeclaringType, type) && OriginalMethod(constructor) &&
        constructor.Name == ".ctor" && !constructor.IsStatic && !constructor.IsVirtual &&
        constructor.Visibility == MethodAttributes.Public &&
        (constructor.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) ==
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) &&
        Return(constructor, app.SystemTypes.SystemVoidType, Il2CppTypeEnum.IL2CPP_TYPE_VOID) &&
        Parameters(constructor, app.SystemTypes.SystemStringType) && Abi(constructor, "rcx", "rdx", "r8") &&
        type.Methods.Count(candidate => candidate.Name == ".ctor" &&
            candidate.Parameters is [var parameter] &&
            ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemStringType)) == 1;

    private static bool BindStringRead(MethodAnalysisContext caller, TypeAnalysisContext receiver,
        int offset, out FieldAnalysisContext field, out MethodAnalysisContext? getter)
    {
        field = null!;
        getter = null;
        if (X64LiteralConcatProof.ProveInheritedStringField(receiver, (ulong)offset, out field))
            return OrdinaryClass(field.DeclaringType) && Referenced(caller, field.DeclaringType);
        // An inlined private read is source-accessible only through one independently
        // proved original getter. A member name alone does not establish its effects.
        var fields = receiver.Fields.Where(candidate => !candidate.IsStatic &&
            candidate.Offset == offset).ToArray();
        if (fields is not [var value] || !OrdinaryClass(receiver) ||
            value.Name != value.DefaultName || value.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(value.FieldType, caller.AppContext.SystemTypes.SystemStringType) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(value,
                new LocalVariable("lookup-record", new ISIL.Register(null, "rax"), receiver), offset)))
            return false;

        var candidates = receiver.Properties.Where(property =>
            ProveStringGetter(property, caller, value)).ToArray();
        if (candidates is not [var property])
            return false;
        field = value;
        getter = property.Getter;
        return true;
    }

    private static bool ProveStringGetter(PropertyAnalysisContext property,
        MethodAnalysisContext caller, FieldAnalysisContext field)
    {
        var type = field.DeclaringType;
        if (!ReferenceEquals(property.DeclaringType, type) || property.Definition is not { } definition ||
            !ReferenceEquals(definition.DeclaringType, type.Definition) ||
            property.Name != property.DefaultName || property.Attributes != property.DefaultAttributes ||
            property.OverridePropertyType != null ||
            !ReferenceEquals(property.PropertyType, caller.AppContext.SystemTypes.SystemStringType) ||
            property.IsStatic || property.IsVirtual || property.Getter is not { } getter ||
            !ReferenceEquals(definition.Getter, getter.Definition) ||
            !ReferenceEquals(definition.Setter, property.Setter?.Definition) ||
            !ReferenceEquals(getter.DeclaringType, type) || !OriginalMethod(getter, requireUnique: false) ||
            getter.IsStatic || getter.IsVirtual || getter.Visibility != MethodAttributes.Public ||
            (getter.Attributes & MethodAttributes.SpecialName) == 0 || !Parameters(getter) ||
            !Return(getter, caller.AppContext.SystemTypes.SystemStringType, Il2CppTypeEnum.IL2CPP_TYPE_STRING) ||
            !Abi(getter, "rcx", "rdx") ||
            X64NativeInstructionReader.ReadFramelessLeaf(getter, 2, 32) is not { } body ||
            !MemoryRegister(body[0], Code.Mov_r64_rm64, NativeRegister.RCX,
                (ulong)field.Offset, NativeRegister.RAX, 8, store: false) ||
            body[1].Code != Code.Retnq || body[1].OpCount != 0 ||
            X86CallerExceptionRegionProof.Check(getter, body, new HashSet<ulong>()) != null)
            return false;
        return true;
    }

    private static bool OriginalMethod(MethodAnalysisContext method, bool requireUnique = true) =>
        method.Definition is { GenericContainer: null } definition &&
        !definition.IsUnmanagedCallersOnly &&
        ReferenceEquals(definition.DeclaringType, method.DeclaringType?.Definition) &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUnique);

    private static bool Parameters(MethodAnalysisContext method, params TypeAnalysisContext[] types)
    {
        if (method.Definition is not { } definition || definition.parameterCount != types.Length ||
            method.Parameters.Count != types.Length ||
            (definition.InternalParameterData?.Length ?? 0) != types.Length)
            return false;
        return method.Parameters.Select((parameter, index) =>
            Parameter(parameter, definition.InternalParameterData![index], method, index, types[index])).All(valid => valid);
    }

    private static bool Parameter(ParameterAnalysisContext parameter, Il2CppParameterDefinition raw,
        MethodAnalysisContext method, int index, TypeAnalysisContext type) =>
        ReferenceEquals(parameter.Definition, raw) && ReferenceEquals(parameter.DeclaringMethod, method) &&
        parameter.ParameterIndex == index && !parameter.IsRef &&
        raw.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } rawType && rawType.Type == type.Type &&
        ReferenceEquals(parameter.ParameterType, type) && ReferenceEquals(parameter.DefaultParameterType, type) &&
        parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes &&
        parameter.OverrideParameterType == null && parameter.OverrideAttributes == null &&
        !parameter.UseOverrideDefaultValue;

    private static bool Return(MethodAnalysisContext method, TypeAnalysisContext type, Il2CppTypeEnum rawType) =>
        method.Definition?.RawReturnType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
        raw.Type == rawType && ReferenceEquals(method.ReturnType, type) &&
        ReferenceEquals(method.DefaultReturnType, type);

    private static bool ReturnClass(MethodAnalysisContext method) =>
        OrdinaryClass(method.ReturnType) && Return(method, method.ReturnType, Il2CppTypeEnum.IL2CPP_TYPE_CLASS);

    private static bool Abi(MethodAnalysisContext method, params string[] registers) =>
        !CallingConventions.ReturnsViaHiddenBuffer(method) &&
        (method.IsVoid || CallingConventions.ReturnRegister(method).Name == "rax") &&
        CallingConventions.ResolveForManaged(method).Select(operand =>
            operand is ISIL.Register register ? register.Name : "").SequenceEqual(registers);

    private static bool OrdinaryClass(TypeAnalysisContext type)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = type; seen.Add(current); current = current.DeclaringType)
        {
            if (!NullCheckedCall.IsReferenceClass(current) || current.Definition is not
                { GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                definition.RawBaseType is { } rawBase && !ReferenceDescriptor(rawBase) ||
                current.Name != current.DefaultName || current.Namespace != current.DefaultNamespace ||
                current.Attributes != current.DefaultAttributes ||
                (current.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout)
                return false;
            if (current.DeclaringType == null)
                return current.Visibility == TypeAttributes.Public && definition.DeclaringTypeIndex.IsNull;
            if (current.Visibility != TypeAttributes.NestedPublic ||
                !ReferenceEquals(definition.DeclaringType, current.DeclaringType.Definition))
                return false;
        }
        return false;
    }

    private static bool HasLookupReceiverBase(TypeAnalysisContext receiver, TypeAnalysisContext owner)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null && seen.Add(current); current = current.BaseType)
        {
            // A native direct base call still consumes the derived receiver. Each
            // original class and base descriptor must preserve that reference ABI.
            if (!OrdinaryClass(current))
                return false;
            if (ReferenceEquals(current, owner))
                return true;
        }
        return false;
    }

    private static bool ReferenceDescriptor(Il2CppType type) => type is
        { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
            NumMods: 0, Byref: 0, Pinned: 0, Data: not null };

    private static bool Referenced(MethodAnalysisContext caller, TypeAnalysisContext type) =>
        X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringType!.DeclaringAssembly,
            type.DeclaringAssembly);

    private static bool Slots(PE pe, X64UnwindProof.Index unwind, Shape shape)
    {
        var slots = new[] { shape.ProducerTypeInfoSlot, shape.LiteralSlot,
            shape.ExceptionTypeInfoSlot, shape.MethodDefSlot };
        if (!X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.OnceFlag) ||
            slots.Any(slot => !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, slot, 8) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, slot, 8) ||
                Overlaps(slot, 8, shape.OnceFlag, 1)))
            return false;
        return slots.SelectMany((slot, index) => slots.Skip(index + 1)
            .Select(other => !Overlaps(slot, 8, other, 8))).All(disjoint => disjoint);
    }

    private static bool UniqueMethod(ApplicationAnalysisContext app, ulong target,
        out MethodAnalysisContext method)
    {
        method = null!;
        if (target == 0 || !app.MethodsByAddress.TryGetValue(target, out var bindings) ||
            bindings is not [var unique] || unique.UnderlyingPointer != target)
            return false;
        method = unique;
        return true;
    }

    private static bool Overlaps(ulong first, ulong firstLength, ulong second, ulong secondLength) =>
        first > ulong.MaxValue - firstLength || second > ulong.MaxValue - secondLength ||
        first < second + secondLength && second < first + firstLength;

    private static bool MemoryRegister(NativeInstruction instruction, Code code,
        NativeRegister basis, ulong offset, NativeRegister register, uint size, bool store)
    {
        var memory = store ? 0 : 1;
        var operand = 1 - memory;
        return instruction.Code == code && instruction.OpCount == 2 &&
            instruction.GetOpKind(operand) == OpKind.Register &&
            instruction.GetOpRegister(operand) == register &&
            instruction.GetOpKind(memory) == OpKind.Memory && instruction.MemoryBase == basis &&
            instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 == offset &&
            instruction.MemorySize.GetSize() == size;
    }

    private static bool Move(NativeInstruction instruction, NativeRegister destination, NativeRegister source) =>
        Registers(instruction, Code.Mov_r64_rm64, destination, source);
    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        Registers(instruction, Code.Xor_r32_rm32, register, register);
    private static bool TestRax(NativeInstruction instruction) =>
        Registers(instruction, Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX);
    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;
    private static bool Stack(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x28;
    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;
    private static bool Call(NativeInstruction instruction, ulong? target = null) =>
        instruction.Code == Code.Call_rel32_64 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0 &&
        (target == null || instruction.NearBranchTarget == target);
    private static bool RipCompare(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;
    private static bool RipAddress(NativeInstruction instruction) =>
        instruction.Code == Code.Lea_r64_m && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RCX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;
    private static bool StackAddress(NativeInstruction instruction, ulong offset) =>
        instruction.Code == Code.Lea_r64_m && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RCX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RSP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemoryDisplacement64 == offset;
    private static bool RipLoad(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RCX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;
    private static bool RipStoreOne(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == address && instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 1;
    private static bool ClassInitCheck(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm32_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RCX &&
        instruction.MemoryIndex == NativeRegister.None && instruction.MemorySize.GetSize() == 4 &&
        instruction.MemoryDisplacement64 == Il2CppClassLayout.CctorFinishedOrNoCctorOffset64 &&
        instruction.Op1Kind == OpKind.Immediate8to32 && instruction.GetImmediate(1) == 0;
    private static bool FieldRead(NativeInstruction instruction) =>
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue &&
        MemoryRegister(instruction, Code.Mov_r64_rm64, NativeRegister.RAX,
            instruction.MemoryDisplacement64, NativeRegister.RAX, 8, store: false);
}
