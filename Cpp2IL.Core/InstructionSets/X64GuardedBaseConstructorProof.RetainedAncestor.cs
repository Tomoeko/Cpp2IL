using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedBaseConstructorProof
{
    // Construction retains its evidenced class-init guard. Ordinary instance
    // calls use the same original dependency without introducing another guard.
    internal static Evidence? FindRetainedAncestor(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { } owner ||
                !X64OriginalReferenceClassProof.IsValid(owner, retainAncestorInitializers: true) ||
                !OrdinaryConstructor(method, owner, app) ||
                !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
                owner.Methods.Where(candidate => candidate.Name == ".ctor").ToArray() is not [var selected] ||
                !ReferenceEquals(selected, method) || owner.BaseType is not { Definition: { HasCctor: true } } immediateBase ||
                !ReferenceEquals(immediateBase.BaseType, app.SystemTypes.SystemObjectType) ||
                ReferenceEquals(owner.DeclaringAssembly, immediateBase.DeclaringAssembly) ||
                !OnlyConstructor(immediateBase, app, out var baseConstructor) ||
                !X64OriginalReferenceClassProof.InstanceConstructor(baseConstructor) ||
                !X64OriginalReferenceClassProof.OriginalMethodPointer(baseConstructor) ||
                !TryFindBody(method, pe, unwind, out var shape) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var callers) ||
                callers is not [var caller] || !ReferenceEquals(caller, method) ||
                !BindGuard(method, pe, unwind, shape, immediateBase) ||
                baseConstructor.UnderlyingPointer != shape.Tail ||
                !app.MethodsByAddress.TryGetValue(shape.Tail, out var aliases) ||
                aliases.Count == 0 || aliases.Count != aliases.Distinct().Count() ||
                aliases.Count(alias => ReferenceEquals(alias, baseConstructor)) != 1 ||
                aliases.Any(alias => !OriginalTailBinding(alias, app, shape.Tail)))
                return null;

            baseConstructor.EnsureRawBytes();
            var decoded = X86Utils.Iterate(baseConstructor).ToArray();
            // A large cached method span may contain padding and the next body.
            // The existing thunk reader authenticates the seven consumed bytes,
            // inert Object target, padding and distinct next unwind entry.
            var systemConstructor = X64ObjectConstructorThunkProof.Find(baseConstructor, decoded);
            return systemConstructor != null &&
                   ReferenceEquals(systemConstructor.DeclaringType, app.SystemTypes.SystemObjectType)
                ? new Evidence(baseConstructor) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or
                                          System.Collections.Generic.KeyNotFoundException or System.IO.EndOfStreamException)
        {
            return null;
        }
    }

    private static bool OriginalTailBinding(MethodAnalysisContext method, ApplicationAnalysisContext app, ulong pointer)
    {
        if (method is ConcreteGenericMethodAnalysisContext concrete)
            return OriginalConcreteTailBinding(concrete, app, pointer);
        if (!ReferenceEquals(method.AppContext, app) || method.UnderlyingPointer != pointer ||
            method.Definition is not { } definition || method.DeclaringType?.Definition is not { } owner ||
            !X64OriginalReferenceClassProof.OriginalType(app, owner) ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            definition.declaringTypeIdx.Value < 0 || definition.declaringTypeIdx.Value >= app.Metadata.TypeDefinitionCount ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            !method.DeclaringType.Methods.Contains(method) || method.Name != method.DefaultName ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.OverrideReturnType != null || !X64OriginalReferenceClassProof.ValidTypeIndex(app, definition.returnTypeIdx.Value) ||
            definition.RawReturnType is not { } raw || !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            definition.InternalParameterData is not { } parameters || parameters.Length != method.Parameters.Count ||
            definition.parameterCount != parameters.Length)
            return false;
        return !method.Parameters.Where((parameter, index) =>
            !ReferenceEquals(parameter.Definition, parameters[index]) ||
            !X64OriginalReferenceClassProof.OriginalParameter(app, definition, index, parameters[index]) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.ParameterIndex != index ||
            parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
            parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
            !X64OriginalReferenceClassProof.ValidTypeIndex(app, parameters[index].typeIndex.Value) ||
            parameters[index].RawType is not { } parameterRaw ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, parameterRaw)).Any();
    }

    private static bool OriginalConcreteTailBinding(ConcreteGenericMethodAnalysisContext method,
        ApplicationAnalysisContext app, ulong pointer)
    {
        // These aliases do not select the managed callee. The immediate original
        // base declaration and its independently inert native body do that.
        // Retain each original concrete registration rather than treating the
        // shared native address as a unique MethodDef.
        if (!ReferenceEquals(method.AppContext, app) || method.UnderlyingPointer != pointer ||
            method.MethodRef is not { } reference ||
            !X64OriginalReferenceClassProof.OriginalGenericMethodReference(app, reference, pointer) ||
            !app.ConcreteGenericMethodsByRef.TryGetValue(reference, out var original) ||
            !ReferenceEquals(original, method) ||
            !app.Binary.ConcreteGenericMethods.TryGetValue(reference.BaseMethod, out var registrations) ||
            registrations.Count(candidate => ReferenceEquals(candidate, reference)) != 1 ||
            method.BaseMethodContext is not { Definition: { } definition } baseMethod ||
            !ReferenceEquals(reference.BaseMethod, definition) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), baseMethod) ||
            method.Name != method.DefaultName || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes || method.OverrideReturnType != null ||
            method.Parameters.Count != 0 || definition.parameterCount != 0 ||
            reference.MethodGenericParams.Length != 0 || method.MethodGenericParameters.Count != 0 ||
            reference.TypeGenericParams.Length != method.TypeGenericParameters.Count ||
            !X64OriginalReferenceClassProof.InstanceConstructor(baseMethod))
            return false;
        return !reference.TypeGenericParams.Where((raw, index) =>
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            !ReferenceEquals(app.ResolveIl2CppType(raw), method.TypeGenericParameters[index])).Any();
    }
}
