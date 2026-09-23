using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Emits a managed catch only when every part of the bounded x64 signed-divide
/// shape, its native helper chain, and both funclet exits are established from
/// the current player's bytes and metadata.
/// </summary>
internal static class X64CatchDivideRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64CatchDivideBodyProof.Find(method) is not { } body ||
            X64CatchFuncletClassProof.Find(method) is not { } funclet ||
            !ReferenceEquals(body.ExceptionClass, funclet.CheckedClass) ||
            !X64CatchDivideHelperProof.Check(method, body) ||
            !X64CatchFuncletFlowProof.Check(method, funclet))
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
        var tryStart = new CilInstruction(CilOpCodes.Ldarg_1);
        var divide = new CilInstruction(CilOpCodes.Ldarg_0);
        var catchStart = new CilInstruction(CilOpCodes.Pop);
        var returnStart = new CilInstruction(CilOpCodes.Ldloc, result);

        instructions.Add(tryStart);
        instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel(divide));
        instructions.Add(CilOpCodes.Newobj, body.Constructor.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Throw);
        instructions.Add(divide);
        instructions.Add(CilOpCodes.Ldarg_1);
        instructions.Add(CilOpCodes.Div);
        instructions.Add(CilOpCodes.Stloc, result);
        instructions.Add(CilOpCodes.Leave, new CilInstructionLabel(returnStart));
        instructions.Add(catchStart);
        instructions.Add(CilOpCodes.Ldc_I4, body.CaughtReturn);
        instructions.Add(CilOpCodes.Stloc, result);
        instructions.Add(CilOpCodes.Leave, new CilInstructionLabel(returnStart));
        instructions.Add(returnStart);
        instructions.Add(CilOpCodes.Ret);

        il.ExceptionHandlers.Add(new CilExceptionHandler
        {
            HandlerType = CilExceptionHandlerType.Exception,
            TryStart = new CilInstructionLabel(tryStart),
            TryEnd = new CilInstructionLabel(catchStart),
            HandlerStart = new CilInstructionLabel(catchStart),
            HandlerEnd = new CilInstructionLabel(returnStart),
            ExceptionType = body.ExceptionClass.ToTypeSignature().ToTypeDefOrRef()
        });
        return true;
    }
}
