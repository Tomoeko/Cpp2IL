using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64LookupGuardManagedThrowRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64LookupGuardManagedThrowProof.Find(method) is not { } proof)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var record = new CilLocalVariable(proof.Lookup.ReturnType.ToTypeSignature());
        var formattedKey = new CilLocalVariable(method.AppContext.SystemTypes.SystemStringType.ToTypeSignature());
        body.LocalVariables.Add(record);
        body.LocalVariables.Add(formattedKey);
        var failure = new CilInstruction(CilOpCodes.Ldarga, definition.Parameters[0]);
        var instructions = body.Instructions;
        instructions.Add(CilOpCodes.Call, proof.Producer.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldarg_1);
        // This callvirt supplies the exact runtime receiver check before any
        // lookup effect. The target itself is independently bound as nonvirtual.
        instructions.Add(CilOpCodes.Callvirt, proof.Lookup.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Stloc, record);
        instructions.Add(CilOpCodes.Ldloc, record);
        instructions.Add(CilOpCodes.Brfalse, new CilInstructionLabel(failure));
        instructions.Add(CilOpCodes.Ldloc, record);
        if (proof.StringGetter is { } getter)
            instructions.Add(CilOpCodes.Call, getter.ToMethodDescriptor());
        else
            instructions.Add(CilOpCodes.Ldfld, proof.StringField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Ret);
        instructions.Add(failure);
        // Keep the original parameter as an Int32 address receiver. Replacing
        // this call with a formatted constant would lose culture and edge behavior.
        instructions.Add(CilOpCodes.Call, proof.IntegerFormatter.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Stloc, formattedKey);
        instructions.Add(CilOpCodes.Ldstr, proof.Literal);
        instructions.Add(CilOpCodes.Ldloc, formattedKey);
        instructions.Add(CilOpCodes.Call, proof.Concat.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Newobj, proof.Constructor.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Throw);
        body.VerifyLabels();
        body.ComputeMaxStack();
        return true;
    }
}
