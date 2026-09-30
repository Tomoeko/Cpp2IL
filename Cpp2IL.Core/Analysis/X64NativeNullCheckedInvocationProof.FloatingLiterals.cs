using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private static int FloatingWidth(MethodAnalysisContext caller, TypeAnalysisContext type) =>
        ReferenceEquals(type, caller.AppContext.SystemTypes.SystemSingleType) ? 32 :
        ReferenceEquals(type, caller.AppContext.SystemTypes.SystemDoubleType) ? 64 : 0;

    private static bool FloatingStoreLiteralTypesRetained(MethodAnalysisContext caller) =>
        !caller.ControlFlowGraph!.Instructions.Any(operation =>
            operation is { OpCode: OpCode.Move, Operands: [FieldReference access, Immediate] } &&
            access.Field.FieldType.Type is Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8);

    // FloatLiteralRecovery interprets the stored integer bits using the typed
    // field. Keep those exact bits, including signed zero and NaN payloads, while
    // rebinding the field, owner and native write width on every validation.
    private static bool TryFloatingStoreLiteralKey(MethodAnalysisContext caller, Instruction operation,
        FieldReference access, IOperand literal, int width, out ValueKey key)
    {
        key = null!;
        if (operation is not { IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                NativeAddress: { } address } ||
            FloatingWidth(caller, access.Field.FieldType) != width ||
            !AccessibleField(caller, access.Field) ||
            !NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, width) ||
            !TryOrigin(caller, access.Local, operation, out var owner) || owner.Definition != null || owner.Entry != -1 ||
            !ReferenceEquals(owner.Type, caller.DeclaringType) ||
            !TryFloatingLiteralBits(literal, width, out var bits) ||
            ReadBody(caller, out var body, out _) is not { } values)
            return false;
        var native = body.SingleOrDefault(instruction => instruction.IP == address);
        if (native.Op0Kind != OpKind.Memory || native.MemoryIndex != NativeRegister.None ||
            native.MemoryDisplacement64 != (ulong)access.Offset || native.MemorySize.GetSize() * 8 != width ||
            !values.Matches(address, native.MemoryBase, 64, new(NativeRegister.RCX)))
            return false;
        var matches = width == 32 && native.Code == Code.Mov_rm32_imm32 && native.Immediate32 == (uint)bits ||
                      width == 64 && native.Code == Code.Mov_rm64_imm32 && unchecked((ulong)native.Immediate32to64) == bits ||
                      native.Op1Kind == OpKind.Register &&
                      (width == 32 && native.Code == Code.Mov_rm32_r32 || width == 64 && native.Code == Code.Mov_rm64_r64) &&
                      values.Matches(address, native.Op1Register, width, new(NativeRegister.None, Literal: bits));
        if (!matches) return false;
        key = new("floating-literal", bits, [new("width", width, [])]);
        return true;
    }

    internal static bool TryFloatingLiteralBits(IOperand operand, int width, out ulong bits)
    {
        bits = 0;
        switch (operand)
        {
            case Immediate integer when width == 32 && integer.Value is >= int.MinValue and <= uint.MaxValue:
                bits = unchecked((uint)integer.Value);
                return true;
            case FloatLiteral single when width == 32:
                bits = BitConverter.ToUInt32(BitConverter.GetBytes(single.Value), 0);
                return true;
            case Immediate integer when width == 64:
                bits = integer.UnsignedValue;
                return true;
            case DoubleLiteral wide when width == 64:
                bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(wide.Value));
                return true;
            default:
                return false;
        }
    }
}
