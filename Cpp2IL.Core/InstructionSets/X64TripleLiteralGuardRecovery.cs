using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64TripleLiteralGuardRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64TripleLiteralGuardProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        var textType = method.AppContext.SystemTypes.SystemStringType
            .ToTypeSignature();
        var firstText = new CilLocalVariable(textType);
        var chosenText = new CilLocalVariable(textType);
        il.LocalVariables.Add(firstText);
        il.LocalVariables.Add(chosenText);

        var falseLiteral = new CilInstruction(CilOpCodes.Ldstr,
            evidence.FalseLiteral);
        var concatenate = new CilInstruction(CilOpCodes.Ldloc, firstText);
        var instructions = il.Instructions;
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, evidence.Getter.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldfld,
            evidence.TextField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Stloc, firstText);
        instructions.Add(CilOpCodes.Ldarg_0);
        instructions.Add(CilOpCodes.Call, evidence.Getter.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Callvirt,
            evidence.NestedGetter.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ldfld,
            evidence.FlagField.ToFieldDescriptor());
        instructions.Add(CilOpCodes.Brfalse,
            new CilInstructionLabel(falseLiteral));
        instructions.Add(CilOpCodes.Ldstr, evidence.TrueLiteral);
        instructions.Add(CilOpCodes.Stloc, chosenText);
        instructions.Add(CilOpCodes.Br, new CilInstructionLabel(concatenate));
        instructions.Add(falseLiteral);
        instructions.Add(CilOpCodes.Stloc, chosenText);
        instructions.Add(concatenate);
        instructions.Add(CilOpCodes.Ldloc, chosenText);
        instructions.Add(CilOpCodes.Ldstr, evidence.Suffix);
        instructions.Add(CilOpCodes.Call, evidence.Concat.ToMethodDescriptor());
        instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
