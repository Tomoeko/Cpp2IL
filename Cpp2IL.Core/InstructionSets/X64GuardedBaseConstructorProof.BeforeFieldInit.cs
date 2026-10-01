using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedBaseConstructorProof
{
    internal const string BeforeFieldInitEvidenceKey = "X64BeforeFieldInitBaseConstructorProof";

    internal sealed class BeforeFieldInitEvidence
    {
        private readonly object[] _inputs;
        private readonly Instruction[] _body;
        private readonly X64GenericMethodTableProof.Evidence _tables;
        private readonly X64ScalarStaticConstructorProof.Evidence _initializer;
        internal MethodAnalysisContext Method { get; }
        internal MethodAnalysisContext BaseConstructor { get; }
        internal MethodAnalysisContext ObjectConstructor { get; }
        internal MethodAnalysisContext ClassConstructor => _initializer.Method;
        internal FieldAnalysisContext StaticField => _initializer.StaticField;
        internal uint InitializerValueBits => _initializer.ValueBits;
        internal Shape Native { get; }
        internal ReadOnlySpan<Instruction> Body => _body;

        internal BeforeFieldInitEvidence(MethodAnalysisContext method, MethodAnalysisContext baseConstructor,
            MethodAnalysisContext objectConstructor, Shape native, Instruction[] body, List<object> inputs, X64GenericMethodTableProof.Evidence tables,
            X64ScalarStaticConstructorProof.Evidence initializer)
        {
            Method = method;
            BaseConstructor = baseConstructor;
            ObjectConstructor = objectConstructor;
            Native = native;
            _body = body.ToArray();
            _inputs = inputs.ToArray();
            _tables = tables;
            _initializer = initializer;
        }

        internal bool IsUnchanged() => Method.AppContext.Binary is PE pe &&
            X64UnwindProof.ForApplication(Method.AppContext) is { } unwind &&
            _tables.Matches(Method.AppContext, pe, unwind) && _initializer.IsUnchanged() &&
            FindBeforeFieldInit(Method) is { } current && Native == current.Native &&
            ReferenceEquals(BaseConstructor, current.BaseConstructor) &&
            ReferenceEquals(ClassConstructor, current.ClassConstructor) && ReferenceEquals(ObjectConstructor, current.ObjectConstructor) &&
            _body.SequenceEqual(current._body) && _inputs.SequenceEqual(current._inputs);
    }

    // The immediate base call is retained. No RunClassConstructor call is
    // invented: the original BeforeFieldInit declaration owns initialization
    // timing, and the target compiler supplies the native constructor guard.
    internal static BeforeFieldInitEvidence? FindBeforeFieldInit(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                app.Binary is not PE { PointerSizeBytes: 8 } pe ||
                !pe.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { Definition: { HasCctor: false } } owner ||
                !OrdinaryConstructor(method, owner, app) ||
                owner.BaseType is not { Definition: { HasCctor: true } } immediateBase ||
                !ReferenceEquals(immediateBase.BaseType, app.SystemTypes.SystemObjectType) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(owner.DeclaringAssembly, immediateBase.DeclaringAssembly) ||
                (owner.Attributes & TypeAttributes.BeforeFieldInit) == 0 ||
                (immediateBase.Attributes & TypeAttributes.BeforeFieldInit) == 0 ||
                owner.Fields.Any(field => !field.IsStatic) || immediateBase.Fields.Any(field => !field.IsStatic) ||
                owner.Methods.Where(candidate => candidate.Name == ".ctor").ToArray() is not [var selected] ||
                !ReferenceEquals(selected, method) || !OnlyConstructor(immediateBase, app, out var baseConstructor) ||
                immediateBase.Methods.Where(candidate => candidate.Name == ".cctor").ToArray() is not [var cctor] ||
                X64ScalarStaticConstructorProof.Find(cctor) is not { } initializer ||
                !TryFindBody(method, pe, unwind, out var shape) ||
                !BindGuard(method, pe, unwind, shape, immediateBase) ||
                pe.ReadPointerAtVirtualAddress(shape.TypeInfoSlot) !=
                    ((ulong)MetadataUsageType.TypeInfo << 29 | (ulong)immediateBase.Definition.ByvalTypeIndex.Value << 1 | 1) ||
                !X64MetadataInitializationHelperProof.TryCaptureTypeInfoInput(app, pe, unwind, shape.MetadataInitializer, out var helper) ||
                X64GenericMethodTableProof.TryIdentify(app, pe, unwind) is not { } tables)
                return null;

            baseConstructor.EnsureRawBytes();
            var baseBody = X86Utils.Iterate(baseConstructor).ToArray();
            if (X64ObjectConstructorThunkProof.Find(baseConstructor, baseBody) is not { } objectConstructor ||
                !ReferenceEquals(objectConstructor.DeclaringType, app.SystemTypes.SystemObjectType) ||
                objectConstructor.UnderlyingPointer != shape.Tail ||
                baseBody.Length != 2 || baseConstructor.RawBytes.Length != 7)
                return null;

            var inputs = new List<object> { app, app.LibCpp2IlContext, app.Metadata, pe, shape, helper };
            if (!CaptureBeforeFieldInitType(owner, inputs) || !CaptureBeforeFieldInitType(immediateBase, inputs) ||
                !CaptureBeforeFieldInitMember(method, inputs) || !CaptureBeforeFieldInitMember(baseConstructor, inputs) ||
                !CaptureBeforeFieldInitMember(objectConstructor, inputs) ||
                !CaptureBeforeFieldInitAliases(app, method.UnderlyingPointer, tables, inputs, unique: true) ||
                !CaptureBeforeFieldInitAliases(app, baseConstructor.UnderlyingPointer, tables, inputs, unique: false) ||
                !CaptureBeforeFieldInitNative(pe, unwind, shape.TypeInfoSlot, 8, inputs, executable: false) ||
                !CaptureBeforeFieldInitClassInitializer(app, pe, unwind, shape.ClassInitializer, inputs) ||
                !CaptureBeforeFieldInitNative(pe, unwind, method.UnderlyingPointer, 73, inputs, executable: true) ||
                !CaptureBeforeFieldInitNative(pe, unwind, baseConstructor.UnderlyingPointer, 7, inputs, executable: true) ||
                !CaptureBeforeFieldInitNative(pe, unwind, objectConstructor.UnderlyingPointer, 3, inputs, executable: true))
                return null;
            inputs.Add(Convert.ToBase64String(method.RawBytes.AsSpan().ToArray()));
            inputs.Add(Convert.ToBase64String(baseConstructor.RawBytes.AsSpan().ToArray()));
            inputs.Add(Convert.ToBase64String(objectConstructor.RawBytes.AsSpan().ToArray()));
            return new(method, baseConstructor, objectConstructor, shape, X86Utils.Iterate(method).ToArray(), inputs, tables, initializer);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }
}
