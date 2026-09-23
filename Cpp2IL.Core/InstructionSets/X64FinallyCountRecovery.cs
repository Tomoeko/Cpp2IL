using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Emits a managed finally only for the bounded signed-divide/counter shape
/// whose complete native body, EH4 cleanup, and metadata identities are proved.
/// </summary>
internal static class X64FinallyCountRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64FinallyCountBodyProof.Find(method) is not { } body)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var result = new CilLocalVariable(definition.DeclaringModule!.CorLibTypeFactory.Int32);
        il.LocalVariables.Add(result);
        var instructions = il.Instructions;
        var tryStart = new CilInstruction(CilOpCodes.Ldarg_0);
        var divide = new CilInstruction(CilOpCodes.Ldc_I4, body.Dividend);
        var finallyStart = new CilInstruction(CilOpCodes.Ldarg_1);
        var returnStart = new CilInstruction(CilOpCodes.Ldloc, result);

        instructions.Add(tryStart);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(divide));
        instructions.Add(CilOpCodes.Newobj, body.Constructor.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Throw);
        instructions.Add(divide);
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Div);
        instructions.Add(CilOpCodes.Stloc, result);
        instructions.Add(CilOpCodes.Leave, new CilInstructionLabel(returnStart));
        instructions.Add(finallyStart);
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Ldind_I4);
        instructions.Add(CilOpCodes.Ldc_I4_1);
        instructions.Add(CilOpCodes.Add);
        instructions.Add(CilOpCodes.Stind_I4);
        instructions.Add(CilOpCodes.Endfinally);
        instructions.Add(returnStart);
        instructions.Add(CilOpCodes.Ret);

        il.ExceptionHandlers.Add(new CilExceptionHandler
        {
            HandlerType = CilExceptionHandlerType.Finally,
            TryStart = new CilInstructionLabel(tryStart),
            TryEnd = new CilInstructionLabel(finallyStart),
            HandlerStart = new CilInstructionLabel(finallyStart),
            HandlerEnd = new CilInstructionLabel(returnStart)
        });
        return true;
    }
}
