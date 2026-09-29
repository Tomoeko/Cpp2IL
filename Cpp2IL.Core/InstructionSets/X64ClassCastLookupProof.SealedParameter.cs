using System.Collections.Generic;
using System.Linq;
using Iced.Intel;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ClassCastLookupProof
{
    // IsInstSealed preserves the incoming reference only for exact class
    // identity. The Boolean form tests that reference into AL; the reference
    // form returns it directly. Metadata initialization precedes both null paths.
    internal static Shape? TryProveSealedParameterShape(
        IReadOnlyList<NativeInstruction> body, bool booleanResult)
    {
        var result = booleanResult ? NativeRegister.RCX : NativeRegister.RAX;
        var target = booleanResult ? NativeRegister.RAX : NativeRegister.RCX;
        var result32 = booleanResult ? NativeRegister.ECX : NativeRegister.EAX;
        if (body.Count != (booleanResult ? 19 : 17) || body[0].IP == 0 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !PushRbx(body[0]) || body[0].Length != 2 ||
            !Stack(body[1], Mnemonic.Sub) || body[1].Length != 4 ||
            !RipCompareZero(body[2]) || !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[4], Code.Jne_rel8_64, body[8].IP) ||
            !RipLea(body[5], NativeRegister.RCX) || !DirectCall(body[6]) ||
            !RipStoreOne(body[7], body[2].IPRelativeMemoryAddress) ||
            body[8].Code != Code.Xor_r32_rm32 || body[8].OpCount != 2 ||
            body[8].Op0Kind != OpKind.Register || body[8].Op1Kind != OpKind.Register ||
            body[8].Op0Register != result32 || body[8].Op1Register != result32 ||
            !SelfTest(body[9], NativeRegister.RBX) ||
            !Branch(body[10], Code.Je_rel8_64, body[14].IP) ||
            !RipLoad(body[11], target, body[5].IPRelativeMemoryAddress) ||
            body[12].Code != Code.Cmp_rm64_r64 || body[12].OpCount != 2 ||
            body[12].Op0Kind != OpKind.Memory || body[12].MemoryBase != NativeRegister.RBX ||
            body[12].MemoryIndex != NativeRegister.None || body[12].MemoryDisplacement64 != 0 ||
            body[12].MemorySize.GetSize() != 8 || body[12].Op1Kind != OpKind.Register ||
            body[12].Op1Register != target ||
            body[13].Code != Code.Cmove_r64_rm64 || body[13].OpCount != 2 ||
            body[13].Op0Kind != OpKind.Register || body[13].Op1Kind != OpKind.Register ||
            body[13].Op0Register != result || body[13].Op1Register != NativeRegister.RBX ||
            booleanResult && (!SelfTest(body[14], result) ||
                body[15].Code != Code.Setne_rm8 || body[15].OpCount != 1 ||
                body[15].Op0Kind != OpKind.Register || body[15].Op0Register != NativeRegister.AL) ||
            !ReturnEpilog(body, booleanResult ? 16 : 14))
            return null;

        return new Shape(0, body[2].IPRelativeMemoryAddress,
            body[5].IPRelativeMemoryAddress, body[6].NearBranchTarget);
    }
}
