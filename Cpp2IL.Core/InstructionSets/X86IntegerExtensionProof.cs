using System;
using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete register-only conversion from the first Windows x64 integer argument.
/// It does not infer upper parameter bits, partial register writes, memory widths or control flow.
/// </summary>
internal static class X86IntegerExtensionProof
{
    public static List<IsilInstruction>? TryLift(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var owner = context.DeclaringType;
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { parameterCount: 1, GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            owner.Attributes != owner.DefaultAttributes || context.GenericParameters.Count != 0 ||
            !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters is not [var parameter] || parameter.ParameterIndex != 0 ||
            parameter.Definition == null || definition.InternalParameterData is not [var originalParameter] || parameter.Definition != originalParameter ||
            !ReferenceEquals(parameter.DeclaringMethod, context) || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            !ReferenceEquals(context.ReturnType, context.DefaultReturnType) ||
            parameter.Definition.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            body.Count == 0 || body[0].IP != context.UnderlyingPointer)
            return null;

        var types = context.AppContext.SystemTypes;
        return TryLift(body, ISIL.IntegerExtension.StorageBits(parameter.ParameterType, types),
            ISIL.IntegerExtension.StorageBits(context.ReturnType, types));
    }

    internal static List<IsilInstruction>? TryLift(IReadOnlyList<Instruction> body, int parameterBits, int returnBits)
    {
        if (body.Count == 0 || parameterBits is not (8 or 16 or 32) || returnBits is not (32 or 64))
            return null;

        var result = new List<IsilInstruction>();
        var argument = new IsilRegister(null, "rcx");
        IsilRegister? accumulator = null;
        var observedExtension = false;
        var directUnsignedWiden = false;
        var nextIp = body[0].IP;
        foreach (var instruction in body)
        {
            if (instruction.IP != nextIp || instruction.Length == 0 || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None)
                return null;
            nextIp = instruction.NextIP;

            if (instruction.Code == Code.Retnq && instruction.OpCount == 0)
            {
                if (accumulator == null || !(observedExtension || directUnsignedWiden && returnBits == 64))
                    return null;
                // The return signature may seed its operand's managed type. Keep that projection
                // separate from the 64-bit RAX value used by preceding native conversions.
                var returned = returnBits == 32 ? Extend(accumulator.Value, 32, 32, false) : accumulator.Value;
                result.Add(new(result.Count, ISIL.OpCode.Return, returned));
                return result;
            }
            if (instruction.FlowControl != FlowControl.Next)
                return null;

            if (instruction.Mnemonic is Mnemonic.Cwde or Mnemonic.Cdqe)
            {
                if (instruction.OpCount != 0 || accumulator == null ||
                    instruction.Code is not (Code.Cwde or Code.Cdqe))
                    return null;
                WriteResult(accumulator.Value, instruction.Mnemonic == Mnemonic.Cwde ? 16 : 32,
                    instruction.Mnemonic == Mnemonic.Cwde ? 32 : 64, true);
                observedExtension = true;
                continue;
            }

            if (instruction.OpCount != 2 || instruction.Op0Kind != OpKind.Register || instruction.Op1Kind != OpKind.Register ||
                instruction.Op0Register is not (Register.EAX or Register.RAX))
                return null;
            var sourceBits = instruction.Op1Register switch
            {
                Register.CL or Register.AL => 8,
                Register.CX or Register.AX => 16,
                Register.ECX or Register.EAX => 32,
                _ => 0,
            };
            IsilRegister source;
            if (instruction.Op1Register is Register.CL or Register.CX or Register.ECX)
            {
                // Windows x64 right-justifies narrow arguments; it does not establish the
                // remaining RCX bits. A signed byte parameter cannot prove an ECX source.
                if (sourceBits > parameterBits)
                    return null;
                source = argument;
            }
            else if (sourceBits != 0 && accumulator.HasValue)
                source = accumulator.Value;
            else
                return null;

            var resultBits = instruction.Op0Register == Register.EAX ? 32 : 64;
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Movsx when sourceBits is 8 or 16:
                case Mnemonic.Movzx when sourceBits is 8 or 16:
                case Mnemonic.Movsxd when sourceBits == 32 && resultBits == 64:
                    WriteResult(source, sourceBits, resultBits, instruction.Mnemonic != Mnemonic.Movzx);
                    observedExtension = true;
                    break;
                case Mnemonic.Mov when instruction.Op0Register == Register.EAX && instruction.Op1Register == Register.ECX:
                    WriteResult(source, 32, 32, false);
                    directUnsignedWiden = true;
                    break;
                default:
                    return null;
            }
        }
        // Never replace a missing native terminator with a synthesized managed return.
        return null;

        IsilRegister Extend(IsilRegister source, int sourceBits, int destinationBits, bool signed)
        {
            var destination = new IsilRegister(null, $"integer_extension_{result.Count}");
            result.Add(new(result.Count, ISIL.OpCode.IntegerExtend, destination, source,
                new ISIL.Immediate(sourceBits), new ISIL.Immediate(destinationBits), new ISIL.Immediate(signed ? 1 : 0)));
            return destination;
        }

        void WriteResult(IsilRegister source, int sourceBits, int destinationBits, bool signed)
        {
            var value = Extend(source, sourceBits, destinationBits, signed);
            // Every EAX write zeros the high RAX half, including a signed 8/16->32 extension.
            accumulator = destinationBits == 32 ? Extend(value, 32, 64, false) : value;
        }
    }
}
