using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Tests.Isil;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCaseSource(typeof(X86NumericConversionRejectionTests), nameof(X86NumericConversionRejectionTests.Cases))]
    public void ReachableNumericConversionCannotProduceManagedIl(string bytes, Mnemonic expected)
    {
        var (context, definition, _) = CreateMethod("UnprovedNumericConversion", _app.SystemTypes.SystemInt32Type, []);
        var conversion = new X86InstructionSet().GetIsilFromInstruction(X86NumericConversionRejectionTests.Decode(bytes)).Single();
        context.ControlFlowGraph = new([conversion, new(1, OpCode.Return, Imm(0))]);

        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.NotImplemented));
        var error = Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(context, definition));
        Assert.That(error!.Message, Does.Contain(expected.ToString()).IgnoreCase);
        Assert.That(definition.CilMethodBody, Is.Null, "Unproved numeric conversion must fail before allocating a method body.");
    }

    [TestCase("0F5BC1")]
    [TestCase("0F5AC1")]
    [TestCase("F30FE6C1")]
    [TestCase("660F5AC1")]
    [TestCase("F20F2CC1")]
    [TestCase("F2480F2CC1")]
    public void OverwrittenNumericConversionCannotDisappearThroughDeadCodeElimination(string bytes)
    {
        var (context, definition, _) = CreateMethod("OverwrittenNumericConversion", _app.SystemTypes.SystemInt32Type, []);
        var native = X86NumericConversionRejectionTests.Decode(bytes);
        var conversion = new X86InstructionSet().GetIsilFromInstruction(native).Single();
        // Model the two SSA versions of a native destination. If conversion were lowered
        // to Move, its unused old version would be pure and DCE could silently remove it.
        for (var index = 0; index < conversion.Operands.Count; index++)
        {
            if (conversion.Operands[index] is not IsilRegister register)
                continue;
            var local = new LocalVariable("old_" + register.Name, register.Copy(0), _app.SystemTypes.SystemInt32Type);
            context.Locals.Add(local);
            conversion.SetOperand(index, local);
        }
        var destination = new IsilRegister(null, X86Utils.GetRegisterName(native.Op0Register), 1);
        var overwritten = new LocalVariable("overwritten", destination, _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(overwritten);
        context.ControlFlowGraph = new([
            conversion,
            new IsilInstruction(1, OpCode.Move, overwritten, Imm(7)),
            new IsilInstruction(2, OpCode.Return, overwritten),
        ]);
        DeadCodeEliminator.Run(context);

        Assert.That(conversion.OpCode, Is.EqualTo(OpCode.NotImplemented));
        Assert.That(context.ControlFlowGraph.Instructions, Does.Contain(conversion));
        Assert.That(() => IlGenerator.GenerateIl(context, definition), Throws.TypeOf<DecompilerException>());
        Assert.That(definition.CilMethodBody, Is.Null);
    }
}
