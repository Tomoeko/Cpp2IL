using System;
using System.Linq;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void BooleanArrayStoreUsesVerifiableByteOpcodeInBothEmissionPaths(
        bool computedValue)
    {
        var arrayType = new SzArrayTypeAnalysisContext(_app.SystemTypes.SystemBooleanType);
        var (context, definition, parameters) = CreateMethod("SetBoolean",
            _app.SystemTypes.SystemVoidType,
            [arrayType, _app.SystemTypes.SystemInt32Type]);
        var target = new ArrayAccess(parameters[0], parameters[1]);
        Emit(context, definition, computedValue
            ? [new(0, OpCode.CheckEqual, target, Imm(7), Imm(7)),
                new(1, OpCode.Return)]
            : [new(0, OpCode.Move, target, Imm(1)),
                new(1, OpCode.Return)]);

        var il = definition.CilMethodBody!.Instructions;
        Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stelem_I1), Is.EqualTo(1));
        Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Stelem), Is.False);

        using var runtime = Load();
        var array = new bool[2];
        runtime.Type.GetMethod("SetBoolean")!.Invoke(null, [array, 1]);
        Assert.That(array, Is.EqualTo(new[] { false, true }));
    }
}
