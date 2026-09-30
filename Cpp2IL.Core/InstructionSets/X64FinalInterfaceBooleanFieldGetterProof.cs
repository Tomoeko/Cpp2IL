using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Closes one final interface implementation whose entire native body reads a Boolean field.</summary>
internal static class X64FinalInterfaceBooleanFieldGetterProof
{
    internal const string EvidenceKey = "X64FinalInterfaceBooleanFieldGetterProof";
    internal const string CaptureRegister = "final_interface_boolean_field_capture";

    // The values are captured, rather than retaining mutable vtable/interface arrays.
    // Re-reading the current model must reproduce every dispatch and layout value.
    internal sealed class DispatchState
    {
        private readonly object[] _values;

        internal DispatchState(List<object> values) => _values = values.ToArray();

        internal bool Matches(DispatchState other) => _values.SequenceEqual(other._values);
    }

    internal sealed record Proof(FieldAnalysisContext Field, ulong LoadIp, ulong ReturnIp, ulong End,
        DispatchState Dispatch)
    {
        internal bool Matches(Proof other) => ReferenceEquals(Field, other.Field) && LoadIp == other.LoadIp &&
            ReturnIp == other.ReturnIp && End == other.End && Dispatch.Matches(other.Dispatch);
    }

    internal static Proof? GetEvidence(MethodAnalysisContext method) => method.GetExtraData<Proof>(EvidenceKey);

    internal static List<Instruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method) is not { } proof)
            return null;
        method.PutExtraData(EvidenceKey, proof);
        var capture = new IsilRegister(null, CaptureRegister);
        return
        [
            new(0, OpCode.Move, capture, new MemoryOperand(new IsilRegister(null, "rcx"), null, proof.Field.Offset))
                { IntegerBitWidth = 8, NativeAddress = proof.LoadIp },
            new(1, OpCode.Return, capture) { NativeAddress = proof.ReturnIp },
        ];
    }

    internal static Proof? Find(MethodAnalysisContext? method)
    {
        if (method is not { AppContext: { Binary: PE pe } app } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            TryDispatch(method) is not { } dispatch ||
            X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;
        try
        {
            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).ToArray();
            if (X86DirectBooleanFieldGetterProof.FindFinalInterface(method, body) is not { } field)
                return null;
            var leaf = body.TakeWhile(instruction => instruction.Code != Iced.Intel.Code.Retnq)
                .Concat(body.SkipWhile(instruction => instruction.Code != Iced.Intel.Code.Retnq).Take(1)).ToArray();
            if (X86DirectBooleanFieldGetterProof.TryProveShape(leaf) is not { } shape ||
                shape.End <= method.UnderlyingPointer || shape.End - method.UnderlyingPointer > 64 ||
                field.DeclaringType.Definition is not { } owner ||
                (ulong)field.Offset + 1 > owner.RawSizes.instance_size ||
                unwind.ClassifySpan(method.UnderlyingPointer, shape.End) is not
                    { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var start, End: var end } ||
                start != method.UnderlyingPointer || end != shape.End ||
                X64NativeInstructionReader.Read(pe, unwind, start, leaf.Length,
                    checked((int)(end - start))) is not { } fresh || !fresh.SequenceEqual(leaf))
                return null;
            return new Proof(field, leaf[^2].IP, leaf[^1].IP, shape.End, dispatch);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static bool HasUnchangedDispatch(MethodAnalysisContext? method) => TryDispatch(method) != null;

    private static DispatchState? TryDispatch(MethodAnalysisContext? method)
    {
        if (method is not { Definition: { GenericContainer: null } definition, DeclaringType: { } owner } ||
            !method.IsVirtual || !method.IsFinal || !method.IsNewSlot || method.IsStatic || method.IsAbstract ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.Name != method.DefaultName || method.OverrideReturnType != null ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 0 || definition.parameterCount != 0 ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } || method.BaseMethod != null ||
            owner.Definition is not { InterfaceOffsetsCount: 1, InterfacesCount: 1 } ownerDefinition ||
            ownerDefinition.InterfaceOffsets is not [var mapping] || mapping.offset < 0 ||
            ownerDefinition.RawInterfaces is not [var rawInterface] ||
            owner.InterfaceContexts.ToArray() is not [var contract] ||
            !ReferenceEquals(contract, method.AppContext.ResolveIl2CppType(rawInterface)) ||
            !ReferenceEquals(contract, method.AppContext.ResolveIl2CppType(mapping.Type)) ||
            !OrdinaryType(contract, true) || contract.BaseType != null || contract.InterfaceContexts.Count != 0 ||
            contract.Definition is not { InterfacesCount: 0, InterfaceOffsetsCount: 0 } interfaceDefinition ||
            contract.Fields.Count != 0 || contract.Methods.Count == 0 ||
            contract.Methods.Count != interfaceDefinition.MethodCount ||
            ownerDefinition.VTable is not { } vtable || definition.slot == ushort.MaxValue ||
            definition.slot >= vtable.Length || vtable[definition.slot] is not
                { Type: MetadataUsageType.MethodDef } slot || !ReferenceEquals(slot.AsMethod(), definition))
            return null;

        var values = new List<object>();
        var hierarchy = new HashSet<TypeAnalysisContext>();
        var reachedObject = false;
        for (var type = owner; type != null; type = type.BaseType)
        {
            // An ancestor's class initializer is a separate managed body. Its
            // presence does not change this getter's field offset or interface
            // slot, but its declaration must still match the player metadata.
            if (!hierarchy.Add(type) || !OrdinaryType(type, false,
                    allowAncestorClassConstructor: !ReferenceEquals(type, owner)))
                return null;
            CaptureType(type, values);
            reachedObject |= ReferenceEquals(type, method.AppContext.SystemTypes.SystemObjectType);
        }
        if (!reachedObject)
            return null;

        CaptureType(contract, values);
        values.Add(mapping.offset);
        values.Add(mapping.typeIndex);
        var interfaceSlots = contract.Methods.Select(candidate => candidate.Definition?.slot ?? ushort.MaxValue).ToArray();
        if (interfaceSlots.Distinct().Count() != interfaceSlots.Length ||
            interfaceSlots.Any(value => value >= interfaceSlots.Length) ||
            mapping.offset > vtable.Length - interfaceSlots.Length)
            return null;
        var matches = new List<MethodAnalysisContext>();
        foreach (var declaration in contract.Methods)
        {
            if (declaration.Definition is not { GenericContainer: null } original ||
                original.IsUnmanagedCallersOnly ||
                !declaration.IsAbstract || !declaration.IsVirtual || declaration.IsStatic || declaration.IsFinal ||
                declaration.Attributes != declaration.DefaultAttributes ||
                declaration.ImplAttributes != declaration.DefaultImplAttributes ||
                declaration.Name != declaration.DefaultName || declaration.OverrideReturnType != null ||
                declaration.GenericParameters.Count != 0 || declaration.Parameters.Count != original.parameterCount ||
                !ReferenceEquals(original.DeclaringType, interfaceDefinition) ||
                vtable[mapping.offset + original.slot] is not { Type: MetadataUsageType.MethodDef } implementation)
                return null;
            CaptureMethod(declaration, values);
            values.Add(implementation.AsMethod());
            if (ReferenceEquals(implementation.AsMethod(), definition))
            {
                if (definition.slot != mapping.offset + original.slot || original.parameterCount != 0 ||
                    (original.InternalParameterData?.Length ?? 0) != 0 ||
                    !ReferenceEquals(declaration.ReturnType, method.ReturnType) ||
                    original.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                        NumMods: 0, Byref: 0, Pinned: 0 })
                    return null;
                matches.Add(declaration);
            }
        }
        if (matches.Count != 1 || !method.Overrides.SequenceEqual(matches))
            return null;
        CaptureMethod(method, values);
        foreach (var entry in vtable)
        {
            if (entry is not { Type: MetadataUsageType.MethodDef })
                return null;
            var target = entry.AsMethod();
            values.Add(target);
            values.Add(target.flags);
            values.Add(target.iflags);
            values.Add(target.slot);
            values.Add(target.token);
        }
        return new DispatchState(values);
    }

    private static bool OrdinaryType(TypeAnalysisContext type, bool contract,
        bool allowAncestorClassConstructor = false) =>
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { NumMods: 0, Byref: 0, Pinned: 0 } raw } &&
        (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
         raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
         ReferenceEquals(type, type.AppContext.SystemTypes.SystemObjectType)) &&
        !type.IsValueType && type.IsInterface == contract && !type.IsGenericInstance &&
        type.GenericParameters.Count == 0 && type.Attributes == type.DefaultAttributes &&
        (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout &&
        type.Name == type.DefaultName && type.OverrideNamespace == null &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        HasUnchangedClassConstructor(type, allowAncestorClassConstructor) &&
        type.Fields.Count == type.Definition.FieldCount && type.Fields.All(field => field.BackingData?.Field.RawFieldType != null &&
            field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
            field.Offset == field.DefaultOffset && field.OverrideFieldType == null) &&
        type.Methods.Count == type.Definition.MethodCount && type.Methods.All(method => method.Definition != null);

    private static bool HasUnchangedClassConstructor(TypeAnalysisContext type,
        bool allowAncestorClassConstructor)
    {
        var constructors = type.Methods.Where(method => method.Name == ".cctor").ToArray();
        if (!type.Definition!.HasCctor)
            return constructors.Length == 0;
        if (!allowAncestorClassConstructor || constructors is not [{ } constructor] ||
            constructor.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.IsUnmanagedCallersOnly ||
            !ReferenceEquals(definition.DeclaringType, type.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            constructor.Name != constructor.DefaultName || !constructor.IsStatic ||
            constructor.IsVirtual || constructor.IsAbstract ||
            constructor.Parameters.Count != 0 || constructor.GenericParameters.Count != 0 ||
            constructor.OverrideReturnType != null ||
            !ReferenceEquals(constructor.ReturnType, type.AppContext.SystemTypes.SystemVoidType) ||
            constructor.Attributes != constructor.DefaultAttributes ||
            constructor.ImplAttributes != constructor.DefaultImplAttributes ||
            (constructor.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
            (constructor.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            definition.slot != ushort.MaxValue ||
            (constructor.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) !=
            (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName))
            return false;
        return true;
    }

    private static void CaptureType(TypeAnalysisContext type, List<object> values)
    {
        var definition = type.Definition!;
        values.Add(type);
        values.Add(type.Name);
        values.Add(type.Namespace);
        values.Add(type.Attributes);
        values.Add(definition.NameIndex);
        values.Add(definition.NamespaceIndex);
        values.Add(definition.Token);
        values.Add(definition.RawType.Type);
        values.Add(definition.RawType.NumMods);
        values.Add(definition.RawType.Byref);
        values.Add(definition.RawType.Pinned);
        values.Add(definition.Bitfield);
        values.Add(definition.RawSizes.instance_size);
        values.Add(definition.ParentIndex);
        values.Add(definition.InterfacesStart);
        values.Add(definition.InterfacesCount);
        values.Add(definition.InterfaceOffsetsStart);
        values.Add(definition.InterfaceOffsetsCount);
        values.Add(definition.VtableStart);
        values.Add(definition.VtableCount);
        values.Add(type.Fields.Count);
        foreach (var field in type.Fields)
        {
            values.Add(field);
            values.Add(field.Name);
            values.Add(field.Attributes);
            values.Add(field.Offset);
            values.Add(field.FieldType.FullName);
            values.Add(field.FieldType.Type);
            values.Add(field.BackingData!.Field.nameIndex);
            values.Add(field.BackingData.Field.token);
            values.Add(field.BackingData.Field.typeIndex);
            var raw = field.BackingData.Field.RawFieldType!;
            values.Add(raw.Type);
            values.Add(raw.NumMods);
            values.Add(raw.Byref);
            values.Add(raw.Pinned);
        }
        values.Add(type.Methods.Count);
        foreach (var member in type.Methods)
            CaptureMethod(member, values);
    }

    private static void CaptureMethod(MethodAnalysisContext method, List<object> values)
    {
        values.Add(method);
        values.Add(method.Name);
        values.Add(method.Attributes);
        values.Add(method.ImplAttributes);
        values.Add(method.Definition!.iflags);
        values.Add(method.ReturnType.FullName);
        values.Add(method.ReturnType.Type);
        values.Add(method.Definition!.slot);
        values.Add(method.Definition.nameIndex);
        values.Add(method.Definition.token);
        values.Add(method.Definition.parameterCount);
        values.Add(method.Definition.returnTypeIdx);
        values.Add(method.Definition.declaringTypeIdx);
    }
}
