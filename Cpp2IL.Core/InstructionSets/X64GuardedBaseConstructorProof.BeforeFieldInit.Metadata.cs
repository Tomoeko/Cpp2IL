using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedBaseConstructorProof
{
    private static bool CaptureBeforeFieldInitType(TypeAnalysisContext type, List<object> inputs)
    {
        var app = type.AppContext;
        if (type.Definition is not { GenericContainer: null, IsEnumType: false, IsByRefLike: false,
                IsImportOrWindowsRuntime: false, PackingSizeIsDefault: true, ClassSizeIsDefault: true } original ||
            type.IsValueType || type.IsInterface || type.IsGenericInstance || type.GenericParameters.Count != 0 ||
            type.Visibility != TypeAttributes.Public || type.DeclaringType != null ||
            type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
            type.OverrideName != null || type.OverrideNamespace != null || type.OverrideAttributes != null ||
            type.Attributes != type.DefaultAttributes || type.OverrideBaseType != null ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(type.DeclaringAssembly) ||
            !X64OriginalReferenceClassProof.OriginalType(app, original) ||
            !ReferenceEquals(app.ResolveContextForType(original), type) ||
            !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, original.RawType), type) ||
            original.RawType.Datapoint != (ulong)original.TypeIndex.Value ||
            !X64OriginalReferenceClassProof.ValidTypeIndex(app, original.ParentIndex.Value) ||
            !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, app.Binary.AllTypes[original.ParentIndex.Value]), type.BaseType) ||
            !X64ScalarStaticConstructorProof.CaptureDescriptor(app, app.Binary.AllTypes[original.ParentIndex.Value], inputs) ||
            !X64ScalarStaticConstructorProof.CaptureDescriptor(app, original.RawType, inputs) ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(type, out var layout) ||
            !(original.Methods ?? []).SequenceEqual(type.Methods.Select(method => method.Definition)) ||
            !(original.Fields ?? []).SequenceEqual(type.Fields.Select(field => field.BackingData?.Field)) ||
            type.InterfaceContexts.Count != original.InterfacesCount ||
            original.RawInterfaces.Length != type.InterfaceContexts.Count)
            return false;
        inputs.AddRange([type, original, type.DeclaringAssembly, type.Name, type.Namespace, type.Attributes,
            type.BaseType ?? (object)"no-base", original.TypeIndex.Value, original.Token, original.Flags,
            original.Bitfield, original.ParentIndex.Value, original.ByvalTypeIndex.Value,
            Convert.ToBase64String(layout), type.Methods.Count, type.Fields.Count]);
        for (var ordinal = 0; ordinal < type.InterfaceContexts.Count; ordinal++)
        {
            var raw = original.RawInterfaces[ordinal];
            var context = type.InterfaceContexts[ordinal];
            var index = app.Metadata.ReadClassArrayAtRawAddr<int>(app.Metadata.metadataHeader.interfaces.Offset +
                (long)(original.InterfacesStart.Value + ordinal) * 4, 1)[0];
            if (!X64OriginalReferenceClassProof.ValidTypeIndex(app, index) ||
                app.Metadata.GetInterfaceIndicesFromOffset(original.InterfacesStart, checked((ushort)ordinal)).Value != index ||
                !ReferenceEquals(app.Binary.AllTypes[index], raw) ||
                context is not { IsInterface: true } || !ReferenceEquals(app.ResolveIl2CppType(raw), context) ||
                !X64ScalarStaticConstructorProof.CaptureDescriptor(app, raw, inputs)) return false;
            inputs.Add(index); inputs.Add(context);
        }
        foreach (var method in type.Methods)
            if (!CaptureBeforeFieldInitMember(method, inputs)) return false;
        foreach (var field in type.Fields)
        {
            if (field.BackingData?.Field is not { RawFieldType: { } raw } definition ||
                !ReferenceEquals(field.DeclaringType, type) ||
                !X64OriginalReferenceClassProof.OriginalField(app, definition) ||
                !X64ScalarStaticConstructorProof.OrdinaryField(field) ||
                !ReferenceEquals(app.ResolveIl2CppType(raw), field.FieldType) ||
                !X64ScalarStaticConstructorProof.CaptureDescriptor(app, raw, inputs)) return false;
            inputs.AddRange([field, definition, field.Name, field.Attributes, field.Offset, field.FieldType,
                definition.nameIndex, definition.typeIndex.Value, definition.token]);
        }
        return true;
    }

    private static bool CaptureBeforeFieldInitMember(MethodAnalysisContext method, List<object> inputs)
    {
        var app = method.AppContext;
        if (!OriginalTailBinding(method, app, method.UnderlyingPointer) ||
            method.Definition is not { RawReturnType: { } raw } original ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !X64ScalarStaticConstructorProof.CaptureDescriptor(app, raw, inputs)) return false;
        inputs.AddRange([method, original, method.Name, method.Attributes, method.ImplAttributes,
            method.ReturnType, method.UnderlyingPointer, original.MethodIndex.Value, original.nameIndex,
            original.declaringTypeIdx.Value, original.returnTypeIdx.Value, original.parameterStart.Value,
            original.genericContainerIndex.Value, original.token, original.flags, original.iflags,
            original.slot, original.parameterCount, method.GenericParameters.Count]);
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Definition is not { RawType: { } parameterRaw } definition ||
                !X64ScalarStaticConstructorProof.CaptureDescriptor(app, parameterRaw, inputs)) return false;
            inputs.AddRange([parameter, definition, parameter.Name, parameter.Attributes, parameter.ParameterType,
                definition.nameIndex, definition.typeIndex.Value, definition.token]);
        }
        return true;
    }

    private static bool CaptureBeforeFieldInitAliases(ApplicationAnalysisContext app, ulong pointer,
        X64GenericMethodTableProof.Evidence tables, List<object> inputs, bool unique)
    {
        if (!app.MethodsByAddress.TryGetValue(pointer, out var cached) || cached.Count == 0 ||
            cached.Any(alias => alias == null) || cached.Distinct().Count() != cached.Count ||
            Contains(tables.Invokers) || Contains(tables.AdjustorThunks)) return false;
        var original = new HashSet<MethodAnalysisContext>();
        foreach (var definition in app.Metadata.methodDefs)
        {
            if (definition.MethodPointer != pointer) continue;
            if (app.ResolveContextForMethod(definition) is not { } method ||
                !ReferenceEquals(method.Definition, definition) || !original.Add(method) ||
                !CaptureBeforeFieldInitMember(method, inputs)) return false;
        }
        var references = app.Binary.ConcreteGenericMethods.Values.SelectMany(group => group)
            .Where(reference => reference.GenericVariantPtr == pointer).ToArray();
        var byTable = new Dictionary<int, Cpp2IlMethodRef>();
        foreach (var reference in references)
        {
            if (!app.Binary.TryGetGenericMethodRegistration(reference, out var origin) ||
                !X64OriginalReferenceClassProof.OriginalGenericMethodReference(app, reference, pointer) ||
                byTable.ContainsKey(origin.TableIndex)) return false;
            byTable.Add(origin.TableIndex, reference);
        }
        var consumedRows = 0;
        foreach (var row in tables.Functions)
        {
            var ordinal = row.Origin.MethodPointerIndex;
            if (ordinal < 0 || ordinal >= tables.MethodPointers.Length || tables.MethodPointers[ordinal] != pointer)
                continue;
            if (!byTable.TryGetValue(row.Origin.Index, out var reference) ||
                !app.ConcreteGenericMethodsByRef.TryGetValue(reference, out var context) ||
                !ReferenceEquals(context.MethodRef, reference) || !original.Add(context) ||
                !OriginalConcreteTailBinding(context, app, pointer)) return false;
            consumedRows++;
            inputs.AddRange([row.Origin, reference, context, context.BaseMethodContext, context.DeclaringType!,
                context.Name, context.Attributes, context.ImplAttributes, context.ReturnType,
                reference.BaseMethod, reference.GenericVariantPtr, reference.TypeGenericParams.Length]);
            foreach (var raw in reference.TypeGenericParams)
                if (!X64ScalarStaticConstructorProof.CaptureDescriptor(app, raw, inputs)) return false;
        }
        if (consumedRows != byTable.Count || !original.SetEquals(cached) || unique && original.Count != 1)
            return false;
        inputs.Add(original.Count);
        return true;

        bool Contains(ReadOnlySpan<ulong> pointers)
        {
            foreach (var value in pointers) if (value == pointer) return true;
            return false;
        }
    }

    private static bool CaptureBeforeFieldInitClassInitializer(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong export, List<object> inputs)
    {
        if (export != X64PeExportProof.Find(pe, unwind, "il2cpp_runtime_class_init") ||
            X64NativeInstructionReader.Read(pe, unwind, export, 1, 5) is not [var thunk] ||
            thunk.Code != Iced.Intel.Code.Jmp_rel32_64 || thunk.OpCount != 1 || thunk.Length != 5 ||
            unwind.ClassifySpan(export, thunk.NextIP).Kind != X64UnwindProof.SpanKind.NoEntry ||
            !CaptureBeforeFieldInitNative(pe, unwind, export, 5, inputs, executable: true)) return false;
        var target = thunk.NearBranchTarget;
        var span = unwind.ClassifySpan(target, checked(target + 1));
        return span.Start == target && span.End > target && span.End - target <= 4096 &&
            !X64NativeInstructionReader.HasInteriorManagedEntry(app, target, span.End) &&
            CaptureBeforeFieldInitNative(pe, unwind, target, checked((int)(span.End - target)), inputs, executable: true);
    }

    private static bool CaptureBeforeFieldInitNative(PE pe, X64UnwindProof.Index unwind, ulong address,
        int count, List<object> inputs, bool executable)
    {
        if (count <= 0 || address > ulong.MaxValue - (ulong)count ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, address, checked((uint)count))) return false;
        var first = pe.MapVirtualAddressToRaw(address, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - count ||
            pe.MapVirtualAddressToRaw(address + (ulong)count - 1, false) != first + count - 1) return false;
        var bytes = image.Slice(checked((int)first), count);
        if (executable && !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, bytes, address)) return false;
        inputs.Add(address); inputs.Add(Convert.ToBase64String(bytes.ToArray()));
        return true;
    }
}
