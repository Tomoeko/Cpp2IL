using AsmResolver.DotNet;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void LoadInstanceCallReceiver(IOperand receiver, MethodAnalysisContext target,
        MethodDefinition method, EmissionLocals locals)
    {
        var owner = target.DeclaringType ??
            throw new DecompilerException("Instance call target has no declaring type");
        if (!owner.IsValueType)
        {
            LoadOperand(receiver, method, locals, owner);
            return;
        }

        // A value-type instance call consumes a managed pointer, not a copy of
        // the value. Preserve an already explicit address or byref receiver.
        if ((receiver is AddressOf { Target: LocalVariable { Type: { } addressedType } } &&
                ReferenceEquals(addressedType, owner)) ||
            (receiver is LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } elementType } } &&
                ReferenceEquals(elementType, owner)))
        {
            LoadOperand(receiver, method, locals);
            return;
        }

        if (receiver is not LocalVariable { Type: { } receiverType } local ||
            !ReferenceEquals(receiverType, owner) || !locals.Parameters.ContainsKey(local))
            throw new DecompilerException("Value-type instance receiver has no proved managed storage");

        // The managed 'this' argument of a struct method is itself a byref.
        if (local.IsThis)
        {
            if (locals.Context.IsStatic || !ReferenceEquals(locals.Context.DeclaringType, owner))
                throw new DecompilerException("Value-type instance receiver has a changed this type");
            LoadOperand(local, method, locals);
            return;
        }

        // A by-value managed argument owns addressable storage for this call.
        // Do not take the address of an inferred SSA copy or a retyped argument.
        if (!locals.ParameterContexts.TryGetValue(local, out var parameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, locals.Context) ||
            parameter.ParameterIndex < 0 ||
            parameter.ParameterIndex >= locals.Context.Parameters.Count ||
            !ReferenceEquals(locals.Context.Parameters[parameter.ParameterIndex], parameter) ||
            parameter.IsRef || parameter.OverrideParameterType != null ||
            parameter.OverrideAttributes != null || parameter.UseOverrideDefaultValue ||
            !ReferenceEquals(parameter.ParameterType, owner) ||
            !ReferenceEquals(parameter.DefaultParameterType, owner) ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.Definition is { RawType: not { NumMods: 0, Byref: 0, Pinned: 0 } })
            throw new DecompilerException("Value-type instance receiver has no unchanged by-value parameter");

        LoadOperand(new AddressOf(local), method, locals);
    }
}
