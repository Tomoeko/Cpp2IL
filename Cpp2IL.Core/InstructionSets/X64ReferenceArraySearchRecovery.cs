using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ReferenceArraySearchRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64ReferenceArraySearchProof.TryAuthenticate(method, out var proof))
        {
            if (NativeRecoveryProofTracker.Has(method, X64ReferenceArraySearchProof.EvidenceKey) ||
                method.GetExtraData<X64ReferenceArraySearchProof.Proof>(X64ReferenceArraySearchProof.EvidenceKey) != null)
                throw new InvalidOperationException("The admitted reference-array search evidence no longer matches its native inputs.");
            return false;
        }
        var body = new CilMethodBody { InitializeLocals = true, ComputeMaxStackOnBuild = true, VerifyLabelsOnBuild = true };
        definition.CilMethodBody = body;
        var array = new CilLocalVariable(proof.ArrayField.FieldType.ToTypeSignature());
        var index = new CilLocalVariable(method.AppContext.SystemTypes.SystemInt32Type.ToTypeSignature());
        var element = proof.Native.CapturesElement ? new CilLocalVariable(proof.KeyField.DeclaringType.ToTypeSignature()) : null;
        var length = proof.Native.CapturesElement ? new CilLocalVariable(index.VariableType) : null;
        body.LocalVariables.Add(array);
        body.LocalVariables.Add(index);
        if (element != null) body.LocalVariables.Add(element);
        if (length != null) body.LocalVariables.Add(length);
        var loop = new CilInstruction(CilOpCodes.Ldloc, index);
        var increment = new CilInstruction(CilOpCodes.Ldloc, index);
        var missing = new CilInstruction(CilOpCodes.Ldc_I4_M1);
        var result = new CilInstruction(CilOpCodes.Ldloc, index);
        var il = body.Instructions;
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, proof.ArrayField.ToFieldDescriptor());
        il.Add(CilOpCodes.Stloc, array);
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Stloc, index);
        il.Add(CilOpCodes.Ldloc, array);
        il.Add(CilOpCodes.Brfalse, new CilInstructionLabel(missing));
        if (length != null)
        {
            il.Add(CilOpCodes.Ldloc, array);
            il.Add(CilOpCodes.Ldlen);
            il.Add(CilOpCodes.Conv_I4);
            il.Add(CilOpCodes.Stloc, length);
        }
        il.Add(loop);
        if (length != null) il.Add(CilOpCodes.Ldloc, length);
        else
        {
            il.Add(CilOpCodes.Ldloc, array);
            il.Add(CilOpCodes.Ldlen);
            il.Add(CilOpCodes.Conv_I4);
        }
        il.Add(CilOpCodes.Bge, new CilInstructionLabel(missing));
        il.Add(CilOpCodes.Ldloc, array);
        il.Add(CilOpCodes.Ldloc, index);
        il.Add(CilOpCodes.Ldelem_Ref);
        if (element != null)
        {
            il.Add(CilOpCodes.Stloc, element);
            il.Add(CilOpCodes.Ldloc, element);
        }
        il.Add(CilOpCodes.Brfalse, new CilInstructionLabel(increment));
        if (element != null) il.Add(CilOpCodes.Ldloc, element);
        else
        {
            // Retain the second read when the native body does not capture the
            // first element. Coalescing it would erase an evidenced access.
            il.Add(CilOpCodes.Ldloc, array);
            il.Add(CilOpCodes.Ldloc, index);
            il.Add(CilOpCodes.Ldelem_Ref);
        }
        il.Add(CilOpCodes.Ldfld, proof.KeyField.ToFieldDescriptor());
        il.Add(CilOpCodes.Ldarg_1);
        il.Add(CilOpCodes.Beq, new CilInstructionLabel(result));
        il.Add(increment);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stloc, index);
        il.Add(CilOpCodes.Br, new CilInstructionLabel(loop));
        il.Add(missing);
        il.Add(CilOpCodes.Stloc, index);
        il.Add(result);
        if (proof.Native.ReturnsBoolean)
        {
            il.Add(CilOpCodes.Ldc_I4_0);
            il.Add(CilOpCodes.Clt);
            il.Add(CilOpCodes.Ldc_I4_0);
            il.Add(CilOpCodes.Ceq);
        }
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
