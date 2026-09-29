using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits the proved single-capture, null-guarded delegate invocation.</summary>
internal static class X64NullableDelegateFieldTailRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64NullableDelegateFieldTailProof.Find(method,
                X86Utils.Iterate(method).ToArray()) is not { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true,
        };
        definition.CilMethodBody = body;
        var capture = new CilLocalVariable(
            evidence.Field.FieldType.ToTypeSignature());
        body.LocalVariables.Add(capture);
        var instructions = body.Instructions;
        var end = new CilInstruction(CilOpCodes.Ret);
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Ldfld,
            evidence.Field.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Stloc, capture);
        instructions.Add(CilOpCodes.Ldloc, capture);
        instructions.Add(CilOpCodes.Brfalse,
            new CilInstructionLabel(end));
        instructions.Add(CilOpCodes.Ldloc, capture);
        instructions.Add(CilOpCodes.Callvirt,
            evidence.Invoke.ToMethodDescriptor());
        instructions.Add(end);
        return true;
    }
}
