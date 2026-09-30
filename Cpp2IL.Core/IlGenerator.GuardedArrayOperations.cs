using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateGuardedArrayOperations(MethodAnalysisContext method)
    {
        if (method.GetExtraData<X64GuardedArrayOperationProof.Evidence>(
                X64GuardedArrayOperationProof.EvidenceKey) is not { } evidence)
            return;
        var current = X64GuardedArrayOperationProof.Find(method, evidence.Body);
        if (current == null || !current.Body.SequenceEqual(evidence.Body) ||
            !current.Sites.SequenceEqual(evidence.Sites) ||
            !current.RemovedAddresses.SetEquals(evidence.RemovedAddresses) ||
            !current.EffectAddresses.SequenceEqual(evidence.EffectAddresses) ||
            LinearInstructions(method.ControlFlowGraph!) is not { } instructions)
            throw ArrayOperationFailure("the native evidence or successful linear path changed");

        var operations = new HashSet<Instruction>();
        if (instructions.Any(instruction => !ArrayCompositionOpcode(instruction.OpCode)))
            throw ArrayOperationFailure("the final graph introduced an unproved operation");
        foreach (var site in evidence.Sites)
        {
            var operation = UniqueAt(instructions, site.OperationIp, instruction =>
                instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0 or 32,
                    CallSemantics: CallSemantics.Direct } &&
                instruction.Operands.Count == 2 &&
                instruction.Operands[site.IsStore ? 0 : 1] is ArrayAccess);
            if (operation == null || !operations.Add(operation) ||
                operation.Operands[site.IsStore ? 0 : 1] is not ArrayAccess access ||
                access.Array.Type is not SzArrayTypeAnalysisContext array ||
                !ReferenceEquals(array.ElementType, site.ElementType) ||
                !ValidArrayOrigin(method, instructions, access.Array,
                    instructions.IndexOf(operation), site.ArrayOrigin) ||
                access.Index is not LocalVariable index ||
                !ReferenceEquals(index.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
                !ValidArrayIndex(method, instructions, index, instructions.IndexOf(operation), site.IndexOrigin))
                throw ArrayOperationFailure("an access lost its exact typed array, index, or native origin");
            var value = operation.Operands[site.IsStore ? 1 : 0];
            if (value is LocalVariable local
                    ? !ReferenceEquals(local.Type, site.ElementType)
                    : !site.IsStore || site.Width != 4 || value is not Immediate)
                throw ArrayOperationFailure("an element value lost its proved native storage width");
            if (site.IsStore && !ValidArrayStoreValue(method, instructions, value,
                    instructions.IndexOf(operation), site, operations))
                throw ArrayOperationFailure("an element store lost its original value");
        }
        if (instructions.Any(instruction => instruction.Operands.Any(operand => operand is ArrayAccess) &&
                !operations.Contains(instruction)))
            throw ArrayOperationFailure("the final graph contains an unbound array operation");

        // Include reads, calls, division failures and stores. An unused array
        // read still throws, and a field reload after a call is a distinct value.
        // Neither may be discarded or moved merely because its value is copied.
        var effects = instructions.Where(IsArrayCompositionEffect).ToArray();
        if (effects.Any(instruction => instruction.NativeAddress == null) ||
            !effects.Select(instruction => instruction.NativeAddress!.Value)
                .SequenceEqual(evidence.EffectAddresses))
            throw ArrayOperationFailure("a native memory effect, call, or exception changed order or disappeared");
        foreach (var effect in effects.Where(instruction => instruction.IsCall))
        {
            var native = evidence.Body.Single(instruction => instruction.IP == effect.NativeAddress);
            var target = effect.Operands.OfType<MethodAnalysisContext>().SingleOrDefault();
            if (native.Code != Code.Call_rel32_64 || target == null ||
                target.UnderlyingPointer != native.NearBranchTarget ||
                !ReferenceEquals(target.AppContext, method.AppContext) ||
                !X64GuardedArrayOperationProof.HasEligibleEffectCall(target))
                throw ArrayOperationFailure("an intervening call lost its unique original native target");
        }
        if (instructions.Any(instruction => instruction.Destination is FieldReference field &&
                (field.Field.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0))
            throw ArrayOperationFailure("a field store has readonly metadata outside a constructor");
    }

    private static bool IsArrayCompositionEffect(Instruction instruction) =>
        instruction.IsCall || instruction.OpCode is OpCode.Divide or OpCode.DivideUnsigned or
            OpCode.Modulo or OpCode.ModuloUnsigned ||
        instruction.Operands.Any(operand => operand is FieldReference or ArrayAccess or ArrayLength);

    private static bool ArrayCompositionOpcode(OpCode opcode) => opcode is
        OpCode.Nop or OpCode.Move or OpCode.Call or OpCode.CallVoid or OpCode.Return or OpCode.Jump or
        OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.DivideUnsigned or
        OpCode.Modulo or OpCode.ModuloUnsigned or OpCode.ShiftLeft or OpCode.ShiftRight or
        OpCode.ShiftRightUnsigned or OpCode.And or OpCode.Or or OpCode.Xor or OpCode.Not or OpCode.Negate or
        OpCode.CheckEqual or OpCode.CheckNotEqual or OpCode.CheckGreater or OpCode.CheckLess or
        OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual or OpCode.CheckLessUnsigned or
        OpCode.CheckGreaterUnsigned or OpCode.CheckLessOrEqualUnsigned or OpCode.CheckGreaterOrEqualUnsigned or
        OpCode.IntegerExtend;

    private static bool ValidArrayStoreValue(MethodAnalysisContext method, List<Instruction> instructions,
        IOperand value, int before, X64GuardedArrayOperationProof.Site site, HashSet<Instruction> operations)
    {
        if (site.StoredValue is not { } stored)
            return false;
        if (stored.Literal is { } literal)
            return stored.Origin == null && value is Immediate immediate &&
                   (ReferenceEquals(site.ElementType, method.AppContext.SystemTypes.SystemUInt32Type)
                       ? immediate.Value >= int.MinValue && immediate.Value <= uint.MaxValue &&
                         unchecked((uint)immediate.Value) == unchecked((uint)literal)
                       : immediate.Value == literal);
        if (stored.Origin is not { } origin || value is not LocalVariable local)
            return false;
        if (origin.DefinitionIp is not { } ip)
            return X64GuardedArrayOperationProof.EntryParameter(method, origin.EntryRegister) is { } parameter &&
                   ReferenceEquals(parameter.ParameterType, site.ElementType) &&
                   ReachesArrayEntry(method, instructions, local, before, origin.EntryRegister, parameter);
        var definition = UniqueAt(instructions, ip, instruction => instruction.Destination is LocalVariable);
        return definition != null && ReachesDefinition(instructions, local, before, definition) &&
               (operations.Contains(definition) && definition.Operands is [LocalVariable, ArrayAccess] ||
                definition is { OpCode: OpCode.Move, IntegerBitWidth: 0 or 32,
                    Operands: [LocalVariable, FieldReference field] } &&
                ReferenceEquals(field.Field.FieldType, site.ElementType) &&
                NarrowFieldEqualityProof.HasUnchangedFieldLayout(field, 32) ||
                definition.IsCall && definition.Operands.OfType<MethodAnalysisContext>().SingleOrDefault() is { } target &&
                ReferenceEquals(target.ReturnType, site.ElementType));
    }

    private static bool ValidArrayOrigin(MethodAnalysisContext method,
        List<Instruction> instructions, LocalVariable array, int before,
        X64GuardedArrayOperationProof.Origin origin)
    {
        if (origin.FieldReadIp is not { } fieldIp)
            return origin.Field == null && origin.Parameter is { } parameter &&
                   ReachesArrayEntry(method, instructions, array, before, origin.EntryRegister, parameter);
        var read = UniqueAt(instructions, fieldIp, instruction => instruction is
        {
            OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [LocalVariable, FieldReference]
        });
        return origin.Parameter == null && origin.Field is { } field && read != null &&
               read.Operands is [LocalVariable loaded, FieldReference access] &&
               ReferenceEquals(access.Field, field) && access.Offset == field.Offset &&
               ReferenceEquals(access.Local.Type, field.DeclaringType) &&
               NullCheckedCall.SameOrdinaryType(loaded.Type, field.FieldType) &&
               NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) &&
               ReachesDefinition(instructions, array, before, read) &&
               ReachesArrayEntry(method, instructions, access.Local, instructions.IndexOf(read),
                   origin.EntryRegister, null);
    }

    private static bool ReachesArrayEntry(MethodAnalysisContext method, List<Instruction> instructions,
        LocalVariable value, int before, Iced.Intel.Register entryRegister,
        ParameterAnalysisContext? expectedParameter)
    {
        var parameter = X64GuardedArrayOperationProof.EntryParameter(method, entryRegister);
        if (expectedParameter != null && !ReferenceEquals(parameter, expectedParameter))
            return false;
        var seen = new HashSet<LocalVariable>();
        while (seen.Add(value))
        {
            if (method.ParameterLocals.Contains(value))
                return value.IsThis
                    ? !method.IsStatic && entryRegister == Iced.Intel.Register.RCX &&
                      ReferenceEquals(value.Type, method.DeclaringType)
                    : LocalVariables.GetIncomingParameterIndex(method, value) is { } index &&
                      ReferenceEquals(method.Parameters[index], parameter) &&
                      NullCheckedCall.SameOrdinaryType(value.Type, parameter!.ParameterType);
            var definition = instructions.Take(before).LastOrDefault(instruction =>
                ReferenceEquals(instruction.Destination, value));
            if (definition is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, Operands: [LocalVariable, LocalVariable source] })
                return false;
            before = instructions.IndexOf(definition);
            value = source;
        }
        return false;
    }

    private static bool ValidArrayIndex(MethodAnalysisContext method, List<Instruction> instructions,
        LocalVariable index, int before, X64GuardedArrayOperationProof.ValueOrigin origin)
    {
        if (origin.DefinitionIp is not { } ip)
            return X64GuardedArrayOperationProof.EntryParameter(method, origin.EntryRegister) is { } parameter &&
                   ReferenceEquals(parameter.ParameterType, method.AppContext.SystemTypes.SystemInt32Type) &&
                   ReachesArrayEntry(method, instructions, index, before, origin.EntryRegister, parameter);
        var definition = UniqueAt(instructions, ip, instruction => instruction.Destination is LocalVariable);
        if (definition?.Destination is not LocalVariable value ||
            !ReferenceEquals(value.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
            !ReachesDefinition(instructions, index, before, definition))
            return false;
        if (definition.IsCall)
            return definition.Operands.OfType<MethodAnalysisContext>().SingleOrDefault() is { } target &&
                   ReferenceEquals(target.ReturnType, method.AppContext.SystemTypes.SystemInt32Type);
        return definition is { OpCode: OpCode.Move, IntegerBitWidth: 0 or 32,
                   Operands: [LocalVariable, FieldReference field] } &&
               ReferenceEquals(field.Field.FieldType, method.AppContext.SystemTypes.SystemInt32Type) &&
               NarrowFieldEqualityProof.HasUnchangedFieldLayout(field, 32);
    }

    private static DecompilerException ArrayOperationFailure(string reason) =>
        new("Proved native array guard composition lost managed semantics: " + reason);
}
