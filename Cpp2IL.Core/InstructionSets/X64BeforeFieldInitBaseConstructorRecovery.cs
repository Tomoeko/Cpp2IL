using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64BeforeFieldInitBaseConstructorRecovery
{
    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey) ||
        method.GetExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> native)
    {
        if (X64GuardedBaseConstructorProof.FindBeforeFieldInit(method) is not { } proof ||
            !native.SequenceEqual(proof.Body.ToArray()) ||
            HasEvidence(method) && (method.GetExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(
                X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey) is not { } saved || !saved.IsUnchanged())) return null;
        method.PutExtraData(X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey);
        return
        [new(0, OpCode.CallVoid, proof.BaseConstructor, new Register(null, "rcx")) { NativeAddress = native[^1].IP },
            new(1, OpCode.Return) { NativeAddress = native[^1].IP }];
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (method.GetExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(
                    X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey) is not { } proof || !proof.IsUnchanged() ||
                !NativeStraightLineGraph.TryGetBody(method, out var body) || body is not [var call, var returned] ||
                call.OpCode != OpCode.CallVoid || call.IntegerBitWidth != 0 || call.CallSemantics != CallSemantics.Direct ||
                call.NativeAddress != proof.Body[^1].IP ||
                call.Operands is not [MethodAnalysisContext target, LocalVariable receiver] ||
                !ReferenceEquals(target, proof.BaseConstructor) || !receiver.IsThis || receiver.IsMethodInfo ||
                !ReferenceEquals(receiver.Type, method.DeclaringType) ||
                method.ParameterLocals.Where(local => local.IsThis).ToArray() is not [var originalReceiver] ||
                !ReferenceEquals(receiver, originalReceiver) ||
                returned.OpCode != OpCode.Return || returned.IntegerBitWidth != 0 ||
                returned.CallSemantics != CallSemantics.Direct || returned.NativeAddress != proof.Body[^1].IP ||
                returned.Operands.Count != 0 || method.NullCheckedFieldAccesses.Count != 0 ||
                method.NullArmFieldProbes.Count != 0 || method.InlinedBooleanSetters.Count != 0) return false;
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or NullReferenceException)
        {
            return false;
        }
    }

    internal static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition output) =>
        X64GuardedBaseConstructorProof.MatchesBeforeFieldInitOutput(method, output);

    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition output)
    {
        if (!IsValidFor(method) || !MatchesOutput(method, output) ||
            method.GetExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(
                X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey) is not { } proof) return false;
        var body = new CilMethodBody { ComputeMaxStackOnBuild = true, VerifyLabelsOnBuild = true };
        body.Instructions.Add(CilOpCodes.Ldarg_0);
        body.Instructions.Add(CilOpCodes.Call, proof.BaseConstructor.ToMethodDescriptor());
        body.Instructions.Add(CilOpCodes.Ret);
        var previous = output.CilMethodBody;
        output.CilMethodBody = body;
        try { body.VerifyLabels(); body.ComputeMaxStack(); }
        catch { output.CilMethodBody = previous; throw; }
        return true;
    }
}
