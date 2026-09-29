using System.Collections.Generic;
using System.Linq;
using Iced.Intel;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ClassCastLookupProof
{
    // The observed Boolean form first materializes the original reference or
    // null, then tests that reference into AL on both returning paths. The
    // complete predicate retains metadata initialization before the null test.
    internal static Shape? TryProveBooleanParameterShape(
        IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 35 || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !PushRbx(body[0]) || body[0].Length != 2 ||
            !Stack(body[1], Mnemonic.Sub) || body[1].Length != 4 ||
            !RipCompareZero(body[2]) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[4], Code.Jne_rel8_64, body[8].IP) ||
            !RipLea(body[5], NativeRegister.RCX) || !DirectCall(body[6]) ||
            !RipStoreOne(body[7], body[2].IPRelativeMemoryAddress) ||
            !SelfTest(body[8], NativeRegister.RBX) ||
            !Branch(body[9], Code.Jne_rel8_64, body[16].IP) ||
            !ZeroEax(body[10]) || !BooleanReferenceReturn(body, 11) ||
            !RipLoad(body[16], NativeRegister.RDX, body[5].IPRelativeMemoryAddress) ||
            !ObjectClassLoad(body[17], NativeRegister.RAX, NativeRegister.RBX) ||
            !TargetDepthLoad(body[18], NativeRegister.ECX, NativeRegister.RDX) ||
            !ClassDepthCompare(body[19], NativeRegister.RAX, NativeRegister.CL) ||
            !Branch(body[20], Code.Jb_rel8_64, body[26].IP) ||
            !HierarchyLoad(body[21], NativeRegister.RAX) ||
            !HierarchyCompare(body[22], NativeRegister.RDX) ||
            !Branch(body[23], Code.Jne_rel8_64, body[26].IP) ||
            body[24].Code != Code.Mov_r8_imm8 ||
            body[24].Op0Kind != OpKind.Register || body[24].Op0Register != NativeRegister.CL ||
            body[24].Op1Kind != OpKind.Immediate8 || body[24].Immediate8 != 1 ||
            !Branch(body[25], Code.Jmp_rel8_64, body[27].IP) ||
            body[26].Code != Code.Xor_r8_rm8 ||
            body[26].Op0Kind != OpKind.Register || body[26].Op1Kind != OpKind.Register ||
            body[26].Op0Register != NativeRegister.CL ||
            body[26].Op1Register != NativeRegister.CL ||
            !ZeroEax(body[27]) || !TestCl(body[28]) ||
            !CmovOriginal(body[29], NativeRegister.RBX) ||
            !BooleanReferenceReturn(body, 30))
            return null;

        return new Shape(0, body[2].IPRelativeMemoryAddress,
            body[5].IPRelativeMemoryAddress, body[6].NearBranchTarget);
    }

    private static bool BooleanReferenceReturn(IReadOnlyList<NativeInstruction> body,
        int start) =>
        SelfTest(body[start], NativeRegister.RAX) &&
        body[start + 1].Code == Code.Setne_rm8 &&
        body[start + 1].Op0Kind == OpKind.Register &&
        body[start + 1].Op0Register == NativeRegister.AL &&
        ReturnEpilog(body, start + 2);
}
