using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Emits only struct static constructors accepted by the complete native proof.</summary>
internal static class X64StructStaticConstructorRecovery
{
    internal static bool TryGenerate(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (X64StructStaticConstructorProof.Find(method) is not { } evidence)
            return false;

        var il = new CilMethodBody
        {
            InitializeLocals = true,
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };
        definition.CilMethodBody = il;
        il.Instructions.Add(CilOpCodes.Ldsfld, evidence.Witness.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldc_I4_1);
        il.Instructions.Add(CilOpCodes.Add);
        il.Instructions.Add(CilOpCodes.Stsfld, evidence.Witness.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldc_I4, evidence.MarkerValue);
        il.Instructions.Add(CilOpCodes.Stsfld, evidence.Marker.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ldc_R4,
            BitConverter.ToSingle(BitConverter.GetBytes(evidence.BiasBits), 0));
        il.Instructions.Add(CilOpCodes.Stsfld, evidence.Bias.ToFieldDescriptor());
        il.Instructions.Add(CilOpCodes.Ret);
        return true;
    }
}
