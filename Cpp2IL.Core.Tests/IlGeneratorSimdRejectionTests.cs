using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Tests.Isil;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase("660F6EC1")]
    [TestCase("66480F7EC0")]
    [TestCase("0F28C1")]
    [TestCase("0F10C1")]
    [TestCase("660F6FC1")]
    [TestCase("F30F6FC1")]
    [TestCase("0FC6C11B")]
    [TestCase("0F14C1")]
    [TestCase("0F54C1")]
    [TestCase("0F56C1")]
    [TestCase("0F57C1")]
    [TestCase("0F57C0")]
    public void OverwrittenUnprovedSimdCannotBeDeletedOrEmitted(string bytes)
    {
        var (context, definition, _) = CreateMethod("DiscardedSimd", _app.SystemTypes.SystemInt32Type, []);
        var native = X86SimdRejectionTests.Decode(bytes);
        var lifted = new X86InstructionSet().GetIsilFromInstruction(native);
        // Model independent SSA values for a formerly fabricated scalar move/operation.
        // The native result is overwritten, so pure aliases could previously vanish in DCE.
        var current = new Dictionary<string, LocalVariable>();
        var version = 0;
        LocalVariable Read(Register register)
        {
            if (current.TryGetValue(register.Name, out var local))
                return local;
            local = new LocalVariable(register.Name + "_input", register.Copy(0), _app.SystemTypes.SystemInt32Type);
            current.Add(register.Name, local);
            context.Locals.Add(local);
            return local;
        }
        foreach (var instruction in lifted)
        {
            var destination = instruction.Destination as Register?;
            for (var index = 1; index < instruction.Operands.Count; index++)
                if (instruction.Operands[index] is Register source)
                    instruction.SetOperand(index, Read(source));
            if (destination.HasValue)
            {
                var register = destination.Value;
                var local = new LocalVariable(register.Name + "_" + ++version, register.Copy(version), _app.SystemTypes.SystemInt32Type);
                context.Locals.Add(local);
                current[register.Name] = local;
                instruction.SetOperand(0, local);
            }
        }
        var final = new LocalVariable("overwritten", new Register(null, X86Utils.GetRegisterName(native.Op0Register), ++version), _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(final);
        lifted.Add(new(lifted.Count, OpCode.Move, final, Imm(7)));
        lifted.Add(new(lifted.Count, OpCode.Return, final));
        context.ControlFlowGraph = new(lifted);
        DeadCodeEliminator.Run(context);

        Assert.That(context.ControlFlowGraph.Instructions.Count(i => i.OpCode == OpCode.NotImplemented), Is.EqualTo(1));
        var error = Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain(native.Mnemonic.ToString()).IgnoreCase);
        Assert.That(definition.CilMethodBody, Is.Null);
    }
}
