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
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Preserves proved native low32 transfers before copy propagation can erase
/// their width. Inputs remain full64 values; conversion is always explicit.
/// </summary>
internal static class IntegerTruncationRecovery
{
    private const string EvidenceKey = "x64-integer-truncation";
    private const string ResultPrefix = "integer_low32_";
    private sealed record Shift(Instruction Operation, LocalVariable Result, Register Register,
        TypeAnalysisContext Type, OpCode Opcode, ulong Address, long Count);
    private sealed record Value(LocalVariable Incoming, Register Register, TypeAnalysisContext Type,
        IReadOnlyList<Shift> Shifts);
    private sealed record Store(FieldReference Access, FieldAnalysisContext Field, LocalVariable Receiver,
        Register Register, TypeAnalysisContext Type, int Offset, TypeAnalysisContext FieldType,
        string Name, FieldAttributes Attributes, int NameIndex, int TypeIndex, uint Token, TypeStamp RawType);
    private readonly record struct TypeStamp(uint Bits, ulong Datapoint, ulong Data, uint Attributes,
        Il2CppTypeEnum Kind, uint Modifiers, uint Byref, uint Pinned, uint ValueType);
    private readonly record struct MetadataStamp(uint MethodToken, int MethodNameIndex, int ReturnTypeIndex,
        int ParameterStart, int DeclaringTypeIndex, uint OwnerToken, int OwnerNameIndex, int OwnerNamespaceIndex,
        uint OwnerBitfield, int OwnerTypeIndex, int OwnerBaseIndex, uint ParameterToken,
        int ParameterNameIndex, int ParameterTypeIndex, TypeStamp RawParameter, TypeStamp RawReturn);
    private sealed record MethodStamp(ulong Pointer, string Name, MethodAttributes Attributes,
        MethodImplAttributes ImplAttributes, TypeAnalysisContext ReturnType, string ParameterName,
        ParameterAttributes ParameterAttributes, TypeAnalysisContext ParameterType, string OwnerName,
        string OwnerNamespace, TypeAttributes OwnerAttributes, TypeAnalysisContext? BaseType, MetadataStamp Metadata);
    private sealed record Binding(Instruction Conversion, LocalVariable Source, LocalVariable Result,
        Register ResultRegister, TypeAnalysisContext ResultType, X64IntegerTruncationProof.Site Native,
        Value Value, Instruction Use, Store? Store);
    private sealed record Evidence(MethodStamp Method, IReadOnlyList<Binding> Bindings, Instruction Return);

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<Evidence>(EvidenceKey) != null || method.Locals.Any(IsOwnedResult) ||
        method.ControlFlowGraph?.Instructions.Any(instruction => instruction.Operands.OfType<LocalVariable>().Any(IsOwnedResult)) == true;

    private static bool IsOwnedResult(LocalVariable local) => local.Register.Name.StartsWith(ResultPrefix, StringComparison.Ordinal);

    internal static void Run(MethodAnalysisContext method)
    {
        method.PutExtraData<Evidence>(EvidenceKey, null!);
        if (!Signature(method) || LinearBody(method) is not { } block ||
            X64IntegerTruncationProof.FindBody(method) is not { } body || !GraphShape(method, body, final: false) ||
            block.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop) is not
                { OpCode: OpCode.Return } ret || ret.NativeAddress != body[^1].IP)
            return;
        var pending = new List<(Instruction Operation, LocalVariable Source, LocalVariable Result,
            X64IntegerTruncationProof.Site Native, Value Value, Instruction Use, Store? Store)>();
        foreach (var native in body.Select(X64IntegerTruncationProof.TryGetSite).Where(site => site != null))
        {
            if (block.Instructions.Where(instruction => instruction.NativeAddress == native!.Address &&
                    instruction.OpCode == OpCode.Move).ToArray() is not [var operation] ||
                operation is not { IntegerBitWidth: 0 or 32, CallSemantics: CallSemantics.Direct,
                    Operands: [var destination, LocalVariable source] } ||
                StorageBits(method, source.Type) != 64 ||
                !TryBindValue(method, body, source, operation, native!.Address, native.Source, out var value))
                return;
            LocalVariable result;
            Store? store = null;
            Instruction use;
            if (destination is LocalVariable local)
            {
                if (native.Destination == NativeRegister.None || native.Receiver != NativeRegister.None ||
                    !X64IntegerTruncationProof.MatchesRegister(local.Register, native.Destination) ||
                    !PrivateLocal(method, local, operation) ||
                    ret.Operands is not [LocalVariable returned] || !ReferenceEquals(returned, local) ||
                    StorageBits(method, method.ReturnType) != 32 ||
                    block.Instructions.SelectMany(OperandEffects.ReadLocals).Count(read => ReferenceEquals(read, local)) != 1 ||
                    !X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(body, body[^1].IP,
                        NativeRegister.RAX, native.Address))
                    return;
                result = local;
                use = ret;
            }
            else if (destination is FieldReference access)
            {
                if (native.Destination != NativeRegister.None ||
                    !TryBindStore(method, body, access, native, out store))
                    return;
                result = new LocalVariable("integerLow32", new Register(null, ResultPrefix + native.Address.ToString("x"), version: 0),
                    access.Field.FieldType);
                use = operation;
            }
            else
                return;
            pending.Add((operation, source, result, native, value!, use, store));
        }
        if (pending.Count == 0 || pending.Any(item => !Precedes(method, item.Operation, ret) &&
                !ReferenceEquals(item.Use, ret)) ||
            ret.Operands.Count != (ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ? 0 : 1))
            return;

        var bindings = new List<Binding>();
        // Apply only after all native sites, field widths, producers and uses close.
        foreach (var item in pending)
        {
            var resultType = item.Store?.FieldType ?? method.ReturnType;
            var signed = ReferenceEquals(resultType, method.AppContext.SystemTypes.SystemInt32Type);
            Instruction conversion;
            item.Result.Type = resultType;
            if (item.Store == null)
            {
                // The private slot also detects a lost binding at final emission.
                // Its native parent64 result has only the separately proved I4
                // return use; a later I8 observation needs another explicit proof.
                item.Result.Register = new Register(null, ResultPrefix + item.Native.Address.ToString("x"), version: 0);
                conversion = item.Operation;
                conversion.OpCode = OpCode.IntegerExtend;
                conversion.IntegerBitWidth = 0;
                conversion.SetOperands(item.Result, item.Source, new Immediate(32), new Immediate(32), new Immediate(signed ? 1 : 0));
            }
            else
            {
                conversion = new Instruction(item.Operation.Index, OpCode.IntegerExtend, item.Result, item.Source,
                    new Immediate(32), new Immediate(32), new Immediate(signed ? 1 : 0)) { NativeAddress = item.Native.Address };
                block.Instructions.Insert(block.Instructions.IndexOf(item.Operation), conversion);
                method.Locals.Add(item.Result);
                item.Operation.SetOperand(1, item.Result);
                item.Operation.IntegerBitWidth = 32;
            }
            bindings.Add(new Binding(conversion, item.Source, item.Result, item.Result.Register, resultType,
                item.Native, item.Value, item.Use, item.Store));
        }
        method.PutExtraData(EvidenceKey, new Evidence(Snapshot(method), bindings, ret));
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (method.GetExtraData<Evidence>(EvidenceKey) is not { } saved || !Signature(method) ||
            Snapshot(method) != saved.Method || LinearBody(method) is not { } block ||
            X64IntegerTruncationProof.FindBody(method) is not { } body ||
            !GraphShape(method, body, final: true) ||
            block.Instructions.LastOrDefault(instruction => instruction.OpCode != OpCode.Nop) is not { } ret ||
            !ReferenceEquals(ret, saved.Return) || ret.OpCode != OpCode.Return || ret.NativeAddress != body[^1].IP ||
            ret.IntegerBitWidth != 0 || ret.CallSemantics != CallSemantics.Direct ||
            body.Count(instruction => X64IntegerTruncationProof.TryGetSite(instruction) != null) != saved.Bindings.Count ||
            block.Instructions.Count(instruction => instruction.OpCode == OpCode.IntegerExtend) != saved.Bindings.Count ||
            !block.Instructions.Where(instruction => instruction.OpCode is OpCode.ShiftRight or OpCode.ShiftRightUnsigned)
                .SequenceEqual(saved.Bindings.SelectMany(binding => binding.Value.Shifts).Select(shift => shift.Operation).Distinct()) ||
            body.Count(instruction => instruction.Code is Code.Shr_rm64_imm8 or Code.Sar_rm64_imm8) !=
                saved.Bindings.SelectMany(binding => binding.Value.Shifts).Select(shift => shift.Operation).Distinct().Count())
            return false;
        foreach (var binding in saved.Bindings)
        {
            var conversion = binding.Conversion;
            if (!block.Instructions.Contains(conversion) || conversion.NativeAddress != binding.Native.Address ||
                conversion.CallSemantics != CallSemantics.Direct ||
                !IntegerExtension.TryGet(conversion, out var extension) ||
                extension != new IntegerExtension(32, 32,
                    ReferenceEquals(binding.ResultType, method.AppContext.SystemTypes.SystemInt32Type)) ||
                !extension.HasCanonicalTypes(conversion, method.AppContext) ||
                conversion.Operands[0] is not LocalVariable result || !ReferenceEquals(result, binding.Result) ||
                result.Register != binding.ResultRegister || !ReferenceEquals(result.Type, binding.ResultType) ||
                !PrivateLocal(method, result, conversion) || conversion.Operands[1] is not LocalVariable source ||
                !TryBindValue(method, body, source, conversion, binding.Native.Address, binding.Native.Source, out var value) ||
                !SameValue(value!, binding.Value) ||
                body.Where(instruction => instruction.IP == binding.Native.Address).ToArray() is not [var native] ||
                X64IntegerTruncationProof.TryGetSite(native) != binding.Native ||
                !Precedes(method, conversion, binding.Use) ||
                block.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, result)) != 1)
                return false;
            if (binding.Store is { } store)
            {
                if (binding.Use is not { OpCode: OpCode.Move, IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
                        Operands: [FieldReference access, LocalVariable stored] } ||
                    binding.Use.NativeAddress != binding.Native.Address || !ReferenceEquals(stored, result) ||
                    !ReferenceEquals(access, store.Access) || !TryBindStore(method, body, access, binding.Native, out var current) ||
                    current != store || !Precedes(method, binding.Use, ret))
                    return false;
            }
            else if (!ReferenceEquals(binding.Use, ret) || ret.Operands is not [LocalVariable returned] ||
                     !ReferenceEquals(returned, result) || !ReferenceEquals(result.Type, method.ReturnType) ||
                     !X64NativeRegisterAliasProof.IsAliasFromIntegerTruncation(body, body[^1].IP,
                         NativeRegister.RAX, binding.Native.Address))
                return false;
        }
        var nativeStores = body.Select(X64IntegerTruncationProof.TryGetSite).Where(site => site != null && site.Receiver != NativeRegister.None)
            .Select(site => site!.Address).ToArray();
        var managedStores = block.Instructions.Where(instruction => instruction.Operands.FirstOrDefault() is FieldReference)
            .Select(instruction => instruction.NativeAddress).ToArray();
        return nativeStores.Select(address => (ulong?)address).SequenceEqual(managedStores) &&
               ret.Operands.Count == (ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType) ? 0 : 1);
    }

    private static bool Signature(MethodAnalysisContext method) =>
        method.Parameters.Count == 1 && StorageBits(method, method.Parameters[0].ParameterType) == 64 &&
        method.Parameters[0].Definition?.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8,
            NumMods: 0, Byref: 0, Pinned: 0 } parameter && parameter.Type == method.Parameters[0].ParameterType.Type &&
        !method.Parameters[0].IsRef &&
        (StorageBits(method, method.ReturnType) == 32 || ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType)) &&
        method.Definition?.RawReturnType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
            Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } returned && returned.Type == method.ReturnType.Type &&
        method.DeclaringType is { } owner && owner.Attributes == owner.DefaultAttributes &&
        ReferenceEquals(owner.BaseType, owner.DefaultBaseType);

    private static int StorageBits(MethodAnalysisContext method, TypeAnalysisContext? type) =>
        IntegerExtension.StorageBits(type, method.AppContext.SystemTypes);

    private static MethodStamp Snapshot(MethodAnalysisContext method)
    {
        var definition = method.Definition!;
        var owner = method.DeclaringType!.Definition!;
        var parameter = method.Parameters[0].Definition!;
        var metadata = new MetadataStamp(definition.token, definition.nameIndex, definition.returnTypeIdx.Value,
            definition.parameterStart.Value, definition.declaringTypeIdx.Value, owner.Token, owner.NameIndex,
            owner.NamespaceIndex, owner.Bitfield, owner.ByvalTypeIndex.Value, owner.ParentIndex.Value,
            parameter.token, parameter.nameIndex, parameter.typeIndex.Value,
            Snapshot(parameter.RawType!), Snapshot(definition.RawReturnType!));
        return new(method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes, method.ReturnType,
            method.Parameters[0].Name, method.Parameters[0].Attributes, method.Parameters[0].ParameterType,
            method.DeclaringType!.Name, method.DeclaringType.Namespace, method.DeclaringType.Attributes,
            method.DeclaringType.BaseType, metadata);
    }

    private static TypeStamp Snapshot(Il2CppType type) => new(type.Bits, type.Datapoint, type.Data.Dummy,
        type.Attrs, type.Type, type.NumMods, type.Byref, type.Pinned, type.ValueType);

    private static bool TryBindValue(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> body,
        LocalVariable source, Instruction use, ulong address, NativeRegister register, out Value? value)
    {
        value = null;
        var visited = new HashSet<LocalVariable>();
        return Bind(source, use, address, register, out value);

        bool Bind(LocalVariable local, Instruction consumer, ulong useAddress, NativeRegister useRegister, out Value? result)
        {
            result = null;
            if (!visited.Add(local) || local.IsThis || local.IsMethodInfo || StorageBits(method, local.Type) != 64 ||
                OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph!.Instructions).Any(mutable =>
                    mutable.Register.Number == local.Register.Number))
                return false;
            if (LocalVariables.GetIncomingParameterIndex(method, local) is { } parameter)
            {
                var abi = new X64CallingConventionResolver().ResolveForParameters(method);
                var slot = parameter + (method.IsStatic ? 0 : 1);
                if (parameter != 0 || slot >= abi.Length || slot >= method.ParameterOperands.Count ||
                    method.ParameterLocals.Count(candidate => ReferenceEquals(candidate, local)) != 1 ||
                    abi[slot] is not Register original || original != local.Register ||
                    method.ParameterOperands[slot] is not Register current || current != original ||
                    method.Locals.Any(candidate => !ReferenceEquals(candidate, local) && candidate.Register == original) ||
                    !ReferenceEquals(local.Type, method.Parameters[parameter].ParameterType) ||
                    method.ControlFlowGraph.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)) ||
                    !Enum.TryParse<NativeRegister>(original.Name, true, out var incoming) ||
                    !X64NativeRegisterAliasProof.IsAlias(body, useAddress, useRegister, incoming))
                    return false;
                result = new Value(local, local.Register, local.Type!, []);
                return true;
            }
            if (method.ControlFlowGraph.Instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray()
                    is not [var producer] || !Precedes(method, producer, consumer) ||
                producer.NativeAddress is not { } produced ||
                body.Where(instruction => instruction.IP == produced).ToArray() is not [var native] ||
                producer.CallSemantics != CallSemantics.Direct)
                return false;
            if (producer is { OpCode: OpCode.Move, IntegerBitWidth: 0, Operands: [_, LocalVariable copied] } &&
                native.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && native.Op0Kind == OpKind.Register &&
                native.Op1Kind == OpKind.Register && X64IntegerTruncationProof.MatchesRegister(local.Register, native.Op0Register))
                return Bind(copied, consumer, useAddress, useRegister, out result);
            if (producer is not { OpCode: OpCode.ShiftRight or OpCode.ShiftRightUnsigned, IntegerBitWidth: 64,
                    Operands: [LocalVariable destination, LocalVariable input, Immediate count] } ||
                !ReferenceEquals(destination, local) ||
                native.Code != (producer.OpCode == OpCode.ShiftRight ? Code.Sar_rm64_imm8 : Code.Shr_rm64_imm8) ||
                native.Op0Kind != OpKind.Register || native.Op1Kind != OpKind.Immediate8 ||
                count.Value != native.Immediate8 || count.Value is < 1 or > 63 ||
                !X64IntegerTruncationProof.MatchesRegister(local.Register, native.Op0Register) ||
                !X64NativeRegisterAliasProof.IsAliasFromIntegerShift(body, useAddress, useRegister, produced) ||
                !Bind(input, producer, produced, native.Op0Register, out var prior))
                return false;
            result = prior! with { Shifts = prior.Shifts.Append(new Shift(producer, local, local.Register, local.Type!,
                producer.OpCode, produced, count.Value)).ToArray() };
            return true;
        }
    }

    private static bool SameValue(Value left, Value right) =>
        ReferenceEquals(left.Incoming, right.Incoming) && left.Register == right.Register &&
        ReferenceEquals(left.Type, right.Type) && left.Shifts.SequenceEqual(right.Shifts);

    private static bool TryBindStore(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> body,
        FieldReference access, X64IntegerTruncationProof.Site native, out Store? store)
    {
        store = null;
        var receiver = access.Local;
        if (method.IsStatic || native.Receiver == NativeRegister.None || native.Offset != access.Offset ||
            access.Offset < 16 || StorageBits(method, access.Field.FieldType) != 32 ||
            access.Field.Name != access.Field.DefaultName || access.Field.BackingData?.Field.RawFieldType?.Type != access.Field.FieldType.Type ||
            (access.Field.Attributes & FieldAttributes.InitOnly) != 0 ||
            access.Field.DeclaringType.Definition is not { } owner ||
            (ulong)access.Offset + 4 > owner.RawSizes.instance_size ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32) ||
            !receiver.IsThis || receiver.IsMethodInfo || !ReferenceEquals(receiver.Type, method.DeclaringType) ||
            method.ParameterLocals.Count(candidate => ReferenceEquals(candidate, receiver)) != 1 ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, receiver)) ||
            OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph.Instructions).Any(local =>
                local.Register.Number == receiver.Register.Number))
            return false;
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        if (abi.Length == 0 || method.ParameterOperands.Count == 0 || abi[0] is not Register original ||
            method.ParameterOperands[0] is not Register current || current != original || original != receiver.Register ||
            !Enum.TryParse<NativeRegister>(original.Name, true, out var incoming) ||
            !X64NativeRegisterAliasProof.IsAlias(body, native.Address, native.Receiver, incoming))
            return false;
        store = new Store(access, access.Field, receiver, receiver.Register, receiver.Type!, access.Offset,
            access.Field.FieldType, access.Field.Name, access.Field.Attributes, access.Field.BackingData!.Field.nameIndex,
            access.Field.BackingData.Field.typeIndex.Value, access.Field.BackingData.Field.token,
            Snapshot(access.Field.BackingData.Field.RawFieldType!));
        return true;
    }

    private static bool PrivateLocal(MethodAnalysisContext method, LocalVariable local, Instruction definition) =>
        !local.IsThis && !local.IsMethodInfo && !method.ParameterLocals.Contains(local) &&
        method.ControlFlowGraph!.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, local)) == 1 &&
        ReferenceEquals(definition.Destination, local) &&
        !OperandEffects.LocalsWithMutableStorage(method.ControlFlowGraph.Instructions).Any(mutable =>
            mutable.Register.Number == local.Register.Number);

    private static bool GraphShape(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> body, bool final) =>
        method.ControlFlowGraph!.Instructions.Count(instruction => instruction.OpCode == OpCode.Return) == 1 &&
        method.ControlFlowGraph.Instructions.All(instruction => instruction.CallSemantics == CallSemantics.Direct &&
            (instruction.OpCode switch
            {
                OpCode.Nop => instruction.IntegerBitWidth == 0 && instruction.Operands.Count == 0,
                OpCode.Return => instruction.IntegerBitWidth == 0,
                OpCode.ShiftRight or OpCode.ShiftRightUnsigned => instruction.IntegerBitWidth == 64 &&
                    instruction.Operands is [LocalVariable result, LocalVariable input, Immediate] &&
                    StorageBits(method, result.Type) == 64 && StorageBits(method, input.Type) == 64,
                OpCode.IntegerExtend => final,
                OpCode.Move => IsRegisterMove(method, body, instruction, final) ||
                               instruction.Operands is [FieldReference, LocalVariable] && instruction.IntegerBitWidth is 0 or 32,
                _ => false,
            }));

    private static bool IsRegisterMove(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> body,
        Instruction operation, bool final)
    {
        if (operation is not { IntegerBitWidth: 0, NativeAddress: { } address,
                Operands: [LocalVariable result, LocalVariable source] } || StorageBits(method, source.Type) != 64 ||
            body.Where(instruction => instruction.IP == address).ToArray() is not [var native] ||
            native.Op0Kind != OpKind.Register || native.Op1Kind != OpKind.Register ||
            !X64IntegerTruncationProof.MatchesRegister(result.Register, native.Op0Register.GetFullRegister()))
            return false;
        return !final && X64IntegerTruncationProof.TryGetSite(native) is { Destination: not NativeRegister.None } ||
               native.Code is Code.Mov_r64_rm64 or Code.Mov_rm64_r64 && StorageBits(method, result.Type) == 64 &&
               TryBindValue(method, body, source, operation, address, native.Op1Register, out _);
    }

    private static bool Precedes(MethodAnalysisContext method, Instruction producer, Instruction consumer) =>
        LinearBody(method) is { } block && block.Instructions.IndexOf(producer) is var first && first >= 0 &&
        block.Instructions.IndexOf(consumer) > first;

    private static Block? LinearBody(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph;
        if (graph == null || graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.Blocks.Where(block => block != graph.EntryBlock && block != graph.ExitBlock).ToArray() is not [var body] ||
            graph.EntryBlock.Successors is not [var entry] || !ReferenceEquals(entry, body) ||
            body.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, graph.EntryBlock) ||
            body.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock) ||
            graph.ExitBlock.Predecessors is not [var final] || !ReferenceEquals(final, body))
            return null;
        return body;
    }
}
