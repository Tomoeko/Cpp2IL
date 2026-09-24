using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Lifts a generated iterator constructor only when an independently proved
/// factory binds it to the exact native Object constructor call and state field.
/// Shared no-op native bodies can otherwise misidentify that call as Dispose.
/// </summary>
internal static class X64IteratorConstructorProof
{
    internal sealed record Evidence(MethodAnalysisContext BaseConstructor,
        FieldAnalysisContext State, MethodAnalysisContext Factory);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        return
        [
            new(0, ISIL.OpCode.CallVoid, proof.BaseConstructor, receiver),
            new(1, ISIL.OpCode.Move, new ISIL.MemoryOperand(receiver, null, proof.State.Offset),
                new ISIL.Register(null, "rdx")),
            new(2, ISIL.OpCode.Return),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.Name != ".ctor" || method.IsStatic || method.Parameters.Count != 1 ||
            method.DeclaringType is not { } iterator ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            decoded.Count != 12 || decoded[0].IP != method.UnderlyingPointer)
            return null;

        var baseConstructors = method.AppContext.SystemTypes.SystemObjectType.Methods
            .Where(candidate => candidate.Name == ".ctor" && !candidate.IsStatic &&
                candidate.Parameters.Count == 0 && candidate.IsVoid &&
                candidate.UnderlyingPointer == decoded[6].NearBranchTarget).ToArray();
        if (baseConstructors is not [{ } baseConstructor])
            return null;

        // A factory can live on a containing type or on a separately declared
        // owner captured by the iterator. These metadata edges bound the search.
        var owners = iterator.Fields.Where(field => !field.IsStatic &&
                !ReferenceEquals(field.FieldType, iterator) &&
                ReferenceEquals(field.FieldType.DeclaringAssembly, iterator.DeclaringAssembly))
            .Select(field => field.FieldType)
            .Concat(iterator.DeclaringType is { } parent ? [parent] : [])
            .Distinct();
        var factories = owners.SelectMany(type => type.Methods)
            .Where(candidate => !candidate.IsStatic &&
                candidate.Name != ".ctor" && candidate.Parameters.Count == 0 &&
                candidate.UnderlyingPointer != 0 && !candidate.IsVoid &&
                candidate.ReturnType.FullName == "System.Collections.IEnumerator")
            .Select(candidate =>
            {
                candidate.EnsureRawBytes();
                var proof = X64IteratorFactoryProof.Find(candidate,
                    X86Utils.Iterate(candidate).ToArray());
                return (candidate, proof);
            })
            .Where(item => ReferenceEquals(item.proof?.Constructor, method))
            .ToArray();
        if (factories is not [{ candidate: var factory, proof: { } factoryProof }] ||
            !X64IteratorFactoryProof.TryProveConstructorShape(decoded,
                factoryProof.State.Offset, baseConstructor.UnderlyingPointer))
            return null;

        return new Evidence(baseConstructor, factoryProof.State, factory);
    }
}
