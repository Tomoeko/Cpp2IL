using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Retains the complete initializer required by an ordinary scalar wrapper.</summary>
internal static class X64ScalarWrapperInitializationProof
{
    internal static bool TryCapture(TypeAnalysisContext owner, FieldAnalysisContext scalar, List<object> values)
    {
        if (owner.Definition is not { } definition) return false;
        var constructors = owner.Methods.Where(method => method.Name == ".cctor").ToArray();
        if (!definition.HasCctor)
        {
            values.Add("no-wrapper-initializer");
            return constructors.Length == 0;
        }
        if (constructors is not [{ } constructor] ||
            X64ScalarWrapperStaticConstructorProof.Find(constructor) is not { } proof ||
            !ReferenceEquals(proof.ScalarField, scalar) ||
            MethodAnalysisContext.MaxMethodSizeBytes != -1 &&
            constructor.RawBytes.Length > MethodAnalysisContext.MaxMethodSizeBytes)
            return false;

        X64SmallAggregateFieldGetterProof.CaptureMethod(constructor, values);
        X64SmallAggregateFieldGetterProof.CaptureRawType(constructor.Definition!.RawReturnType!, values);
        values.Add(proof.StaticField);
        values.Add(proof.ScalarField);
        values.Add(proof.ValueBits);
        foreach (var instruction in X86Utils.Iterate(constructor)) values.Add(instruction);
        foreach (var value in constructor.RawBytes.AsSpan()) values.Add(value);
        return true;
    }
}
