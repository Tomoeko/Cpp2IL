using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using Instruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a scalar parameter/positive-zero selection and its receiver guard to one
/// ordinary floating field store. Normal paths are checked separately for all
/// four IEEE comparison outcomes, both in SSA and after phi destruction.
/// </summary>
internal static class X64ScalarFloatingFieldStoreProof
{
    internal const string EvidenceKey = "X64ScalarFloatingFieldStoreProof.Stores";
    private sealed record Evidence(Instruction Operation, FieldReference Access,
        LocalVariable Receiver, Instruction Comparison, Instruction Branch,
        NativeInstruction[] Body, int Width);
    private sealed record Comparison(ulong Ip, string Left, string Right);
    private sealed record NativePaths(Comparison Comparison, string[] Values, ulong GuardIp, ulong TestIp);
    private const string ReceiverValue = "receiver";
    private const string ZeroValue = "positive-zero";

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<List<Evidence>>(EvidenceKey) != null;

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
            method.GetExtraData<List<Evidence>>(EvidenceKey) is not { Count: 1 } records) return false;
        var record = records[0];
        return method.NullCheckedFieldAccesses.Count(item => ReferenceEquals(item.Operation, record.Operation) &&
                   ReferenceEquals(item.Access, record.Access)) == 1 &&
               IsValidFor(method, record.Operation, record.Access);
    }

    internal static bool HasRecord(MethodAnalysisContext method, Instruction operation) =>
        method.GetExtraData<List<Evidence>>(EvidenceKey)?.Any(item => ReferenceEquals(item.Operation, operation)) == true;

    internal static bool TryRecord(MethodAnalysisContext method, Instruction comparison, Instruction branch,
        LocalVariable receiver, Instruction operation, FieldReference access, IOperand value)
    {
        try
        {
            if (!ReferenceEquals(access.Local, receiver) || operation.Operands is not [FieldReference stored, var source] ||
                !ReferenceEquals(stored, access) || !ReferenceEquals(source, value) ||
                comparison.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || comparison.IntegerBitWidth != 64 ||
                comparison.CallSemantics != CallSemantics.Direct || comparison.Operands.Count != 3 ||
                !comparison.Operands.Contains(receiver) || !comparison.Operands.OfType<Immediate>().Any(literal => literal.Value == 0) ||
                branch.IntegerBitWidth != 0 || branch.CallSemantics != CallSemantics.Direct ||
                !TryWidth(access, out var width) || ReadBody(method) is not { } body)
                return false;
            var evidence = new Evidence(operation, access, receiver, comparison, branch, body, width);
            if (!Valid(method, evidence, beforeRewrite: true)) return false;
            var records = method.GetExtraData<List<Evidence>>(EvidenceKey) ?? [];
            records.RemoveAll(item => ReferenceEquals(item.Operation, operation));
            records.Add(evidence);
            method.PutExtraData(EvidenceKey, records);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool IsValidFor(MethodAnalysisContext method, Instruction operation, FieldReference access)
    {
        try
        {
            return method.GetExtraData<List<Evidence>>(EvidenceKey)?.SingleOrDefault(item =>
                ReferenceEquals(item.Operation, operation) && ReferenceEquals(item.Access, access)) is { } evidence &&
                Valid(method, evidence, beforeRewrite: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (X64NativeInstructionReader.ReadRootBody(method) is not { Length: > 0 } body ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) || body[0].Length != 4 ||
            !unwind.MatchesUnwind(method.UnderlyingPointer, body[^1].NextIP, 4, 0, [4, 0x42])) return null;
        return body;
    }

    private static bool TryWidth(FieldReference access, out int width)
    {
        var types = access.Field.AppContext.SystemTypes;
        width = ReferenceEquals(access.Field.FieldType, types.SystemSingleType) ? 32 :
            ReferenceEquals(access.Field.FieldType, types.SystemDoubleType) ? 64 : 0;
        return width != 0 && access.Field.BackingData?.Field.RawFieldType is
            { NumMods: 0, Byref: 0, Pinned: 0, Type: var kind } &&
            kind == (width == 32 ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8);
    }

    private static bool Valid(MethodAnalysisContext method, Evidence evidence, bool beforeRewrite)
    {
        var graph = method.ControlFlowGraph;
        var access = evidence.Access;
        var owner = method.DeclaringType;
        if (graph == null || !graph.Instructions.Contains(evidence.Operation) ||
            evidence.Operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, NativeAddress: { } storeIp,
                Operands: [FieldReference stored, LocalVariable value] } ||
            !ReferenceEquals(stored, access) || !ReferenceEquals(access.Local, evidence.Receiver) ||
            !ReferenceEquals(value.Type, access.Field.FieldType) || !TryWidth(access, out var width) || width != evidence.Width ||
            owner is not { Definition: { GenericContainer: null, PackingSizeIsDefault: true,
                ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } || owner.IsValueType || owner.IsInterface ||
            owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            !ReferenceEquals(owner.BaseType, method.AppContext.SystemTypes.SystemObjectType) ||
            owner.Definition.RawBaseType is not { NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT } rawBase ||
            !ReferenceEquals(method.AppContext.ResolveIl2CppType(rawBase), owner.BaseType) ||
            owner.BaseType.Attributes != owner.BaseType.DefaultAttributes ||
            owner.BaseType.Name != owner.BaseType.DefaultName || owner.BaseType.Namespace != owner.BaseType.DefaultNamespace ||
            owner.BaseType.BaseType != null || owner.BaseType.DefaultBaseType != null || owner.BaseType.GenericParameters.Count != 0 ||
            (owner.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
            !ReferenceEquals(access.Field.DeclaringType, owner) || !owner.Fields.Contains(access.Field) ||
            access.Field.Name != access.Field.DefaultName || access.Field.Attributes != access.Field.DefaultAttributes ||
            access.Field.Visibility != FieldAttributes.Public || access.Field.IsStatic ||
            (access.Field.Attributes & FieldAttributes.InitOnly) != 0 ||
            !ReferenceEquals(access.Field.BackingData?.Field.DeclaringType, owner.Definition) ||
            !NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, width) ||
            method.IsStatic || !method.IsVoid || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            method.Definition!.RawReturnType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID ||
            !evidence.Receiver.IsThis || !method.ParameterLocals.Contains(evidence.Receiver) ||
            !ReferenceEquals(evidence.Receiver.Type, owner) ||
            graph.Instructions.Any(item => ReferenceEquals(item.Destination, evidence.Receiver)) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Count != 0 ||
            !OrdinaryParameters(method, width) || ReadBody(method) is not { } body ||
            body.Length != evidence.Body.Length || body.Length > 48 ||
            body.Where((instruction, index) => instruction.IP != evidence.Body[index].IP ||
                instruction.NextIP != evidence.Body[index].NextIP || !instruction.Equals(evidence.Body[index])).Any() ||
            body.SingleOrDefault(item => item.IP == storeIp) is not { Length: > 0 } nativeStore ||
            !X64ScalarFloatSelectionProof.CanProject(method, nativeStore, out var projectedWidth) || projectedWidth != width ||
            !TryNativePaths(method, body, access, storeIp, width, out var paths) ||
            evidence.Comparison.NativeAddress != paths.TestIp || evidence.Branch.NativeAddress != paths.GuardIp ||
            (beforeRewrite ? evidence.Branch.OpCode != OpCode.ConditionalJump : evidence.Branch.OpCode != OpCode.Jump))
            return false;
        for (var outcome = 0; outcome < 4; outcome++)
            if (!TypedPath(method, evidence, paths, outcome)) return false;
        return true;
    }

    private static bool OrdinaryParameters(MethodAnalysisContext method, int width)
    {
        var type = width == 32 ? method.AppContext.SystemTypes.SystemSingleType : method.AppContext.SystemTypes.SystemDoubleType;
        var kind = width == 32 ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8;
        if (method.Parameters.Count is < 1 or > 3 ||
            method.AppContext.InstructionSet.CallingConventionResolver is not X64CallingConventionResolver resolver ||
            resolver.ReturnsViaHiddenBuffer(method)) return false;
        var abi = resolver.ResolveForParameters(method);
        return abi.Length == method.Parameters.Count + 2 && abi[0] is ISIL.Register { Name: "rcx" } &&
            method.Parameters.Select((parameter, index) => (parameter, index)).All(pair =>
                pair.parameter.ParameterIndex == pair.index && ReferenceEquals(pair.parameter.DeclaringMethod, method) &&
                pair.parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0, Type: var raw } && raw == kind &&
                !pair.parameter.IsRef && pair.parameter.OverrideParameterType == null && !pair.parameter.UseOverrideDefaultValue &&
                pair.parameter.Name == pair.parameter.DefaultName && pair.parameter.Attributes == pair.parameter.DefaultAttributes &&
                ReferenceEquals(pair.parameter.ParameterType, type) && ReferenceEquals(pair.parameter.DefaultParameterType, type) &&
                abi[pair.index + 1] is ISIL.Register register && register.Name == $"xmm{pair.index + 1}");
    }

    // Outcomes: less, equal, greater, unordered. Expressions retain incoming
    // parameter identity and selection operand order, including NaN/signed-zero semantics.
    private static bool TryNativePaths(MethodAnalysisContext method, NativeInstruction[] body,
        FieldReference access, ulong storeIp, int width, out NativePaths paths)
    {
        paths = null!;
        var stores = body.Where(item => item.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd && item.Op0Kind == OpKind.Memory).ToArray();
        var comparisons = body.Where(item => item.Mnemonic is Mnemonic.Comiss or Mnemonic.Ucomiss or Mnemonic.Comisd or Mnemonic.Ucomisd).ToArray();
        var tests = body.Where(item => item.Code == Code.Test_rm64_r64 && item.Op0Register == NativeRegister.RCX && item.Op1Register == NativeRegister.RCX).ToArray();
        if (stores is not [var store] || store.IP != storeIp || store.MemoryBase != NativeRegister.RCX ||
            store.MemoryIndex != NativeRegister.None || store.MemoryDisplacement64 != (ulong)access.Offset ||
            store.MemorySize.GetSize() != width / 8 || store.Op1Kind != OpKind.Register ||
            comparisons is not [var comparison] || comparison.Op0Kind != OpKind.Register || comparison.Op1Kind != OpKind.Register ||
            tests is not [var test]) return false;
        var guardIndex = Array.FindIndex(body, item => item.IP == test.IP) + 1;
        if (guardIndex <= 0 || guardIndex >= body.Length || body[guardIndex] is not { Code: Code.Je_rel8_64 or Code.Je_rel32_64 } guard ||
            body.SingleOrDefault(item => item.IP == guard.NearBranchTarget) is not { Code: Code.Call_rel32_64 } helper ||
            X86RuntimeNullThrowProof.TryIdentify(method.AppContext, helper.NearBranchTarget) == null ||
            body.Any(item => item.FlowControl == FlowControl.Call && item.IP != helper.IP)) return false;
        var values = new string[4];
        Comparison? boundComparison = null;
        for (var outcome = 0; outcome < 4; outcome++)
        {
            var registers = new Dictionary<NativeRegister, string>();
            for (var parameter = 0; parameter < method.Parameters.Count; parameter++)
                registers[(NativeRegister)((int)NativeRegister.XMM1 + parameter)] = ParameterValue(parameter);
            var seen = new HashSet<ulong>();
            var index = 0;
            var compared = false;
            var comparisonFlagsLive = false;
            var guarded = false;
            var stored = false;
            while (index >= 0 && index < body.Length)
            {
                var instruction = body[index++];
                if (!seen.Add(instruction.IP)) return false;
                if (instruction.IP == test.IP) { guarded = true; comparisonFlagsLive = false; continue; }
                if (instruction.IP == guard.IP) { if (!guarded) return false; continue; }
                if (instruction.IP == storeIp)
                {
                    if (!guarded || stored || !registers.TryGetValue(instruction.Op1Register, out var value)) return false;
                    stored = true; values[outcome] = value; continue;
                }
                if (instruction.IP == comparison.IP)
                {
                    if (compared || !registers.TryGetValue(instruction.Op0Register, out var left) ||
                        !registers.TryGetValue(instruction.Op1Register, out var right)) return false;
                    var current = new Comparison(instruction.IP, left, right);
                    if (boundComparison != null && boundComparison != current) return false;
                    boundComparison = current; compared = true; comparisonFlagsLive = true; continue;
                }
                if (instruction.FlowControl == FlowControl.ConditionalBranch)
                {
                    if (!comparisonFlagsLive || guarded || !NativeCondition(instruction.ConditionCode, outcome, out var taken)) return false;
                    if (taken) index = Array.FindIndex(body, item => item.IP == instruction.NearBranchTarget);
                    continue;
                }
                if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                { index = Array.FindIndex(body, item => item.IP == instruction.NearBranchTarget); continue; }
                if (instruction.Code == Code.Retnq)
                {
                    if (!stored || !compared || index < 2 || !X64Stack28BodyProof.Stack(body[index - 2], Mnemonic.Add)) return false;
                    break;
                }
                if (instruction.Mnemonic is Mnemonic.Xorps or Mnemonic.Xorpd or Mnemonic.Pxor &&
                    instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
                    instruction.Op0Register == instruction.Op1Register)
                { registers[instruction.Op0Register] = ZeroValue; continue; }
                if (instruction.Mnemonic is Mnemonic.Movaps or Mnemonic.Movups or Mnemonic.Movapd or Mnemonic.Movupd or Mnemonic.Movss or Mnemonic.Movsd &&
                    instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
                    registers.TryGetValue(instruction.Op1Register, out var copied))
                { registers[instruction.Op0Register] = copied; continue; }
                if (X64ScalarFloatSelectionProof.IsSelection(instruction) &&
                    registers.TryGetValue(instruction.Op0Register, out var first) && registers.TryGetValue(instruction.Op1Register, out var second))
                { registers[instruction.Op0Register] = SelectionValue(instruction.IP, first, second); continue; }
                if (instruction.Mnemonic == Mnemonic.Nop) continue;
                if (X64Stack28BodyProof.Stack(instruction, Mnemonic.Sub) && instruction.IP == body[0].IP ||
                    X64Stack28BodyProof.Stack(instruction, Mnemonic.Add) && stored && index < body.Length && body[index].Code == Code.Retnq)
                { comparisonFlagsLive = false; continue; }
                return false;
            }
            if (!stored || index < 0 || index > body.Length) return false;
        }
        paths = new NativePaths(boundComparison!, values, guard.IP, test.IP);
        return boundComparison != null;
    }

    private static bool NativeCondition(ConditionCode condition, int outcome, out bool value)
    {
        var carry = outcome is 0 or 3;
        var zero = outcome is 1 or 3;
        value = condition switch { ConditionCode.a => !carry && !zero, ConditionCode.be => carry || zero,
            ConditionCode.b => carry, ConditionCode.ae => !carry, ConditionCode.e => zero,
            ConditionCode.ne => !zero, ConditionCode.p => outcome == 3, ConditionCode.np => outcome != 3, _ => false };
        return condition is ConditionCode.a or ConditionCode.be or ConditionCode.b or ConditionCode.ae or
            ConditionCode.e or ConditionCode.ne or ConditionCode.p or ConditionCode.np;
    }

    private static bool TypedPath(MethodAnalysisContext method, Evidence evidence, NativePaths paths, int outcome)
    {
        var graph = method.ControlFlowGraph!;
        var values = new Dictionary<LocalVariable, string>();
        var booleans = new Dictionary<LocalVariable, bool>();
        foreach (var local in method.ParameterLocals)
        {
            if (ReferenceEquals(local, evidence.Receiver)) values[local] = ReceiverValue;
            else if (LocalVariables.GetIncomingParameterIndex(method, local) is { } index)
                values[local] = ParameterValue(index);
        }
        var seen = new HashSet<Block>();
        var block = graph.EntryBlock;
        Block? predecessor = null;
        var stored = false;
        var visitedGuard = false;
        var lastNativeAddress = method.UnderlyingPointer;
        while (block != graph.ExitBlock)
        {
            if (!seen.Add(block) || seen.Count > 24) return false;
            Block? next = null;
            foreach (var instruction in block.Instructions)
            {
                if (instruction.CallSemantics != CallSemantics.Direct) return false;
                if (instruction.OpCode == OpCode.Nop) continue;
                if (instruction.NativeAddress is { } address)
                {
                    if (address < lastNativeAddress || !evidence.Body.Any(item => item.IP == address)) return false;
                    lastNativeAddress = address;
                }
                if (instruction.OpCode == OpCode.Phi && instruction.Operands[0] is LocalVariable phi)
                {
                    var position = predecessor == null ? -1 : block.Predecessors.IndexOf(predecessor);
                    if (position < 0 || instruction.Operands.Count != block.Predecessors.Count + 1 ||
                        !Value(instruction.Operands[position + 1], out var phiSource) || !FloatingType(phi)) return false;
                    values[phi] = phiSource; continue;
                }
                if (ReferenceEquals(instruction, evidence.Operation))
                {
                    if (stored || !visitedGuard || instruction.Operands is not [FieldReference field, var source] ||
                        !ReferenceEquals(field, evidence.Access) || !Value(field.Local, out var receiver) || receiver != ReceiverValue ||
                        !Value(source, out var value) || value != paths.Values[outcome]) return false;
                    stored = true; continue;
                }
                if (instruction.OpCode == OpCode.FloatProject && FloatProjection.TryGet(instruction, out var projection) &&
                    projection.Width == evidence.Width && instruction.Operands[0] is LocalVariable projected && FloatingType(projected) &&
                    Value(instruction.Operands[1], out var projectSource) && NativeProjection(instruction))
                { values[projected] = projectSource; continue; }
                if (instruction.OpCode == OpCode.FloatSelect && FloatSelection.TryGet(instruction, out var selection) &&
                    selection.Width == evidence.Width && instruction.Operands[0] is LocalVariable selected && FloatingType(selected) &&
                    Value(instruction.Operands[1], out var left) && Value(instruction.Operands[2], out var right) &&
                    evidence.Body.SingleOrDefault(item => item.IP == instruction.NativeAddress) is { } nativeSelection &&
                    X64ScalarFloatSelectionProof.IsSelection(nativeSelection) &&
                    selection.Maximum == (nativeSelection.Mnemonic is Mnemonic.Maxss or Mnemonic.Maxsd))
                { values[selected] = SelectionValue(nativeSelection.IP, left, right); continue; }
                if (instruction.OpCode == OpCode.FloatCompare && instruction.NativeAddress == paths.Comparison.Ip &&
                    instruction.Operands is [LocalVariable flag, var first, var second, Immediate bits, Immediate predicate] &&
                    bits.Value == evidence.Width && Value(first, out var firstValue) && Value(second, out var secondValue) &&
                    firstValue == paths.Comparison.Left && secondValue == paths.Comparison.Right && predicate.Value is 8 or 9 or 10)
                { booleans[flag] = predicate.Value == 8 ? outcome == 3 : predicate.Value == 9 ? outcome is 0 or 3 : outcome is 1 or 3; continue; }
                if (instruction.OpCode == OpCode.Move && instruction.Operands is [LocalVariable copied, var copy] && instruction.IntegerBitWidth == 0)
                {
                    if (Value(copy, out var copiedValue) && (FloatingType(copied) || copiedValue == ReceiverValue && ReferenceEquals(copied.Type, evidence.Receiver.Type)))
                    { values[copied] = copiedValue; continue; }
                    if (Boolean(copy, out var copiedFlag)) { booleans[copied] = copiedFlag; continue; }
                    return false;
                }
                if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual &&
                    instruction.Operands is [LocalVariable checkedValue, var firstOperand, var secondOperand])
                {
                    if (Boolean(firstOperand, out var firstFlag) && Boolean(secondOperand, out var secondFlag))
                        booleans[checkedValue] = instruction.OpCode == OpCode.CheckEqual ? firstFlag == secondFlag : firstFlag != secondFlag;
                    else if (instruction.NativeAddress == paths.TestIp && Value(firstOperand, out var receiver) && receiver == ReceiverValue &&
                        secondOperand is Immediate { Value: 0 })
                        booleans[checkedValue] = instruction.OpCode == OpCode.CheckNotEqual;
                    else return false;
                    continue;
                }
                if (instruction.OpCode is OpCode.And or OpCode.Or or OpCode.Xor &&
                    instruction.Operands is [LocalVariable flagResult, var firstFlagOperand, var secondFlagOperand] &&
                    Boolean(firstFlagOperand, out var firstBoolean) && Boolean(secondFlagOperand, out var secondBoolean))
                { booleans[flagResult] = instruction.OpCode == OpCode.And ? firstBoolean && secondBoolean :
                    instruction.OpCode == OpCode.Or ? firstBoolean || secondBoolean : firstBoolean != secondBoolean; continue; }
                if (instruction.OpCode == OpCode.Not && instruction.Operands is [LocalVariable negated, var flagOperand] &&
                    instruction.IntegerBitWidth == 0 && Boolean(flagOperand, out var flagValue))
                { booleans[negated] = !flagValue; continue; }
                if (instruction.OpCode == OpCode.ConditionalJump && instruction.Operands is [Block taken, var condition] &&
                    block.Successors.Count == 2 && block.Successors.Contains(taken) && Boolean(condition, out var truth))
                {
                    if (ReferenceEquals(instruction, evidence.Branch)) visitedGuard = true;
                    else if (evidence.Body.SingleOrDefault(item => item.IP == instruction.NativeAddress) is not
                        { FlowControl: FlowControl.ConditionalBranch } nativeBranch ||
                        !NativeCondition(nativeBranch.ConditionCode, outcome, out var nativeTruth) || truth != nativeTruth) return false;
                    next = truth ? taken : block.Successors.Single(item => !ReferenceEquals(item, taken)); continue;
                }
                if (instruction.OpCode == OpCode.Jump && instruction.Operands is [Block target] && block.Successors.Contains(target))
                { if (ReferenceEquals(instruction, evidence.Branch)) visitedGuard = true; next = target; continue; }
                if (instruction.OpCode == OpCode.Return && instruction.Operands.Count == 0 && stored)
                { next = graph.ExitBlock; continue; }
                return false;
            }
            if (next == null)
            {
                if (block.Successors.Count != 1) return false;
                next = block.Successors[0];
            }
            predecessor = block; block = next;
        }
        return stored && visitedGuard;

        bool FloatingType(LocalVariable local) => ReferenceEquals(local.Type, evidence.Access.Field.FieldType);
        bool Value(IOperand operand, out string value)
        {
            if (operand is LocalVariable local && values.TryGetValue(local, out value!))
                return value == ReceiverValue || FloatingType(local);
            value = ZeroValue;
            return evidence.Width == 32 && operand is FloatLiteral single && BitConverter.DoubleToInt64Bits(single.Value) == 0 ||
                evidence.Width == 64 && operand is DoubleLiteral number && BitConverter.DoubleToInt64Bits(number.Value) == 0;
        }
        bool Boolean(IOperand operand, out bool value)
        {
            if (operand is LocalVariable local && booleans.TryGetValue(local, out value)) return true;
            value = operand is Immediate { Value: 1 };
            return operand is Immediate { Value: 0 or 1 };
        }
        bool NativeProjection(Instruction instruction) => evidence.Body.SingleOrDefault(item => item.IP == instruction.NativeAddress) is { } native &&
            (native.Mnemonic is Mnemonic.Xorps or Mnemonic.Xorpd or Mnemonic.Pxor && native.Op0Register == native.Op1Register ||
             native.Mnemonic is Mnemonic.Movaps or Mnemonic.Movapd or Mnemonic.Movups or Mnemonic.Movupd or Mnemonic.Movss or Mnemonic.Movsd &&
             native.Op0Kind == OpKind.Register && native.Op1Kind == OpKind.Register);
    }

    private static string ParameterValue(int index) => $"parameter:{index}";
    private static string SelectionValue(ulong ip, string first, string second) => $"selection:{ip}({first},{second})";
}
