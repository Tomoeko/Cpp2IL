using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64SignedFieldComparisonRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64SignedFieldComparisonProof.TryAuthenticate(method, out var proof))
        {
            if (NativeRecoveryProofTracker.Has(method, X64SignedFieldComparisonProof.EvidenceKey) ||
                method.GetExtraData<X64SignedFieldComparisonProof.Proof>(X64SignedFieldComparisonProof.EvidenceKey) != null)
                throw new InvalidOperationException("The admitted signed field comparison no longer matches its native inputs.");
            return false;
        }
        var body = new CilMethodBody { InitializeLocals = true, ComputeMaxStackOnBuild = true, VerifyLabelsOnBuild = true };
        definition.CilMethodBody = body;
        var second = new CilLocalVariable(method.AppContext.SystemTypes.SystemInt32Type.ToTypeSignature());
        var first = proof.Native.CapturesFields ? new CilLocalVariable(second.VariableType) : null;
        body.LocalVariables.Add(second);
        if (first != null) body.LocalVariables.Add(first);
        var checkSecond = new CilInstruction(CilOpCodes.Ldarg_2);
        var read = new CilInstruction(proof.Native.CapturesFields ? CilOpCodes.Ldarg_1 : CilOpCodes.Ldarg_2);
        var less = new CilInstruction(CilOpCodes.Ldc_I4_M1);
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Brtrue, new CilInstructionLabel(checkSecond));
        il.Add(CilOpCodes.Newobj, proof.NullConstructor.ToMethodDescriptor());
        il.Add(CilOpCodes.Throw);
        il.Add(checkSecond);
        il.Add(CilOpCodes.Brtrue, new CilInstructionLabel(read));
        il.Add(CilOpCodes.Newobj, proof.NullConstructor.ToMethodDescriptor());
        il.Add(CilOpCodes.Throw);
        il.Add(read);
        if (first != null)
        {
            il.Add(CilOpCodes.Ldfld, proof.Field.ToFieldDescriptor());
            il.Add(CilOpCodes.Stloc, first);
            il.Add(CilOpCodes.Ldarg_2);
        }
        il.Add(CilOpCodes.Ldfld, proof.Field.ToFieldDescriptor());
        il.Add(CilOpCodes.Stloc, second);
        if (first != null) il.Add(CilOpCodes.Ldloc, first);
        else
        {
            il.Add(CilOpCodes.Ldarg_1);
            il.Add(CilOpCodes.Ldfld, proof.Field.ToFieldDescriptor());
        }
        il.Add(CilOpCodes.Ldloc, second);
        il.Add(CilOpCodes.Blt, new CilInstructionLabel(less));
        if (first != null) il.Add(CilOpCodes.Ldloc, first);
        else
        {
            // This native mode reloads both operands for greater-than. Keep
            // its second read round rather than substituting cached values.
            il.Add(CilOpCodes.Ldarg_2);
            il.Add(CilOpCodes.Ldfld, proof.Field.ToFieldDescriptor());
            il.Add(CilOpCodes.Stloc, second);
            il.Add(CilOpCodes.Ldarg_1);
            il.Add(CilOpCodes.Ldfld, proof.Field.ToFieldDescriptor());
        }
        il.Add(CilOpCodes.Ldloc, second);
        il.Add(CilOpCodes.Cgt);
        il.Add(CilOpCodes.Ret);
        il.Add(less);
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
