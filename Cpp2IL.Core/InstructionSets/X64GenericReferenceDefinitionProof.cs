using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

// Composes original declaration, registration, aliases and complete native leaf
// evidence. Normal-path effects are retained separately from unresolved memory
// and exception semantics; this component never admits caller runtime helpers.
internal static class X64GenericReferenceDefinitionProof
{
    internal sealed record SelectedSpecification(int Ordinal, int InstantiationOrdinal,
        Il2CppMethodSpec Instance, Il2CppType Argument, bool DirectlyRegistered);

    internal sealed record Variant(int FunctionOrdinal, Cpp2IlMethodRef Reference,
        ConcreteGenericMethodAnalysisContext Context, ulong Pointer, ulong Invoker,
        ulong NextEntry, string ClosedBytes);

    internal sealed record BoundIlStep(CilOpCode OpCode, FieldAnalysisContext? Field = null,
        GenericParameterTypeAnalysisContext? ReferenceValue = null);

    internal sealed class Evidence
    {
        internal MethodAnalysisContext Method { get; }
        internal GenericParameterTypeAnalysisContext GenericParameter { get; }
        internal FieldAnalysisContext Counter { get; }
        internal X64GenericMethodTableProof.Evidence Tables { get; }
        internal OriginalGenericDeclarationIdentityProof.Evidence Declaration { get; }
        internal ulong OriginalDefinitionModulePointer { get; }
        private readonly SelectedSpecification[] _specifications;
        private readonly Variant[] _variants;
        private readonly BoundIlStep[] _recipe;
        private readonly string _metadataBinding;
        internal ReadOnlySpan<SelectedSpecification> Specifications => _specifications;
        internal ReadOnlySpan<Variant> Variants => _variants;
        internal ReadOnlySpan<BoundIlStep> Recipe => _recipe;
        internal string RecipeFingerprint { get; }
        internal bool CompleteBodySemantics => false;
        internal string[] UnresolvedBodyReasons =>
        [
            "GENERIC-BODY-IMPLICIT-FAULT: Native receiver access faults have not been established as the same managed exception behavior.",
            "GENERIC-BODY-MEMORY-EFFECTS: Concurrent access and fault precedence of native memory INC versus managed read/add/store remain unverified.",
            "GENERIC-BODY-MODIFIERS: The retained descriptor cannot establish stripped volatile modifier semantics."
        ];

        internal Evidence(MethodAnalysisContext method, GenericParameterTypeAnalysisContext parameter,
            FieldAnalysisContext counter, X64GenericMethodTableProof.Evidence tables,
            OriginalGenericDeclarationIdentityProof.Evidence declaration, ulong modulePointer,
            SelectedSpecification[] specifications, Variant[] variants, string metadataBinding)
        {
            Method = method;
            GenericParameter = parameter;
            Counter = counter;
            Tables = tables;
            Declaration = declaration;
            OriginalDefinitionModulePointer = modulePointer;
            _specifications = specifications.ToArray();
            _variants = variants.ToArray();
            _recipe =
            [new(CilOpCodes.Ldarg_0), new(CilOpCodes.Dup), new(CilOpCodes.Ldfld, counter),
                new(CilOpCodes.Ldc_I4_1), new(CilOpCodes.Add), new(CilOpCodes.Stfld, counter),
                new(CilOpCodes.Ldarg_1, ReferenceValue: parameter), new(CilOpCodes.Ret, ReferenceValue: parameter)];
            _metadataBinding = metadataBinding;
            RecipeFingerprint = Fingerprint(string.Join("|", _recipe.Select(step =>
                step.OpCode.Code + ":" + step.Field?.BackingData!.Field.token + ":" + step.ReferenceValue?.Index)) + "|" +
                method.Definition!.MethodIndex.Value + "|" + method.Definition.token + "|" +
                counter.BackingData!.Field.token + "|" + declaration.Container.Index + "|" + declaration.Parameters[0].Origin.Index);
        }

        // These bindings retain original managed operands for partial IL emission.
        // They do not establish caller MethodRef initialization or full behavior.
        internal string[] IlRecipe => _recipe.Select(step => step.OpCode +
            (step.Field != null ? " original-counter:Int32" : "") +
            (step.ReferenceValue != null ? " original-MVAR:" + step.ReferenceValue.Index : "")).ToArray();

        internal bool IsUnchanged(out string reason)
        {
            if (!Declaration.Matches() || Method.AppContext.Binary is not PE pe ||
                X64UnwindProof.ForApplication(Method.AppContext) is not { } index ||
                !Tables.Matches(Method.AppContext, pe, index))
            { reason = "whole-original-tables"; return false; }
            var current = Find(Method, out reason);
            if (current == null || !ReferenceEquals(Counter, current.Counter) ||
                !ReferenceEquals(GenericParameter, current.GenericParameter) ||
                _metadataBinding != current._metadataBinding ||
                RecipeFingerprint != current.RecipeFingerprint ||
                Declaration.Container != current.Declaration.Container ||
                OriginalDefinitionModulePointer != current.OriginalDefinitionModulePointer ||
                !_specifications.SequenceEqual(current._specifications) ||
                !_variants.SequenceEqual(current._variants))
            { reason = "saved-definition-facts:" + reason; return false; }
            reason = "qualified-normal-path-body-facts";
            return true;
        }
    }

    internal static Evidence? Find(MethodAnalysisContext method, out string reason)
    {
        reason = "profile-and-original-owner";
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
                method is ConcreteGenericMethodAnalysisContext || method.Definition is not
                    { genericContainerIndex: { IsNull: false }, parameterCount: 1 } definition ||
                method.DeclaringType is not { } owner || !X64OriginalReferenceClassProof.IsValid(owner, false) ||
                !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
                !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
                !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
                (method.Attributes & (MethodAttributes.Static | MethodAttributes.Virtual | MethodAttributes.Abstract |
                    MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) != 0 ||
                method.OverrideName != null || method.OverrideAttributes != null || method.OverrideImplAttributes != null ||
                method.OverrideReturnType != null || definition.slot != ushort.MaxValue ||
                definition.parameterCount != 1 || method.Parameters.Count != 1 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                    MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0)
                return null;

            reason = "generic-definition-container-parameter-MVAR";
            if (OriginalGenericDeclarationIdentityProof.TryIdentify(method) is not { } declaration ||
                declaration.Parameters is not [var binding] ||
                binding.Context is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_MVAR } parameter ||
                parameter.Attributes != GenericParameterAttributes.ReferenceTypeConstraint ||
                binding.ConstraintIndices.Length != 0 ||
                declaration.Container.GenericParameterCount != 1 ||
                !declaration.Container.IsGenericMethod ||
                definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_MVAR,
                    Attrs: 0, Byref: 0, Pinned: 0, NumMods: 0, ValueType: 0 } mvar ||
                mvar.Data.Dummy != (ulong)binding.Origin.Index ||
                !X64OriginalReferenceClassProof.RetainedDescriptor(app, mvar) ||
                !ReferenceEquals(method.ReturnType, parameter) ||
                method.Parameters[0] is not { Definition: { } parameterDefinition } argument ||
                argument.ParameterIndex != 0 || !ReferenceEquals(argument.DeclaringMethod, method) ||
                argument.OverrideName != null || argument.OverrideAttributes != null ||
                argument.OverrideParameterType != null || argument.UseOverrideDefaultValue ||
                argument.IsRef || argument.Attributes != 0 || !ReferenceEquals(argument.ParameterType, parameter) ||
                !ReferenceEquals(parameterDefinition.RawType, mvar) ||
                !X64OriginalReferenceClassProof.OriginalParameter(app, definition, 0, parameterDefinition))
                return null;

            reason = "definition-native-shape";
            if (X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 16) is not { } initialBody ||
                X64GenericReferenceLeafShape.TryProveShape(initialBody) == null) return null;

            reason = "whole-original-tables";
            if (X64GenericMethodTableProof.TryIdentify(app, pe, index) is not { } tables) return null;
            var selected = tables.Specifications.ToArray().Where(row =>
                row.Origin.MethodDefinitionIndex == definition.MethodIndex.Value).ToArray();
            if (selected.Length == 0) return null;
            var selectedOrdinals = new HashSet<int>(selected.Select(row => row.Origin.Index));
            var functions = tables.Functions.ToArray().Where(row =>
                selectedOrdinals.Contains(row.Origin.SpecificationIndex)).ToArray();
            if (functions.Length == 0 || !pe.ConcreteGenericMethods.TryGetValue(definition, out var references) ||
                references.Count != functions.Length || references.Distinct().Count() != references.Count)
                return null;

            reason = "selected-specifications-and-reference-class-identities";
            var specs = new List<SelectedSpecification>();
            foreach (var row in selected)
            {
                if (row.Origin.ClassInstantiationIndex != -1 || row.Origin.MethodInstantiationIndex < 0 ||
                    row.Origin.MethodInstantiationIndex >= tables.Instantiations.Rows.Length)
                    return null;
                var instantiation = tables.Instantiations.Rows[row.Origin.MethodInstantiationIndex];
                if (instantiation.Descriptors is not [var descriptor] ||
                    descriptor.Original is not { Attrs: 0, Byref: 0, Pinned: 0, NumMods: 0, ValueType: 0 } raw ||
                    raw.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT) ||
                    X64OriginalReferenceClassProof.ResolveClass(app, raw) is not { } argumentType ||
                    !X64OriginalReferenceClassProof.IsValid(argumentType, false))
                    return null;
                specs.Add(new(row.Origin.Index, instantiation.Ordinal, row.Instance, raw,
                    functions.Any(function => function.Origin.SpecificationIndex == row.Origin.Index)));
            }

            reason = "all-registered-variants-original-identities-and-aliases";
            var variants = new List<Variant>();
            FieldAnalysisContext? counter = null;
            string? fieldLayoutBinding = null;
            foreach (var function in functions)
            {
                var original = function.Origin;
                if (original.MethodPointerIndex < 0 || original.MethodPointerIndex >= tables.MethodPointers.Length ||
                    original.InvokerIndex < 0 || original.InvokerIndex >= tables.Invokers.Length ||
                    original.AdjustorThunkIndex != -1) return null;
                var pointer = tables.MethodPointers[original.MethodPointerIndex];
                var matching = references.Where(reference => pe.TryGetGenericMethodRegistration(reference, out var identity) &&
                    identity.TableIndex == original.Index).ToArray();
                reason = "variant-original-reference";
                if (pointer == 0 || matching is not [var reference] ||
                    !pe.TryGetGenericMethodRegistration(reference, out var identity) ||
                    identity.SpecificationIndex != original.SpecificationIndex ||
                    !ReferenceEquals(reference.BaseMethod, definition) || reference.GenericVariantPtr != pointer ||
                    reference.AdjustorThunkPtr != 0 ||
                    !X64OriginalReferenceClassProof.OriginalGenericMethodReference(app, reference, pointer)) return null;
                reason = "variant-context-substitution";
                if (
                    !app.ConcreteGenericMethodsByRef.TryGetValue(reference, out var context) ||
                    !ReferenceEquals(context.BaseMethodContext, method) || !ReferenceEquals(context.MethodRef, reference) ||
                    context.MethodGenericParameters.Count != 1 || context.TypeGenericParameters.Count != 0 ||
                    context.OverrideReturnType != null || context.Parameters.Count != 1 ||
                    context.Parameters[0].OverrideParameterType != null ||
                    !ReferenceEquals(context.ReturnType, context.MethodGenericParameters[0]) ||
                    !ReferenceEquals(context.Parameters[0].ParameterType, context.MethodGenericParameters[0]) ||
                    !ReferenceEquals(app.ResolveIl2CppType(reference.MethodGenericParams[0]), context.MethodGenericParameters[0])) return null;
                reason = "variant-complete-native-body";
                if (
                    X64NativeInstructionReader.ReadFramelessLeaf(context, 3, 16) is not { } body ||
                    X64GenericReferenceLeafShape.TryProveShape(body) is not { } shape ||
                    X86CallerExceptionRegionProof.Check(context, body, new HashSet<ulong>()) != null)
                    return null;
                var next = app.MethodsByAddress.Keys.Where(address => address > pointer && address <= pointer + 32)
                    .DefaultIfEmpty(0UL).Min();
                reason = "variant-native-terminal-padding";
                if (next <= shape.End || next - shape.End > 15 ||
                    index.ClassifySpan(pointer, next).Kind != X64UnwindProof.SpanKind.NoEntry ||
                    !index.HasFunctionEntryAt(next, next + 1) ||
                    X64NativeInstructionReader.HasInteriorManagedEntry(app, pointer, next) ||
                    !X64NativePaddingProof.HasInt3Padding(pe, shape.End, next)) return null;
                var length = checked((int)(next - pointer));
                var offset = pe.MapVirtualAddressToRaw(pointer, false);
                var closedBytes = pe.GetRawBinaryContent().Slice(checked((int)offset), length);
                reason = "variant-counter-field-layout";
                if (!X64AncestorConstructorThunkProof.FileBackedExecutable(pe, index, closedBytes, pointer) ||
                    BindCounter(method, shape.CounterOffset, ref reason, out var fieldLayout) is not { } field ||
                    counter != null && (!ReferenceEquals(counter, field) || fieldLayoutBinding != fieldLayout)) return null;
                counter = field;
                fieldLayoutBinding = fieldLayout;
                variants.Add(new(original.Index, reference, context, pointer,
                    tables.Invokers[original.InvokerIndex], next, Convert.ToBase64String(closedBytes.ToArray())));
            }
            foreach (var pointer in variants.Select(variant => variant.Pointer).Distinct())
            {
                reason = "variant-complete-alias-set";
                var expected = variants.Where(variant => variant.Pointer == pointer).Select(variant => (MethodAnalysisContext)variant.Context)
                    .ToList();
                if (method.UnderlyingPointer == pointer) expected.Add(method);
                if (!app.MethodsByAddress.TryGetValue(pointer, out var aliases) || aliases.Count != expected.Count ||
                    aliases.Distinct().Count() != aliases.Count || aliases.Any(alias => !expected.Contains(alias)) ||
                    !pe.ConcreteGenericImplementationsByAddress.TryGetValue(pointer, out var nativeAliases) ||
                    nativeAliases.Count != variants.Count(variant => variant.Pointer == pointer) ||
                    nativeAliases.Distinct().Count() != nativeAliases.Count ||
                    nativeAliases.Any(alias => !variants.Any(variant => ReferenceEquals(variant.Reference, alias) && variant.Pointer == pointer)))
                    return null;
            }

            reason = "missing-definition-pointer-bound-to-all-original-variants";
            var module = owner.DeclaringAssembly.CodeGenModule!;
            var modulePointers = pe.GetCodegenModuleMethodPointers(pe.GetCodegenModuleIndex(module));
            var slot = checked((int)(definition.token & 0xffffff) - 1);
            if (slot < 0 || slot >= modulePointers.Length ||
                !pe.TryGetCodegenModuleVirtualAddress(module, out var moduleAddress) ||
                ReadFileBackedData(pe, index, moduleAddress, 24) is not { } moduleBytes ||
                U64(moduleBytes, 8) != (ulong)modulePointers.Length || U64(moduleBytes, 16) != module.methodPointers ||
                ReadFileBackedData(pe, index, checked(module.methodPointers + (ulong)slot * 8), 8) is not { } slotBytes ||
                U64(slotBytes, 0) != modulePointers[slot] || modulePointers[slot] != 0 ||
                method.UnderlyingPointer != variants.OrderBy(variant => variant.FunctionOrdinal).First().Pointer) return null;
            reason = "definition-complete-native-body";
            if (
                X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 16) is not { } definitionBody ||
                X64GenericReferenceLeafShape.TryProveShape(definitionBody) is not { } definitionShape ||
                definitionShape.CounterOffset != counter!.Offset) return null;

            reason = "qualified-normal-path-body-facts";
            var metadataBinding = counter.BackingData!.Field.token + ":" + counter.Offset + ":" +
                owner.Definition!.RawSizes.instance_size + ":" + fieldLayoutBinding;
            return new(method, parameter, counter!, tables, declaration, modulePointers[slot],
                specs.ToArray(), variants.ToArray(), metadataBinding);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        { reason += ":" + failure.GetType().Name; return null; }
    }

    private static FieldAnalysisContext? BindCounter(MethodAnalysisContext method, int offset,
        ref string reason, out string binding)
    {
        binding = "";
        var owner = method.DeclaringType!;
        reason = "counter-original-scalar-field";
        var selected = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        if (selected is not [var field] || field.BackingData?.Field.RawFieldType is not
            { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 } raw ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(method.AppContext, raw) ||
            field.OverrideAttributes != null || field.OverrideOffset != null || field.OverrideName != null ||
            field.OverrideFieldType != null ||
            field.UseOverrideConstantValue || field.OverrideStaticArrayInitialValue != null ||
            (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal | FieldAttributes.HasFieldMarshal)) != 0 ||
            !ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemInt32Type)) return null;
        reason = "counter-file-backed-and-unchanged-layout";
        if (!X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(owner, out var layout) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field,
                new LocalVariable("qualified-owner", new Register(null, "rcx"), owner), offset), 32)) return null;
        binding = Convert.ToBase64String(layout);
        return field;
    }

    private static byte[]? ReadOnly(PE pe, X64UnwindProof.Index index, ulong address, uint length)
    {
        var offset = index.MapReadOnlyData(address, length);
        return offset >= 0 && offset <= pe.GetRawBinaryContent().Length - (long)length
            ? pe.GetRawBinaryContent().Slice(offset, checked((int)length)).ToArray() : null;
    }

    private static byte[]? ReadFileBackedData(PE pe, X64UnwindProof.Index index, ulong address, uint length)
    {
        // Pointer lists are writable in the original PE. Bind file-backed input
        // facts and cached selected values, without declaring runtime immutability.
        if (ReadOnly(pe, index, address, length) is { } readOnly) return readOnly;
        if (!X64MetadataStaticGetterProof.FileBackedWritableData(pe, index, address, length)) return null;
        return pe.GetRawBinaryContent().Slice(checked((int)pe.MapVirtualAddressToRaw(address, false)), checked((int)length)).ToArray();
    }

    private static string Fingerprint(string text)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
}
