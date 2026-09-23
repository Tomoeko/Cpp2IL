using Cpp2IL.Core.Analysis;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// If changing this, also update <see cref="Instruction"/>
/// </summary>
public enum OpCode
{
    /// <summary>Invalid instruction, op 1 is the debug string</summary>
    Invalid,

    /// <summary>Not implemented instruction, op 1 is the debug string</summary>
    NotImplemented,

    /// <summary>
    /// Interrupt, kept for stack analysis
    /// </summary>
    Interrupt,

    /// <summary>
    /// No operation
    /// </summary>
    Nop,

    /// <summary>Moves op 2 into op 1</summary>
    Move,

    /// <summary>Moves the result of phi function into op 1, other operands are inputs</summary>
    Phi,

    /// <summary>Calls a method @ op 1, moves the result into op 2, and the rest are params</summary>
    Call,

    /// <summary>Calls a method @ op 1, the rest are params</summary>
    CallVoid,

    /// <summary>Calls a method @ op 1, the rest are params</summary>
    IndirectCall,

    /// <summary>Returns from the method, op 1 is the value to return (optional)</summary>
    Return,

    /// <summary>Jumps to op 1</summary>
    Jump,

    /// <summary>Jumps to op 1</summary>
    IndirectJump,

    /// <summary><c>If op 2 is true, jumps to op 1</summary>
    ConditionalJump,

    /// <summary>Adds op 1 to stack pointer</summary>
    ShiftStack,

    /// <summary>Adds op 2 and op 3, and moves the result into op 1</summary>
    Add,

    /// <summary>Subtracts op 3 from op 2, and moves the result into op 1</summary>
    Subtract,

    /// <summary>Multiplies op 2 by op 3, and moves the result into op 1</summary>
    Multiply,

    /// <summary>Divides op 2 by op 3, and moves the result into op 1</summary>
    Divide,

    /// <summary>Divides op 2 by op 3, and moves the remainder into op 1</summary>
    Modulo,

    /// <summary>Shifts the bits of op 2 left by op 3, and moves the result into op 1</summary>
    ShiftLeft,

    /// <summary>Arithmetic right shift with sign fill: op 1 = op 2 shifted by op 3.</summary>
    ShiftRight,

    /// <summary>Bitwise AND on op 2 and op 3, moves the result into op 1</summary>
    And,

    /// <summary>Bitwise OR on op 2 and op 3, moves the result into op 1</summary>
    Or,

    /// <summary>Bitwise XOR on op 2 and op 3, moves the result into op 1</summary>
    Xor,

    /// <summary>Logical not on op 2, moves the result into op 1</summary>
    Not,

    /// <summary>Negates op 2, moves the result into op 1</summary>
    Negate,

    /// <summary>Moves 1 into op 1, if op 2 and op 3 are equal</summary>
    CheckEqual,

    /// <summary>Moves 1 into op 1, if op 2 is greater than op 3</summary>
    CheckGreater,

    /// <summary>Moves 1 into op 1, if op 2 is less than op 3</summary>
    CheckLess,

    /// <summary>Moves 1 into op 1, if op 2 and op 3 are not equal</summary>
    CheckNotEqual,

    /// <summary>Moves 1 into op 1, if op 2 is greater than or equal to op 3</summary>
    CheckGreaterOrEqual,

    /// <summary>Moves 1 into op 1, if op 2 is less than or equal to op 3</summary>
    CheckLessOrEqual,

    /// <summary>
    /// Allocates a new, uninitialized instance of the type described by op 2 and moves it into op 1.
    /// </summary>
    Newobj,

    /// <summary>
    /// Allocates a new array of the type described by op 2, with the length in op 3, into op 1.
    /// </summary>
    NewArr,

    /// <summary>
    /// Boxes the value at op 3 as the type described by op 2, and moves the result into op 1.
    /// </summary>
    Box,

    /// <summary>
    /// Throws a new instance of the exception type described by op 1.
    /// </summary>
    Throw,

    /// <summary>Unsigned integer comparison: op 2 is less than op 3.</summary>
    CheckLessUnsigned,
    /// <summary>Unsigned integer comparison: op 2 is greater than op 3.</summary>
    CheckGreaterUnsigned,
    /// <summary>Unsigned integer comparison: op 2 is less than or equal to op 3.</summary>
    CheckLessOrEqualUnsigned,
    /// <summary>Unsigned integer comparison: op 2 is greater than or equal to op 3.</summary>
    CheckGreaterOrEqualUnsigned,

    /// <summary>
    /// Defines op 1 with a value whose semantics are not recovered; op 2 is a diagnostic string.
    /// May be removed only when the definition is unused. A surviving definition cannot emit IL.
    /// </summary>
    UnresolvedValue,

    /// <summary>Logical right shift: op 1 = op 2 shifted by op 3 with zero fill.</summary>
    ShiftRightUnsigned,

    /// <summary>Unsigned integer quotient: op1 = op2 / op3; requires an established native width.</summary>
    DivideUnsigned,
    /// <summary>Unsigned integer remainder: op1 = op2 % op3; requires an established native width.</summary>
    ModuloUnsigned,

    /// <summary>
    /// Boolean scalar floating comparison: destination, left, right, width (32/64), outcome mask.
    /// Mask bits select less=1, equal=2, greater=4, unordered=8. Width and mask are Immediate operands.
    /// </summary>
    FloatCompare,

    /// <summary>
    /// Truncate the source to its low sourceBits, then extend to resultBits: destination, source,
    /// sourceBits (8/16/32), resultBits (32/64), signedFlag (0=zero fill, 1=sign fill).
    /// Widths and signedFlag are Immediate operands; IntegerBitWidth remains zero.
    /// </summary>
    IntegerExtend,

    /// <summary>
    /// Truncate binary64 to a signed integer, yielding MIN for NaN or out-of-range input:
    /// destination, source, sourceBits (64), resultBits (32/64). IntegerBitWidth remains zero.
    /// Models result bits under masked floating exceptions, not MXCSR status/control effects.
    /// </summary>
    FloatTruncateSigned,
    /// <summary>
    /// Nonreturning target-runtime NullCheck failure; op1 is validated RuntimeNullThrowEvidence.
    /// This is not ordinary managed construction/throw. It must be coalesced into an equivalent
    /// implicit managed receiver check; any surviving instruction is a strict emission failure.
    /// </summary>
    RuntimeNullThrow,
}
