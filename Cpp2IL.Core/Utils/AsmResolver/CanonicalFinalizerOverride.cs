using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Utils.AsmResolver;

/// <summary>
/// Preserves the canonical Object.Finalize relationship established by a runtime virtual slot.
/// This does not reconstruct arbitrary explicit MethodImpl rows: an implicit class override
/// generally does not reveal whether its original managed assembly contained such a row.
/// </summary>
internal static class CanonicalFinalizerOverride
{
    public static void AddTo(TypeDefinition managedType, TypeAnalysisContext type)
    {
        var relationship = Resolve(type);
        if (relationship == null)
            return;

        var (body, declaration) = relationship.Value;
        var managedBody = body.GetExtraData<MethodDefinition>("AsmResolverMethod")
                          ?? throw new System.InvalidOperationException("The evidenced finalizer has no managed method definition.");
        var managedDeclaration = (IMethodDefOrRef)declaration.ToMethodDescriptor();
        if (!managedType.MethodImplementations.Any(implementation => implementation.Body == managedBody &&
                implementation.Declaration?.FullName == managedDeclaration.FullName))
            managedType.MethodImplementations.Add(new MethodImplementation(managedDeclaration, managedBody));
    }

    internal static (MethodAnalysisContext Body, MethodAnalysisContext Declaration)? Resolve(TypeAnalysisContext type)
    {
        if (type.Definition is not { HasFinalizer: true } definition || type.IsInterface || type.IsValueType)
            return null;

        var systemObject = type.AppContext.SystemTypes.SystemObjectType;
        if (type == systemObject || !InheritsFrom(type, systemObject))
            return null;

        var declarations = systemObject.Methods.Where(HasCanonicalSignature).ToArray();
        var bodies = type.Methods.Where(HasCanonicalSignature).ToArray();
        if (declarations.Length != 1 || bodies.Length != 1)
            return null;

        var body = bodies[0];
        var declaration = declarations[0];
        var nativeBody = body.Definition!;
        var slot = nativeBody.slot;
        if ((body.Attributes & MethodAttributes.NewSlot) != 0 || slot == ushort.MaxValue ||
            slot != declaration.Definition!.slot)
            return null;

        var vtable = definition.VTable;
        if (slot >= vtable.Length || vtable[slot] is not { Type: MetadataUsageType.MethodDef } entry ||
            entry.AsMethod() != nativeBody)
            return null;

        return (body, declaration);
    }

    private static bool HasCanonicalSignature(MethodAnalysisContext method) =>
        method.Definition != null && method.DefaultName == "Finalize" &&
        (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Family &&
        (method.Attributes & (MethodAttributes.Virtual | MethodAttributes.Static | MethodAttributes.Abstract)) == MethodAttributes.Virtual &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
        method.ReturnType == method.AppContext.SystemTypes.SystemVoidType;

    private static bool InheritsFrom(TypeAnalysisContext type, TypeAnalysisContext ancestor)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = type.BaseType; current != null && seen.Add(current); current = current.BaseType)
        {
            if (current == ancestor)
                return true;
        }
        return false;
    }
}
