using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [TestCase(OpCode.Add)]
    [TestCase(OpCode.Subtract)]
    [TestCase(OpCode.CheckLess)]
    [TestCase(OpCode.CheckLessUnsigned)]
    public void StandaloneByteWidthArithmeticCannotUseTheFieldEqualityEmissionPath(OpCode opcode)
    {
        var resultType = opcode is OpCode.Add or OpCode.Subtract ? _app.SystemTypes.SystemInt32Type : _app.SystemTypes.SystemBooleanType;
        var (context, definition, parameters) = CreateMethod("UnsupportedByteOperation", resultType,
            [_app.SystemTypes.SystemInt32Type, _app.SystemTypes.SystemInt32Type]);
        var result = new LocalVariable("result", new Register(701, "result"), resultType);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition,
        [
            new(0, opcode, result, parameters[0], parameters[1]) { IntegerBitWidth = 8 },
            new(1, OpCode.Return, result),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("width"));
    }
}
