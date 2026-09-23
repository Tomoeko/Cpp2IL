using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(OpCode.Add, 8)]
    [TestCase(OpCode.Subtract, 8)]
    [TestCase(OpCode.CheckLess, 8)]
    [TestCase(OpCode.CheckLessUnsigned, 8)]
    [TestCase(OpCode.Add, 16)]
    [TestCase(OpCode.Subtract, 16)]
    [TestCase(OpCode.CheckLess, 16)]
    [TestCase(OpCode.CheckLessUnsigned, 16)]
    public void StandaloneNarrowArithmeticCannotUseTheFieldEqualityEmissionPath(OpCode opcode, int width)
    {
        var resultType = opcode is OpCode.Add or OpCode.Subtract ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemBooleanType;
        var (context, definition, parameters) = CreateMethod("UnsupportedNarrowOperation", resultType,
            [_app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemInt32Type]);
        var result = new LocalVariable("result", new Register(701, "result"), resultType);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition,
        [
            new(0, opcode, result, parameters[0], parameters[1]) { IntegerBitWidth = width },
            new(1, OpCode.Return, result),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("width"));
    }
}
