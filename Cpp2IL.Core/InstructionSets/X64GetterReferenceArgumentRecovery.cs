using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64GetterReferenceArgumentRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64GetterReferenceArgumentProof.TryAuthenticate(method, out var proof))
        {
            if (NativeRecoveryProofTracker.Has(method, X64GetterReferenceArgumentProof.EvidenceKey) ||
                method.GetExtraData<X64GetterReferenceArgumentProof.Proof>(X64GetterReferenceArgumentProof.EvidenceKey) != null)
                throw new InvalidOperationException("The admitted getter reference argument no longer matches its native inputs.");
            return false;
        }
        var body = new CilMethodBody { InitializeLocals = true, ComputeMaxStackOnBuild = true, VerifyLabelsOnBuild = true };
        definition.CilMethodBody = body;
        var source = new CilLocalVariable(proof.SourceField.FieldType.ToTypeSignature());
        var target = new CilLocalVariable(proof.TargetField.FieldType.ToTypeSignature());
        body.LocalVariables.Add(source);
        body.LocalVariables.Add(target);
        var il = body.Instructions;
        var sourceNonNull = new CilInstruction(CilOpCodes.Ldarg_0);
        var targetNonNull = new CilInstruction(CilOpCodes.Ldloc, target);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, proof.SourceField.ToFieldDescriptor());
        il.Add(CilOpCodes.Stloc, source);
        il.Add(CilOpCodes.Ldloc, source);
        il.Add(CilOpCodes.Brtrue, new CilInstructionLabel(sourceNonNull));
        // Callvirt on this proved null arm throws through the target runtime's
        // implicit NullCheck. Neither the accessor body nor a managed exception
        // constructor can execute here.
        il.Add(CilOpCodes.Ldloc, source);
        il.Add(CilOpCodes.Callvirt, proof.Getter.ToMethodDescriptor());
        il.Add(CilOpCodes.Pop);
        il.Add(CilOpCodes.Ret);
        il.Add(sourceNonNull);
        il.Add(CilOpCodes.Ldfld, proof.TargetField.ToFieldDescriptor());
        il.Add(CilOpCodes.Stloc, target);
        il.Add(CilOpCodes.Ldloc, target);
        il.Add(CilOpCodes.Brtrue, new CilInstructionLabel(targetNonNull));
        il.Add(CilOpCodes.Ldloc, target);
        il.Add(CilOpCodes.Ldnull);
        il.Add(CilOpCodes.Callvirt, proof.Callee.ToMethodDescriptor());
        il.Add(CilOpCodes.Ret);
        // The payload is still read after both native guards. This call is an
        // equivalent accessible field read, not a fabricated native CALL site.
        il.Add(targetNonNull);
        il.Add(CilOpCodes.Ldloc, source);
        il.Add(CilOpCodes.Call, proof.Getter.ToMethodDescriptor());
        il.Add(CilOpCodes.Callvirt, proof.Callee.ToMethodDescriptor());
        il.Add(CilOpCodes.Ret);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
