namespace Cpp2IL.Core.ISIL;

public static class OpCodeExtensions
{
    public static bool IsComparison(this OpCode opcode) => opcode is
        OpCode.CheckEqual or OpCode.CheckNotEqual or OpCode.CheckLess or OpCode.CheckGreater or
        OpCode.CheckLessOrEqual or OpCode.CheckGreaterOrEqual or OpCode.CheckLessUnsigned or
        OpCode.CheckGreaterUnsigned or OpCode.CheckLessOrEqualUnsigned or OpCode.CheckGreaterOrEqualUnsigned;

    public static bool IsUnsignedComparison(this OpCode opcode) => opcode is
        OpCode.CheckLessUnsigned or OpCode.CheckGreaterUnsigned or OpCode.CheckLessOrEqualUnsigned or OpCode.CheckGreaterOrEqualUnsigned;
}
