using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    /// <summary>
    /// The native proof permits replacing each explicit null/bounds exit with the
    /// corresponding managed field/element access. Later passes must retain those
    /// accesses, their original values, and every intervening managed effect.
    /// This initial path accepts only a linear final graph; a join needs a separate
    /// reaching-definition and effect-order proof.
    /// </summary>
    internal static void ValidateGuardedArrayAccesses(MethodAnalysisContext method)
    {
        if (method.GuardedArrayAccessEvidence is not { } evidence)
            return;

        var instructions = LinearInstructions(method.ControlFlowGraph!);
        if (instructions == null || evidence.Sites.Count == 0)
            throw Failure("the final control flow is not a proved linear path");

        var previousElement = -1;
        var previousFieldRead = (Instruction?)null;
        var previousFieldAccess = (FieldReference?)null;
        var seenFields = new HashSet<Instruction>();
        var seenElements = new HashSet<Instruction>();
        var elementReads = new List<Instruction>();
        var native = X86Utils.Iterate(method).ToArray();
        foreach (var (site, siteNumber) in evidence.Sites.Select((site, index) => (site, index)))
        {
            var fieldRead = UniqueAt(instructions, site.ArrayFieldReadIp,
                instruction => instruction is
                {
                    OpCode: OpCode.Move,
                    IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable, FieldReference]
                });
            var elementRead = UniqueAt(instructions, site.ElementReadIp,
                instruction => instruction is
                {
                    OpCode: OpCode.Move,
                    IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable, ArrayAccess]
                });
            if (fieldRead == null || elementRead == null ||
                !seenFields.Add(fieldRead) || !seenElements.Add(elementRead))
                throw Failure("a proved field or element read was removed or duplicated");

            var fieldPosition = instructions.IndexOf(fieldRead);
            var elementPosition = instructions.IndexOf(elementRead);
            if (fieldPosition <= previousElement || elementPosition <= fieldPosition ||
                fieldRead.Operands is not [LocalVariable arrayValue, FieldReference fieldAccess] ||
                elementRead.Operands is not [LocalVariable elementValue, ArrayAccess elementAccess] ||
                !ReferenceEquals(fieldAccess.Field, site.ArrayField) ||
                fieldAccess.Offset != site.ArrayField.Offset ||
                site.ArrayField.IsStatic ||
                !ReferenceEquals(fieldAccess.Local.Type, site.ArrayField.DeclaringType) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(fieldAccess) ||
                site.ArrayField.FieldType is not SzArrayTypeAnalysisContext arrayType ||
                !NullCheckedCall.SameOrdinaryType(arrayValue.Type, arrayType) ||
                !NullCheckedCall.SameOrdinaryType(elementAccess.Array.Type, arrayType) ||
                !NullCheckedCall.SameOrdinaryType(elementValue.Type, arrayType.ElementType) ||
                !ReachesDefinition(instructions, elementAccess.Array, elementPosition, fieldRead) ||
                elementAccess.Index is not LocalVariable index ||
                !ReferenceEquals(index.Type, method.AppContext.SystemTypes.SystemInt32Type) ||
                !IndexComesFromParameter(method, instructions, site, index, elementPosition))
                throw Failure("a proved array access lost its typed receiver, index, or native order");

            if (site.OwnerFieldReadIp is { } ownerIp)
            {
                var ownerRead = UniqueAt(instructions, ownerIp,
                    instruction => instruction is
                    {
                        OpCode: OpCode.Move,
                        IntegerBitWidth: 0,
                        CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable, FieldReference]
                    });
                if (site.OwnerField is not { } ownerField ||
                    ownerRead == null || ownerRead.Operands is not
                    [LocalVariable ownerValue, FieldReference ownerAccess] ||
                    !ReferenceEquals(ownerAccess.Field, ownerField) ||
                    ownerAccess.Offset != ownerField.Offset ||
                    ownerField.IsStatic ||
                    !ReferenceEquals(ownerValue.Type, ownerField.FieldType) ||
                    !ReferenceEquals(ownerAccess.Local.Type, ownerField.DeclaringType) ||
                    !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(ownerAccess) ||
                    !ReachesDefinition(instructions, fieldAccess.Local, fieldPosition, ownerRead) ||
                    instructions.IndexOf(ownerRead) >= fieldPosition)
                    throw Failure("the array field lost its proved owner-field read");
            }
            else if (site.OwnerField != null || !fieldAccess.Local.IsThis ||
                     !ReferenceEquals(fieldAccess.Local.Type, method.DeclaringType))
            {
                throw Failure("the array field receiver no longer matches its native owner");
            }

            if (siteNumber != 0)
            {
                foreach (var effectEvidence in site.EffectsSincePreviousAccess)
                {
                    var effect = UniqueAt(instructions, effectEvidence.Ip,
                        instruction => MatchesEffect(method, native, instructions,
                            evidence.Sites[siteNumber - 1],
                            previousFieldRead!, previousFieldAccess!, effectEvidence, instruction));
                    var effectPosition = effect == null ? -1 : instructions.IndexOf(effect);
                    if (effectPosition <= previousElement || effectPosition >= fieldPosition)
                        throw Failure("an intervening native call or store lost its managed effect or order");
                    previousElement = effectPosition;
                }
            }

            previousElement = elementPosition;
            previousFieldRead = fieldRead;
            previousFieldAccess = fieldAccess;
            elementReads.Add(elementRead);
        }

        ValidateReferenceComparison(method, evidence, instructions, elementReads);
    }

    private static List<Instruction>? LinearInstructions(ISILControlFlowGraph graph)
    {
        var result = new List<Instruction>();
        var seen = new HashSet<Block>();
        var current = graph.EntryBlock;
        while (seen.Add(current))
        {
            if (ReferenceEquals(current, graph.ExitBlock))
                return result;
            if (!ReferenceEquals(current, graph.EntryBlock))
            {
                if (current.Instructions.Any(instruction => instruction.OpCode is
                    OpCode.ConditionalJump or OpCode.IndirectJump or OpCode.Throw or OpCode.RuntimeNullThrow))
                    return null;
                result.AddRange(current.Instructions);
            }
            if (current.Successors is not [var next] ||
                !ReferenceEquals(next, graph.ExitBlock) &&
                (next.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, current)))
                return null;
            current = next;
        }
        return null;
    }

    private static Instruction? UniqueAt(List<Instruction> instructions, ulong nativeIp,
        System.Func<Instruction, bool> predicate)
    {
        var matches = instructions.Where(instruction => instruction.NativeAddress == nativeIp &&
            predicate(instruction)).Take(2).ToArray();
        return matches is [{ } single] ? single : null;
    }

    private static bool ReachesDefinition(List<Instruction> instructions,
        LocalVariable value, int before, Instruction expected)
    {
        var visited = new HashSet<LocalVariable>();
        while (visited.Add(value))
        {
            var definition = instructions.Take(before).LastOrDefault(instruction =>
                ReferenceEquals(instruction.Destination, value));
            if (definition == null)
                return false;
            if (ReferenceEquals(definition, expected))
                return true;
            if (definition is not
                {
                    OpCode: OpCode.Move,
                    IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable, LocalVariable source]
                })
                return false;
            before = instructions.IndexOf(definition);
            value = source;
        }
        return false;
    }

    private static bool IndexComesFromParameter(MethodAnalysisContext method,
        List<Instruction> instructions, X64ArrayGuardSiteProof.Site site,
        LocalVariable index, int before)
    {
        var nativeName = X86Utils.GetRegisterName(site.IndexArgument);
        var operands = method.ParameterOperands
            .Select((operand, position) => (operand, position))
            .Where(pair => pair.operand is Register register && register.Name == nativeName)
            .ToArray();
        if (operands is not [{ position: var slot, operand: Register argument }] ||
            slot <= 0 || slot - 1 >= method.Parameters.Count ||
            !ReferenceEquals(method.Parameters[slot - 1].ParameterType,
                method.AppContext.SystemTypes.SystemInt32Type))
            return false;
        var parameters = method.ParameterLocals.Where(local =>
            local.Register.Number == argument.Number && local.Register.Version == -1).ToArray();
        if (parameters is not [{ } parameter])
            return false;

        var visited = new HashSet<LocalVariable>();
        while (visited.Add(index))
        {
            if (ReferenceEquals(index, parameter))
                return true;
            var definition = instructions.Take(before).LastOrDefault(instruction =>
                ReferenceEquals(instruction.Destination, index));
            if (definition is not
                {
                    OpCode: OpCode.Move,
                    IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable, LocalVariable source]
                } || definition.NativeAddress is { } ip && ip != site.IndexExtensionIp)
                return false;
            before = instructions.IndexOf(definition);
            index = source;
        }
        return false;
    }

    private static bool MatchesEffect(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native, List<Instruction> instructions,
        X64ArrayGuardSiteProof.Site previousSite,
        Instruction previousFieldRead, FieldReference previousFieldAccess,
        X64ArrayGuardSiteProof.Effect effect, Instruction instruction)
    {
        var nativeEffect = native.SingleOrDefault(candidate => candidate.IP == effect.Ip);
        if (nativeEffect.IsInvalid || nativeEffect.Code != effect.NativeCode)
            return false;
        if (effect.Kind == X64ArrayGuardSiteProof.EffectKind.DirectCall)
            return effect.NativeCode == Code.Call_rel32_64 &&
                   nativeEffect.NearBranchTarget == effect.DirectCallTarget &&
                   instruction is { OpCode: OpCode.CallVoid, CallSemantics: CallSemantics.Direct } &&
                   instruction.Operands is [MethodAnalysisContext target, ..] &&
                   ReferenceEquals(target.AppContext, method.AppContext) &&
                   target.IsVoid &&
                   RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) &&
                   target.Name == target.DefaultName &&
                   target.Attributes == target.DefaultAttributes &&
                   target.ImplAttributes == target.DefaultImplAttributes &&
                   target.OverrideReturnType == null &&
                   ReferenceEquals(target.DefaultReturnType,
                       method.AppContext.SystemTypes.SystemVoidType) &&
                   target.Parameters.All(parameter =>
                       parameter.OverrideParameterType == null &&
                       ReferenceEquals(parameter.ParameterType,
                           parameter.DefaultParameterType)) &&
                   target.UnderlyingPointer == effect.DirectCallTarget &&
                   method.AppContext.MethodsByAddress.TryGetValue(
                       effect.DirectCallTarget, out var bindings) &&
                   bindings is [{ } uniqueTarget] &&
                   ReferenceEquals(uniqueTarget, target) &&
                   MatchesDirectCallArguments(method, native, instructions,
                       previousSite, previousFieldAccess, effect, instruction, target);

        if (effect.Kind != X64ArrayGuardSiteProof.EffectKind.MemoryStore ||
            effect.NativeCode != Code.Inc_rm32 || effect.StoreWidth != 4 ||
            effect.StoreIndex != Iced.Intel.Register.None || effect.StoreScale != 1 ||
            nativeEffect.MemoryBase.GetFullRegister() != effect.StoreBase ||
            nativeEffect.MemoryDisplacement64 != effect.StoreDisplacement ||
            native.FirstOrDefault(candidate => candidate.IP == previousSite.ArrayFieldReadIp) is not
                { Code: Code.Mov_r64_rm64 } previousNativeRead ||
            previousNativeRead.MemoryBase.GetFullRegister() != effect.StoreBase ||
            NativeWritesRegisterBetween(native, previousNativeRead.IP, effect.Ip, effect.StoreBase) ||
            previousFieldRead.Operands is not [LocalVariable, FieldReference])
            return false;

        if (instruction is
            {
                OpCode: OpCode.Add,
                IntegerBitWidth: 0 or 32,
                Operands: [FieldReference destination, FieldReference source, Immediate { Value: 1 }]
            } && ReferenceEquals(destination.Field, source.Field) &&
            ReferenceEquals(destination.Local, source.Local) &&
            ReferenceEquals(destination.Local, previousFieldAccess.Local) &&
            destination.Offset == source.Offset &&
            destination.Offset >= 0 &&
            (ulong)destination.Offset == effect.StoreDisplacement &&
            ReferenceEquals(destination.Field.DeclaringType,
                previousFieldAccess.Field.DeclaringType) &&
            ReferenceEquals(destination.Field.FieldType,
                destination.Field.AppContext.SystemTypes.SystemInt32Type))
            return NarrowFieldEqualityProof.HasUnchangedFieldLayout(destination, 32);
        return false;
    }

    private static bool MatchesDirectCallArguments(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native, List<Instruction> instructions,
        X64ArrayGuardSiteProof.Site previousSite, FieldReference previousFieldAccess,
        X64ArrayGuardSiteProof.Effect effect, Instruction call,
        MethodAnalysisContext target)
    {
        // This first compositional path accepts the two native ABI shapes that
        // the controlled fixture establishes: a no-argument instance call on
        // this, or one taking the unchanged owner loaded before the first read.
        // Every managed call operand must be tied to its native argument register.
        var thisLocals = method.ParameterLocals.Where(local => local.IsThis).Take(2).ToArray();
        if (target.IsStatic ||
            !ReferenceEquals(target.DeclaringType, method.DeclaringType) ||
            target.Parameters.Count > 1 ||
            method.ParameterOperands.FirstOrDefault() is not Register thisRegister ||
            thisRegister.Name != X86Utils.GetRegisterName(Iced.Intel.Register.RCX) ||
            thisLocals is not [{ } thisLocal] ||
            !ReferenceEquals(thisLocal.Type, method.DeclaringType) ||
            call.Operands.Count != target.Parameters.Count + 2 ||
            !ReferenceEquals(call.Operands[1], thisLocal) ||
            NativeWritesRegisterBetween(native, 0, effect.Ip, Iced.Intel.Register.RCX))
            return false;

        if (target.Parameters.Count == 0)
            return true;

        if (previousSite.OwnerFieldReadIp is not { } ownerIp ||
            previousSite.OwnerField is not { } ownerField ||
            !ReferenceEquals(target.Parameters[0].ParameterType, ownerField.FieldType) ||
            call.Operands[2] is not LocalVariable argument ||
            !ReferenceEquals(argument.Type, ownerField.FieldType) ||
            !ReferenceEquals(previousFieldAccess.Local.Type, ownerField.FieldType))
            return false;

        var managedOwnerRead = UniqueAt(instructions, ownerIp, instruction => instruction is
        {
            OpCode: OpCode.Move,
            IntegerBitWidth: 0,
            CallSemantics: CallSemantics.Direct,
            Operands: [LocalVariable, FieldReference]
        });
        if (managedOwnerRead == null ||
            !ReachesDefinition(instructions, argument, instructions.IndexOf(call),
                managedOwnerRead))
            return false;

        var ownerRead = native.SingleOrDefault(candidate => candidate.IP == ownerIp);
        if (ownerRead.IsInvalid || ownerRead.Code != Code.Mov_r64_rm64 ||
            ownerRead.Op0Kind != OpKind.Register ||
            ownerRead.Op1Kind != OpKind.Memory ||
            ownerRead.MemoryBase.GetFullRegister() != Iced.Intel.Register.RCX ||
            ownerRead.MemoryIndex != Iced.Intel.Register.None ||
            ownerRead.MemoryDisplacement64 != (ulong)ownerField.Offset ||
            ownerRead.MemorySize.GetSize() != 8)
            return false;

        var ownerRegister = ownerRead.Op0Register.GetFullRegister();
        var argumentMoves = native.Where(candidate => candidate.IP > ownerIp &&
            candidate.IP < effect.Ip && candidate.Code == Code.Mov_r64_rm64 &&
            candidate.Op0Kind == OpKind.Register &&
            candidate.Op0Register.GetFullRegister() == Iced.Intel.Register.RDX &&
            candidate.Op1Kind == OpKind.Register &&
            candidate.Op1Register.GetFullRegister() == ownerRegister).Take(2).ToArray();
        return argumentMoves is [{ } argumentMove] &&
               !NativeWritesRegisterBetween(native, ownerIp, effect.Ip, ownerRegister) &&
               !NativeWritesRegisterBetween(native, argumentMove.IP, effect.Ip,
                   Iced.Intel.Register.RDX);
    }

    private static bool NativeWritesRegisterBetween(IReadOnlyList<NativeInstruction> native,
        ulong start, ulong end, Iced.Intel.Register register)
    {
        var info = new InstructionInfoFactory();
        foreach (var instruction in native)
        {
            if (instruction.IP <= start || instruction.IP >= end)
                continue;
            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
                register is Iced.Intel.Register.RAX or Iced.Intel.Register.RCX or
                    Iced.Intel.Register.RDX or Iced.Intel.Register.R8 or
                    Iced.Intel.Register.R9 or Iced.Intel.Register.R10 or
                    Iced.Intel.Register.R11)
                return true;
            if (info.GetInfo(instruction).GetUsedRegisters().Any(used =>
                used.Register.GetFullRegister() == register && used.Access is
                    OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or
                    OpAccess.ReadCondWrite))
                return true;
        }
        return false;
    }

    private static void ValidateReferenceComparison(MethodAnalysisContext method,
        X64ArrayGuardSiteProof.Evidence evidence, List<Instruction> instructions,
        IReadOnlyList<Instruction> elementReads)
    {
        if (evidence.Comparison is not { } comparison)
        {
            if (evidence.Sites.Any(site => site.Kind == X64ArrayGuardSiteProof.ReadKind.Compare))
                throw Failure("a reference comparison has no proved native equality result");
            return;
        }
        if (evidence.Sites.Count != 2 || elementReads.Count != 2 ||
            evidence.Sites[0].Kind != X64ArrayGuardSiteProof.ReadKind.Move ||
            evidence.Sites[1].Kind != X64ArrayGuardSiteProof.ReadKind.Compare ||
            comparison.LeftElementReadIp != evidence.Sites[0].ElementReadIp ||
            comparison.RightElementReadIp != evidence.Sites[1].ElementReadIp ||
            comparison.CompareIp != evidence.Sites[1].ElementReadIp ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType))
            throw Failure("the final reference comparison has no matching native reads");

        // FlagConditionRecovery may retain the direct CMP result, while copy
        // propagation may instead leave the equivalent SETE definition. The
        // native proof binds both IPs and proves SETE is the sole flag consumer.
        var results = instructions.Where(instruction =>
            instruction.NativeAddress is { } address &&
            (address == comparison.CompareIp || address == comparison.SetEqualIp) &&
            instruction is
            {
                OpCode: OpCode.CheckEqual,
                IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable, LocalVariable, LocalVariable]
            }).Take(2).ToArray();
        if (results is not [{ } result] || result.Operands is not
            [LocalVariable boolean, LocalVariable left, LocalVariable right] ||
            !ReferenceEquals(boolean.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(left.Type, method.AppContext.SystemTypes.SystemObjectType) ||
            !ReferenceEquals(right.Type, method.AppContext.SystemTypes.SystemObjectType))
            throw Failure("the proved native reference equality lost its typed managed result");

        var resultPosition = instructions.IndexOf(result);
        if (resultPosition <= instructions.IndexOf(elementReads[1]) ||
            !(ReachesDefinition(instructions, left, resultPosition, elementReads[0]) &&
              ReachesDefinition(instructions, right, resultPosition, elementReads[1]) ||
              ReachesDefinition(instructions, right, resultPosition, elementReads[0]) &&
              ReachesDefinition(instructions, left, resultPosition, elementReads[1])) ||
            instructions.Any(instruction => instruction.NativeAddress == comparison.CompareIp &&
                instruction.IntegerBitWidth == 64 && instruction.Operands.Any(operand =>
                    operand is LocalVariable reference &&
                    ReferenceEquals(reference.Type, method.AppContext.SystemTypes.SystemObjectType))))
            throw Failure("integer arithmetic replaced a proved object-reference comparison");

        var returns = instructions.Where(instruction => instruction.OpCode == OpCode.Return).ToArray();
        if (returns is not [{ Operands: [LocalVariable returned] } finalReturn] ||
            instructions.IndexOf(finalReturn) <= resultPosition ||
            !ReachesDefinition(instructions, returned, instructions.IndexOf(finalReturn), result))
            throw Failure("the proved Boolean equality result no longer reaches the return");
    }

    private static DecompilerException Failure(string detail) =>
        new("Guarded array access proof no longer matches final managed operations: " + detail);
}
