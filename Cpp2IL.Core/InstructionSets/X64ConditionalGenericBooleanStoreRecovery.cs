using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64ConditionalGenericBooleanStoreRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method,
        MethodDefinition definition)
    {
        if (X64ConditionalGenericBooleanStoreProof.Find(method) is not
            { } evidence)
            return false;

        var body = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = body;
        var il = body.Instructions;
        var returnReceiver = new CilInstruction(CilOpCodes.Ldarg_0);
        var condition = new ConcreteGenericFieldAnalysisContext(
            evidence.Condition, evidence.Receiver).ToFieldDescriptor();
        var booleanTarget = new ConcreteGenericFieldAnalysisContext(
            evidence.BooleanTarget, evidence.Receiver).ToFieldDescriptor();

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Brfalse, new CilInstructionLabel(returnReceiver));
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, condition);
        il.Add(CilOpCodes.Brfalse, new CilInstructionLabel(returnReceiver));

        if (evidence.IntegerTarget is { } integerTarget &&
            evidence.AggregateValue is { } value)
        {
            var integer = new ConcreteGenericFieldAnalysisContext(
                integerTarget, evidence.Receiver).ToFieldDescriptor();
            il.Add(CilOpCodes.Ldarg_0);
            il.Add(CilOpCodes.Ldarga, definition.Parameters[1]);
            il.Add(CilOpCodes.Ldfld, value.ToFieldDescriptor());
            il.Add(CilOpCodes.Stfld, integer);
        }

        il.Add(CilOpCodes.Ldarg_0);
        il.Add(evidence.IntegerTarget == null
            ? CilOpCodes.Ldarg_1 : CilOpCodes.Ldarg_2);
        il.Add(CilOpCodes.Stfld, booleanTarget);
        il.Add(returnReceiver);
        il.Add(CilOpCodes.Ret);
        return true;
    }
}
