using System;
using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves only the low-byte count conversion used by straight-line Windows x64 shifts.
/// This does not authorize a general MOVZX fallback or infer full native register values.
/// </summary>
internal static class X86ShiftCountExtensionProof
{
    public static HashSet<ulong> Find(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var owner = context.DeclaringType;
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { parameterCount: 2, GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || owner.Attributes != owner.DefaultAttributes ||
            context.GenericParameters.Count != 0 || !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters is not [var value, var count] ||
            definition.InternalParameterData is not [var originalValue, var originalCount] ||
            value.Definition != originalValue || count.Definition != originalCount ||
            !UnchangedParameter(value, context, 0) || !UnchangedParameter(count, context, 1) ||
            !ReferenceEquals(count.ParameterType, context.AppContext.SystemTypes.SystemInt32Type) ||
            ISIL.IntegerExtension.StorageBits(value.ParameterType, context.AppContext.SystemTypes) is not (32 or 64) ||
            !ReferenceEquals(context.ReturnType, value.ParameterType) || !ReferenceEquals(context.ReturnType, context.DefaultReturnType) ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            body.Count == 0 || body[0].IP != context.UnderlyingPointer)
            return [];
        return Find(body);
    }

    // The caller of this overload supplies the canonical incoming Int32 in RDX.
    internal static HashSet<ulong> Find(IReadOnlyList<Instruction> body)
    {
        var proved = new HashSet<ulong>();
        if (body.Count == 0)
            return proved;
        var end = body.Count;
        var nextIp = body[0].IP;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 || instruction.IP != nextIp ||
                instruction.Length == 0 || instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None)
                return [];
            nextIp = instruction.NextIP;
            if (IsPlainReturn(instruction))
            {
                end = index + 1;
                break;
            }
            // No edge may bypass the conversion or re-enter after its source is modified.
            // Calls can consume/clobber ABI registers beyond their explicit Iced operands.
            if (instruction.FlowControl != FlowControl.Next)
                return [];
        }

        var factory = new InstructionInfoFactory();
        var sourceIsParameter = true;
        var masked = false;
        for (var index = 0; index < end; index++)
        {
            var instruction = body[index];
            if (sourceIsParameter && IsCountExtension(instruction) && CountUsesAreBounded(index + 1))
                proved.Add(instruction.IP);

            var writesSource = false;
            foreach (var used in factory.GetInfo(instruction).GetUsedRegisters())
                writesSource |= used.Register.GetFullRegister() == Register.RDX && IsWrite(used.Access);
            if (!writesSource)
                continue;
            // The measured compiler masks the Int32 parameter before reading DL. Retain this
            // actual AND in ISIL; it is provenance for the count, not permission to erase it.
            if (sourceIsParameter && !masked && IsCountMask(instruction))
                masked = true;
            else
                sourceIsParameter = false;
        }
        return proved;

        bool CountUsesAreBounded(int start)
        {
            var usedAsCount = false;
            for (var index = start; index < end; index++)
            {
                var instruction = body[index];
                if (IsPlainReturn(instruction))
                    return usedAsCount;
                if (IsSupportedShift(instruction))
                {
                    usedAsCount = true;
                    continue;
                }

                var overwritten = false;
                foreach (var used in factory.GetInfo(instruction).GetUsedRegisters())
                {
                    if (used.Register.GetFullRegister() != Register.RCX)
                        continue;
                    if (used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                        return false;
                    if (used.Access != OpAccess.Write || used.Register.GetSize() is not (4 or 8))
                        return false;
                    overwritten = true;
                }
                if (overwritten)
                    return usedAsCount;
            }
            return false;
        }
    }

    private static bool IsSupportedShift(Instruction instruction) =>
        instruction.Mnemonic is Mnemonic.Shl or Mnemonic.Sal or Mnemonic.Shr or Mnemonic.Sar &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register.GetSize() is 4 or 8 && instruction.Op0Register.IsGPR() &&
        instruction.Op0Register.GetFullRegister() != Register.RCX &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == Register.CL;

    private static bool IsCountExtension(Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Movzx && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.ECX &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == Register.DL;

    private static bool IsCountMask(Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.And && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.EDX &&
        instruction.Op1Kind is OpKind.Immediate8to32 or OpKind.Immediate32 && instruction.GetImmediate(1) is 31 or 63;

    private static bool IsPlainReturn(Instruction instruction) => instruction.Code == Code.Retnq && instruction.OpCount == 0;

    private static bool IsWrite(OpAccess access) =>
        access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;

    private static bool UnchangedParameter(ParameterAnalysisContext parameter, MethodAnalysisContext context, int index) =>
        parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } && parameter.ParameterIndex == index &&
        ReferenceEquals(parameter.DeclaringMethod, context) && !parameter.IsRef && parameter.Attributes == parameter.DefaultAttributes &&
        ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType);
}
