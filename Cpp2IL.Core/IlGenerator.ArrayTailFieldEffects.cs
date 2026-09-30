using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static bool ValidArrayTailFieldEffects(MethodAnalysisContext method, List<Instruction> instructions,
        X64GuardedArrayOperationProof.Evidence evidence)
    {
        var effects = instructions.Where(instruction =>
            instruction.Operands.Any(operand => operand is FieldReference)).ToArray();
        if (effects.Length == 0)
            return true;
        if (X64NativeInvocationValues.Create(evidence.Body, evidence.NoReturnCallAddresses) is not { } values)
            return false;

        foreach (var effect in effects)
        {
            if (effect.NativeAddress is not { } address || effect.CallSemantics != CallSemantics.Direct ||
                evidence.Body.Where(native => native.IP == address).ToArray() is not [var native])
                return false;
            var position = instructions.IndexOf(effect);
            var bits = native.MemorySize.GetSize() * 8;
            if (effect.IntegerBitWidth != 0 && effect.IntegerBitWidth != bits)
                return false;

            if (effect is { OpCode: OpCode.Move, Operands: [LocalVariable result, FieldReference read] })
            {
                if (!(native.Code == Code.Mov_r32_rm32 && bits == 32 ||
                      native.Code == Code.Mov_r64_rm64 && bits == 64) ||
                    native.Op0Kind != OpKind.Register || native.Op1Kind != OpKind.Memory ||
                    native.Op0Register.GetSize() * 8 != bits ||
                    !NullCheckedCall.SameOrdinaryType(result.Type, read.Field.FieldType) ||
                    !BoundArrayTailField(method, instructions, position, native, read, bits, values))
                    return false;
                continue;
            }

            // Preserve an in-place native increment or decrement, including its
            // original field read, owner and wraparound storage width.
            if (native.Code is not (Code.Inc_rm32 or Code.Dec_rm32) ||
                native.Op0Kind != OpKind.Memory || bits != 32 ||
                effect.OpCode != (native.Code == Code.Inc_rm32 ? OpCode.Add : OpCode.Subtract) ||
                effect.Operands is not [FieldReference write, FieldReference source, Immediate { Value: 1 }] ||
                !ReferenceEquals(write.Field, source.Field) ||
                !(ReferenceEquals(write.Field.FieldType, method.AppContext.SystemTypes.SystemInt32Type) ||
                  ReferenceEquals(write.Field.FieldType, method.AppContext.SystemTypes.SystemUInt32Type)) ||
                !BoundArrayTailField(method, instructions, position, native, write, bits, values) ||
                !BoundArrayTailField(method, instructions, position, native, source, bits, values))
                return false;
        }
        return true;
    }

    private static bool BoundArrayTailField(MethodAnalysisContext method, List<Instruction> instructions,
        int position, NativeInstruction native, FieldReference access, int bits, X64NativeInvocationValues values)
    {
        var field = access.Field;
        if (!ReferenceEquals(field.DeclaringType.AppContext, method.AppContext) ||
            field.Name != field.DefaultName || field.Offset < 0 || access.Offset != field.Offset ||
            native.MemoryBase.GetSize() != 8 || native.MemoryIndex != NativeRegister.None ||
            native.MemoryDisplacement64 != (ulong)field.Offset ||
            !(field.FieldType.IsValueType
                ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, bits)
                : bits == 64 && NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access)))
            return false;

        foreach (var entry in new[] { NativeRegister.RCX, NativeRegister.RDX, NativeRegister.R8, NativeRegister.R9 })
            if (X64GuardedArrayOperationProof.EntryType(method, entry) is { } type &&
                NullCheckedCall.HasUnchangedReferenceBase(type, field.DeclaringType) &&
                values.Matches(native.IP, native.MemoryBase, 64, new(entry)) &&
                ReachesArrayEntry(method, instructions, access.Local, position, entry,
                    X64GuardedArrayOperationProof.EntryParameter(method, entry)))
                return true;
        return false;
    }
}
