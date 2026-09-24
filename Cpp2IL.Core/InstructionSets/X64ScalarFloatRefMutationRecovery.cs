using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only the two complete, metadata-bound scalar-float bodies.</summary>
internal static class X64ScalarFloatRefMutationRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64ScalarFloatRefMutationProof.FindLeaf(method) is { } leaf)
        {
            var il = NewBody();
            definition.CilMethodBody = il;
            foreach (var field in leaf.Fields)
            {
                il.Instructions.Add(CilOpCodes.Ldarg_0);
                il.Instructions.Add(CilOpCodes.Ldc_R4, 0f);
                il.Instructions.Add(CilOpCodes.Stfld, field.ToFieldDescriptor());
            }
            il.Instructions.Add(CilOpCodes.Ldarg_1);
            il.Instructions.Add(CilOpCodes.Ret);
            return true;
        }

        if (X64ScalarFloatRefMutationProof.FindCaller(method) is not { } caller)
            return false;

        var body = NewBody();
        definition.CilMethodBody = body;
        var instructions = body.Instructions;
        var fields = caller.Fields;
        var captures = new CilLocalVariable[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            captures[index] = new CilLocalVariable(definition.DeclaringModule!.CorLibTypeFactory.Single);
            body.LocalVariables.Add(captures[index]);
            instructions.Add(CilOpCodes.Ldarg_1);
            instructions.Add(CilOpCodes.Ldfld, fields[index].ToFieldDescriptor());
            instructions.Add(CilOpCodes.Stloc, captures[index]);
        }
        instructions.Add(CilOpCodes.Ldarga, definition.Parameters[1]);
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, caller.Target.ToMethodDescriptor());
        foreach (var capture in captures)
        {
            instructions.Add(CilOpCodes.Ldloc, capture);
            instructions.Add(CilOpCodes.Add);
        }
        instructions.Add(CilOpCodes.Ret);
        return true;
    }

    private static CilMethodBody NewBody() => new()
    {
        InitializeLocals = true,
        ComputeMaxStackOnBuild = true,
        VerifyLabelsOnBuild = true
    };
}
