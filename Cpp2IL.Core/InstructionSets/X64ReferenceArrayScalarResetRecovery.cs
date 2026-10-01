using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ReferenceArrayScalarResetRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64ReferenceArrayScalarResetProof.TryAuthenticate(method, out var proof))
        {
            if (NativeRecoveryProofTracker.Has(method, X64ReferenceArrayScalarResetProof.EvidenceKey) ||
                method.GetExtraData<X64ReferenceArrayScalarResetProof.Proof>(X64ReferenceArrayScalarResetProof.EvidenceKey) != null)
                throw new InvalidOperationException("The admitted reference-array scalar reset evidence no longer matches its native inputs.");
            return false;
        }
        var body = new CilMethodBody { InitializeLocals = true, ComputeMaxStackOnBuild = true, VerifyLabelsOnBuild = true };
        definition.CilMethodBody = body;
        var array = new CilLocalVariable(proof.ArrayField.FieldType.ToTypeSignature());
        var index = new CilLocalVariable(method.AppContext.SystemTypes.SystemInt32Type.ToTypeSignature());
        var element = new CilLocalVariable(proof.ElementField.DeclaringType.ToTypeSignature());
        body.LocalVariables.Add(array);
        body.LocalVariables.Add(index);
        body.LocalVariables.Add(element);
        var il = body.Instructions;
        var reloaded = proof.Native.Mode == X64ReferenceArrayScalarResetProof.LoopMode.ReloadedLength;
        var loop = new CilInstruction(CilOpCodes.Ldloc, index);
        var store = new CilInstruction(CilOpCodes.Ldloc, index);
        var done = new CilInstruction(CilOpCodes.Ret);
        if (reloaded)
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldc_R4, 0.0f);
            il.Add(CilOpCodes.Stfld, proof.MarkerField!.ToFieldDescriptor());
        }
        il.Add(CilOpCodes.Ldc_I4_0);
        il.Add(CilOpCodes.Stloc, index);
        ReadArray();
        il.Add(loop);
        if (reloaded)
        {
            il.Add(CilOpCodes.Ldloc, array);
            il.Add(CilOpCodes.Ldlen);
            il.Add(CilOpCodes.Conv_I4);
            il.Add(CilOpCodes.Bge, new CilInstructionLabel(done));
            // The native length check and element access use distinct field reads.
            ReadArray();
        }
        else il.Add(CilOpCodes.Pop);
        il.Add(CilOpCodes.Ldloc, array);
        il.Add(CilOpCodes.Ldloc, index);
        il.Add(CilOpCodes.Ldelem_Ref);
        il.Add(CilOpCodes.Stloc, element);
        il.Add(CilOpCodes.Ldloc, element);
        il.Add(CilOpCodes.Brtrue, new CilInstructionLabel(store));
        il.Add(CilOpCodes.Newobj, proof.NullConstructor.ToMethodDescriptor());
        il.Add(CilOpCodes.Throw);
        // Both bodies increment their private index before the evidenced field store.
        il.Add(store);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stloc, index);
        il.Add(CilOpCodes.Ldloc, element);
        if (reloaded) il.Add(CilOpCodes.Ldc_I4_0);
        else il.Add(CilOpCodes.Ldc_R4, 0.0f);
        il.Add(CilOpCodes.Stfld, proof.ElementField.ToFieldDescriptor());
        if (reloaded)
        {
            ReadArray();
            il.Add(CilOpCodes.Br, new CilInstructionLabel(loop));
        }
        else
        {
            il.Add(CilOpCodes.Ldloc, index);
            il.Add(CilOpCodes.Ldc_I4, proof.Native.Count);
            il.Add(CilOpCodes.Blt, new CilInstructionLabel(loop));
        }
        il.Add(done);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;

        void ReadArray()
        {
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldfld, proof.ArrayField.ToFieldDescriptor());
            il.Add(CilOpCodes.Stloc, array);
        }
    }
}
