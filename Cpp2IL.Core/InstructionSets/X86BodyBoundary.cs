using System.Collections.Generic;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>A decoded byte-span boundary does not establish a native return.</summary>
internal static class X86BodyBoundary
{
    public static void AppendFallthroughFailure(List<Instruction> instructions)
    {
        // Add this even after a terminal instruction: CFG reachability keeps a real return,
        // tail transfer or closed loop valid, while a branch into trailing code still fails.
        // The sentinel has no native address and must not become a target for an external jump.
        instructions.Add(new(instructions.Count == 0 ? 0 : instructions[^1].Index + 1, OpCode.Invalid,
            new StringLiteral("Decoded native body has an unproved fallthrough at its boundary")));
    }
}
