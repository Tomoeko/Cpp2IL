using System;
using System.Collections.Generic;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves that an incoming receiver value is dead before its register is reused.</summary>
internal static class X86UnusedReceiverProof
{
    public static bool IsUnused(MethodAnalysisContext method, ISIL.Register receiver)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            method.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            method.RawBytes.Length == 0 ||
            !Enum.TryParse<Register>(receiver.Name, true, out var nativeRegister) ||
            !nativeRegister.IsGPR64())
            return false;

        return IsUnused(X86Utils.Disassemble(method.RawBytes.AsSpan(), method.UnderlyingPointer, false), nativeRegister);
    }

    internal static bool IsUnused(IEnumerable<Instruction> body, Register receiver)
    {
        if (!receiver.IsGPR64())
            return false;

        var factory = new InstructionInfoFactory();
        foreach (var instruction in body)
        {
            // Calls can consume ABI arguments not listed as native instruction operands.
            // Branches need a whole-CFG proof; this bounded entry-prefix proof does not guess.
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.FlowControl is not (FlowControl.Next or FlowControl.Return))
                return false;

            var overwritten = false;
            foreach (var used in factory.GetInfo(instruction).GetUsedRegisters())
            {
                if (used.Register.GetFullRegister() != receiver)
                    continue;
                if (used.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                    return false;
                // A dword write clears the upper32 bits on x64. Byte/word writes preserve
                // bits of the incoming receiver and cannot prove that value is dead.
                overwritten |= used.Access == OpAccess.Write && used.Register.GetSize() is 4 or 8;
            }

            if (overwritten || instruction.Mnemonic == Mnemonic.Ret)
                return true;
            if (instruction.FlowControl != FlowControl.Next)
                return false;
        }

        return false;
    }
}
