using System;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64OriginalReferenceClassProof
{
    // Registration identity is separate from signature/ABI eligibility. These
    // fixed v29 rows bind a parsed reference to its original native pointer;
    // callers must independently prove the behavior they emit.
    private const int MethodSpecificationBytes = 12;
    private const int GenericFunctionBytes = 16;

    internal static bool OriginalGenericMethodReference(ApplicationAnalysisContext app,
        Cpp2IlMethodRef reference, ulong pointer)
    {
        if (app.MetadataVersion != 29 || app.Binary is not PE pe || pe.PointerSizeBytes != sizeof(ulong) ||
            !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            !pe.TryGetGenericMethodTableRegistration(out var tables) ||
            !pe.TryGetGenericInstantiationTableRegistration(out var instantiations) ||
            !pe.TryGetGenericMethodRegistration(reference, out var origin) ||
            origin.CodeRegistrationAddress != tables.CodeRegistrationAddress ||
            origin.MetadataRegistrationAddress != tables.MetadataRegistrationAddress ||
            instantiations.MetadataRegistrationAddress != tables.MetadataRegistrationAddress ||
            !pe.TryGetGenericMethodSpecificationRegistration(origin.SpecificationIndex, out var originalSpecification) ||
            !pe.TryGetGenericMethodFunctionRegistration(origin.TableIndex, out var originalFunction) ||
            !NativeData(pe, origin.CodeRegistrationAddress, Il2CppCodeRegistration.GetStructSize(false, 29)) ||
            !NativeData(pe, origin.MetadataRegistrationAddress, Il2CppMetadataRegistration.GetStructSize(false, 29)))
            return false;
        var code = pe.ReadReadableAtVirtualAddress<Il2CppCodeRegistration>(origin.CodeRegistrationAddress);
        var registration = pe.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(origin.MetadataRegistrationAddress);
        var metadata = app.Metadata;
        if (registration.methodSpecsCount != tables.SpecificationCount || registration.methodSpecs != tables.SpecificationsAddress ||
            registration.genericMethodTableCount != tables.FunctionCount || registration.genericMethodTable != tables.FunctionsAddress ||
            registration.genericInstsCount != instantiations.Count || registration.genericInsts != instantiations.Address ||
            code.genericMethodPointersCount != tables.MethodPointerCount || code.genericMethodPointers != tables.MethodPointersAddress ||
            code.invokerPointersCount != tables.InvokerCount || code.invokerPointers != tables.InvokersAddress ||
            code.genericAdjustorThunks != tables.AdjustorThunksAddress ||
            registration.methodSpecsCount != metadata.AllGenericMethodSpecs.Length ||
            registration.genericMethodTableCount != metadata.genericMethodTables.Length ||
            origin.SpecificationIndex < 0 || origin.SpecificationIndex >= registration.methodSpecsCount ||
            origin.TableIndex < 0 || origin.TableIndex >= registration.genericMethodTableCount ||
            registration.genericInstsCount is < 0 or > 1_000_000 ||
            code.genericMethodPointersCount is 0 or > 1_000_000)
            return false;
        var specificationAddress = checked(registration.methodSpecs + (ulong)origin.SpecificationIndex * MethodSpecificationBytes);
        var tableAddress = checked(registration.genericMethodTable + (ulong)origin.TableIndex * GenericFunctionBytes);
        if (!NativeData(pe, specificationAddress, MethodSpecificationBytes) || !NativeData(pe, tableAddress, GenericFunctionBytes))
            return false;
        var specification = pe.ReadReadableAtVirtualAddress<Il2CppMethodSpec>(specificationAddress);
        var retained = metadata.AllGenericMethodSpecs[origin.SpecificationIndex];
        var function = pe.ReadReadableAtVirtualAddress<Il2CppGenericMethodFunctionsDefinitions>(tableAddress);
        var retainedFunction = metadata.genericMethodTables[origin.TableIndex];
        if (specification.methodDefinitionIndex.Value != originalSpecification.MethodDefinitionIndex ||
            specification.classIndexIndex.Value != originalSpecification.ClassInstantiationIndex ||
            specification.methodIndexIndex.Value != originalSpecification.MethodInstantiationIndex ||
            function.GenericMethodIndex != originalFunction.SpecificationIndex ||
            function.methodIndex != originalFunction.MethodPointerIndex || function.invokerIndex != originalFunction.InvokerIndex ||
            function.adjustorThunk != originalFunction.AdjustorThunkIndex ||
            specification.methodDefinitionIndex != retained.methodDefinitionIndex ||
            specification.classIndexIndex != retained.classIndexIndex || specification.methodIndexIndex != retained.methodIndexIndex ||
            function.GenericMethodIndex != origin.SpecificationIndex ||
            function.GenericMethodIndex != retainedFunction.GenericMethodIndex || function.methodIndex != retainedFunction.methodIndex ||
            function.invokerIndex != retainedFunction.invokerIndex || function.adjustorThunk != retainedFunction.adjustorThunk ||
            function.methodIndex < 0 || (ulong)function.methodIndex >= code.genericMethodPointersCount ||
            function.adjustorThunk != -1 || reference.AdjustorThunkPtr != 0 ||
            specification.methodDefinitionIndex.Value < 0 || specification.methodDefinitionIndex.Value >= metadata.MethodDefinitionCount ||
            !ReferenceEquals(metadata.methodDefs[specification.methodDefinitionIndex.Value], reference.BaseMethod) ||
            !OriginalMethod(app, reference.BaseMethod))
            return false;
        var pointerAddress = checked(code.genericMethodPointers + (ulong)function.methodIndex * sizeof(ulong));
        if (pointerAddress != origin.PointerAddress || !NativeData(pe, pointerAddress, sizeof(ulong)) ||
            pe.ReadPointerAtVirtualAddress(pointerAddress) != pointer || reference.GenericVariantPtr != pointer ||
            !app.Binary.ConcreteGenericMethods.TryGetValue(reference.BaseMethod, out var references) ||
            references.Count(candidate => ReferenceEquals(candidate, reference)) != 1)
            return false;
        return OriginalGenericArguments(app, pe, registration, specification.classIndexIndex.Value, reference, classArguments: true) &&
            OriginalGenericArguments(app, pe, registration, specification.methodIndexIndex.Value, reference, classArguments: false);
    }

    private static bool OriginalGenericArguments(ApplicationAnalysisContext app, PE pe,
        Il2CppMetadataRegistration registration, int index, Cpp2IlMethodRef reference, bool classArguments)
    {
        if (index == -1) return (classArguments ? reference.TypeGenericParams : reference.MethodGenericParams).Length == 0;
        if (index < 0 || index >= registration.genericInstsCount ||
            !pe.TryGetGenericInstantiationRegistration(index, out var origin) || origin.Index != index)
            return false;
        var slot = checked(registration.genericInsts + (ulong)index * sizeof(ulong));
        if (!NativeData(pe, slot, sizeof(ulong))) return false;
        var address = pe.ReadPointerAtVirtualAddress(slot);
        if (address != origin.Address || !NativeData(pe, address, 2 * sizeof(ulong))) return false;
        var original = pe.ReadReadableAtVirtualAddress<Il2CppGenericInst>(address);
        var retained = pe.GetGenericInst(Il2CppVariableWidthIndex<Il2CppGenericInst>.MakeTemporaryForFixedWidthUsage(index));
        if (original.pointerCount is < 1 or > 32 || original.pointerCount != origin.ArgumentCount ||
            original.pointerStart != origin.ArgumentsAddress || original.pointerCount != retained.pointerCount ||
            original.pointerStart != retained.pointerStart || !NativeData(pe, original.pointerStart, (long)original.pointerCount * sizeof(ulong)))
            return false;
        // Authenticate the bounded original instantiation before a lazy Types
        // getter can allocate or follow a changed cached pointer array.
        var arguments = classArguments ? reference.TypeGenericParams : reference.MethodGenericParams;
        if ((ulong)arguments.Length != original.pointerCount) return false;
        for (var ordinal = 0; ordinal < arguments.Length; ordinal++)
        {
            var pointer = pe.ReadPointerAtVirtualAddress(checked(original.pointerStart + (ulong)ordinal * sizeof(ulong)));
            if (!ReferenceEquals(pe.GetIl2CppTypeFromPointer(pointer), arguments[ordinal]) ||
                !RetainedDescriptor(app, arguments[ordinal])) return false;
        }
        return true;
    }

    private static bool NativeData(PE pe, ulong address, long size)
    {
        if (address == 0 || size <= 0 || (ulong)size > ulong.MaxValue - address) return false;
        var offset = pe.MapVirtualAddressToRaw(address, false);
        return offset >= 0 && offset <= pe.GetRawBinaryContent().Length - size &&
            pe.MapVirtualAddressToRaw(address + (ulong)size - 1, false) == offset + size - 1;
    }
}
