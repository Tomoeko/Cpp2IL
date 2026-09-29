using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Proves the storage and original receiver of individual fields before the
/// first unknown generic field. It does not prove the surrounding method body.
/// </summary>
internal static class OpenGenericPrefixFieldLayoutProof
{
    private const string EvidenceKey = "OpenGenericPrefixFieldLayoutProof";
    private sealed record Site(ManagedInstruction Operation, int OperandIndex, ulong Address,
        FieldAnalysisContext Field, LocalVariable Receiver, int Offset, int Width);
    private sealed record LocalFact(LocalVariable Local, ManagedRegister Register,
        TypeAnalysisContext? Type, bool IsThis, bool IsMethodInfo, bool IsReturn);
    private sealed record FieldFact(FieldReference Operand, FieldAnalysisContext Field,
        LocalVariable Receiver, int Offset);
    private sealed record OperationFact(ManagedInstruction Operation, OpCode OpCode, int Width,
        CallSemantics CallSemantics, ulong? Address, IOperand[] Operands,
        FieldFact[] Fields, LocalFact[] Locals);
    private sealed record BlockFact(Cpp2IL.Core.Graphs.Block Block, ManagedInstruction[] Operations,
        Cpp2IL.Core.Graphs.Block[] Predecessors, Cpp2IL.Core.Graphs.Block[] Successors);
    private sealed record Binding(Site[] Sites, NativeInstruction[] Native, byte[] Bytes,
        object?[] Metadata, BlockFact[] Blocks, OperationFact[] Operations,
        LocalVariable Incoming, IOperand[] Abi);

    internal static FieldAnalysisContext? TryResolve(MethodAnalysisContext method,
        ManagedInstruction operation, ISIL.MemoryOperand memory, TypeAnalysisContext owner,
        int operandIndex)
    {
        try
        {
            if (!OriginalMethod(method) || method.DeclaringType is not { } declared ||
                !OpenGenericEarlyFieldProof.OriginalOpenOwner(method, owner, declared) ||
                !Incoming(method, owner, out var incoming, out _) ||
                ReadBody(method) is not { } body ||
                operation.NativeAddress is not { } address ||
                body.SingleOrDefault(native => native.IP == address) is not { IsInvalid: false } native ||
                NativeSite(native, operation) is not { } site ||
                site.Offset != memory.Addend ||
                OpenGenericEarlyFieldProof.PrefixField(method, declared, site.Offset, site.Width)
                    is not { } field ||
                !X64NativeRegisterAliasProof.IsAlias(body, native.IP, Iced.Intel.Register.RCX,
                    Iced.Intel.Register.RCX) ||
                operandIndex < 0 || operandIndex >= operation.Operands.Count ||
                !site.Indices.Contains(operandIndex) ||
                operation.Operands[operandIndex] is not ISIL.MemoryOperand current ||
                current.Base is not LocalVariable receiver || !ReferenceEquals(receiver, incoming) ||
                !SameMemory(current, memory))
                return null;
            var saved = method.GetExtraData<List<Site>>(EvidenceKey);
            if (saved == null)
            {
                saved = [];
                method.PutExtraData(EvidenceKey, saved);
            }
            if (saved.Any(previous => ReferenceEquals(previous.Operation, operation) &&
                    previous.OperandIndex == operandIndex))
                return null;
            saved.Add(new Site(operation, operandIndex, address, field, receiver,
                site.Offset, site.Width));
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
            return field;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static void Run(MethodAnalysisContext method)
    {
        if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
            method.GetExtraData<List<Site>>(EvidenceKey) is not { Count: > 0 } sites)
            return;
        try
        {
            if (Capture(method, sites) is { } binding)
                method.PutExtraData(EvidenceKey, binding);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            // The durable marker makes the final validator reject this site.
        }
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey);

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (method.GetExtraData<Binding>(EvidenceKey) is not { } saved ||
                Capture(method, saved.Sites) is not { } current ||
                !saved.Native.SequenceEqual(current.Native) ||
                !saved.Bytes.SequenceEqual(current.Bytes) ||
                !saved.Metadata.SequenceEqual(current.Metadata) ||
                !ReferenceEquals(saved.Incoming, current.Incoming) ||
                !saved.Abi.SequenceEqual(current.Abi) ||
                saved.Blocks.Length != current.Blocks.Length ||
                saved.Operations.Length != current.Operations.Length)
                return false;
            for (var i = 0; i < saved.Blocks.Length; i++)
                if (!ReferenceEquals(saved.Blocks[i].Block, current.Blocks[i].Block) ||
                    !saved.Blocks[i].Operations.SequenceEqual(current.Blocks[i].Operations) ||
                    !saved.Blocks[i].Predecessors.SequenceEqual(current.Blocks[i].Predecessors) ||
                    !saved.Blocks[i].Successors.SequenceEqual(current.Blocks[i].Successors))
                    return false;
            for (var i = 0; i < saved.Operations.Length; i++)
                if (!SameOperation(saved.Operations[i], current.Operations[i]))
                    return false;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static Binding? Capture(MethodAnalysisContext method, IReadOnlyList<Site> sites)
    {
        if (!OriginalMethod(method) || method.DeclaringType is not { } declared ||
            ReadBody(method) is not { } body ||
            !Incoming(method, method.ParameterLocals.FirstOrDefault(local => local.IsThis)?.Type,
                out var incoming, out var abi) ||
            method.ControlFlowGraph is not { } graph || graph.EntryBlock.Instructions.Count != 0 ||
            graph.ExitBlock.Instructions.Count != 0 ||
            graph.EntryBlock.Predecessors.Count != 0 || graph.ExitBlock.Successors.Count != 0 ||
            graph.Blocks.Distinct().Count() != graph.Blocks.Count ||
            !graph.Blocks.Contains(graph.EntryBlock) || !graph.Blocks.Contains(graph.ExitBlock) ||
            sites.Count is 0 or > 32 || sites.Select(site => (site.Operation, site.OperandIndex)).Distinct().Count() != sites.Count)
            return null;
        var facts = new List<object?>
        {
            declared, declared.Definition, declared.Name, declared.Namespace, declared.Attributes,
            declared.Definition!.Flags, declared.Definition.Bitfield, declared.Definition.Token,
            declared.Definition.FirstFieldIdx, declared.Definition.FieldCount,
            declared.Definition.GenericContainerIndex, declared.Definition.ParentIndex,
            method.Definition, method.Name, method.Attributes, method.ImplAttributes,
            method.Definition!.nameIndex, method.Definition.token, method.Definition.parameterCount,
            method.Definition.parameterStart, method.Definition.returnTypeIdx,
            method.UnderlyingPointer,
        };
        var rawReturn = method.Definition.RawReturnType;
        var rawParameters = method.Definition.InternalParameterData ?? [];
        if (rawReturn == null || method.Definition.parameterCount != rawParameters.Length ||
            method.Parameters.Count != rawParameters.Length || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !method.AppContext.Binary.TryGetTypeVirtualAddress(rawReturn, out var returnAddress) ||
            !ReferenceEquals(ClosedGenericValueLayoutProof.ReadUnchangedType(method.AppContext,
                returnAddress), rawReturn))
            return null;
        facts.Add(method.ReturnType);
        CaptureRawType(rawReturn, facts);
        for (var i = 0; i < rawParameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var raw = rawParameters[i];
            var rawType = raw.RawType;
            if (!ReferenceEquals(parameter.Definition, raw) ||
                parameter.OverrideParameterType != null ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                rawType == null ||
                !method.AppContext.Binary.TryGetTypeVirtualAddress(rawType, out var parameterAddress) ||
                !ReferenceEquals(ClosedGenericValueLayoutProof.ReadUnchangedType(method.AppContext,
                    parameterAddress), rawType))
                return null;
            facts.Add(parameter); facts.Add(parameter.ParameterType);
            facts.Add(parameter.Name); facts.Add(parameter.Attributes);
            facts.Add(parameter.ParameterIndex); facts.Add(raw);
            facts.Add(raw.nameIndex); facts.Add(raw.token); facts.Add(raw.typeIndex);
            CaptureRawType(rawType, facts);
        }
        foreach (var field in declared.Fields)
        {
            var raw = field.BackingData?.Field;
            if (raw?.RawFieldType == null)
                return null;
            facts.Add(field); facts.Add(field.Name); facts.Add(field.Attributes);
            facts.Add(field.Offset); facts.Add(raw); facts.Add(raw.nameIndex);
            facts.Add(raw.typeIndex); facts.Add(raw.token);
            facts.Add(raw.RawFieldType.Datapoint); facts.Add(raw.RawFieldType.Bits);
        }
        foreach (var site in sites)
        {
            if (!ReferenceEquals(site.Receiver, incoming) ||
                !OpenGenericEarlyFieldProof.OriginalOpenOwner(method, incoming.Type, declared) ||
                OpenGenericEarlyFieldProof.PrefixField(method, declared, site.Offset, site.Width)
                    is not { } field || !ReferenceEquals(field, site.Field) ||
                body.SingleOrDefault(native => native.IP == site.Address) is not { IsInvalid: false } native ||
                NativeSite(native, site.Operation) is not { } shape ||
                shape.Offset != site.Offset || shape.Width != site.Width ||
                !shape.Indices.Contains(site.OperandIndex) ||
                !X64NativeRegisterAliasProof.IsAlias(body, native.IP, Iced.Intel.Register.RCX,
                    Iced.Intel.Register.RCX) ||
                site.Operation.NativeAddress != site.Address ||
                site.Operation.Operands.Count <= site.OperandIndex ||
                site.Operation.Operands[site.OperandIndex] is not FieldReference bound ||
                !ReferenceEquals(bound.Local, incoming) || bound.Offset != site.Offset ||
                !(ReferenceEquals(incoming.Type, declared)
                    ? ReferenceEquals(bound.Field, field)
                    : bound.Field is ConcreteGenericFieldAnalysisContext projected &&
                      ReferenceEquals(projected.BaseFieldContext, field) &&
                      ReferenceEquals(projected.DeclaringType, incoming.Type) &&
                      projected.OverrideFieldType == null && projected.OverrideOffset == null &&
                      ReferenceEquals(projected.FieldType, field.FieldType)))
                return null;
            facts.Add(site.Operation); facts.Add(site.Address);
            facts.Add(site.OperandIndex); facts.Add(field);
        }
        foreach (var group in sites.GroupBy(site => site.Address))
        {
            var operation = group.First().Operation;
            if (group.Any(site => !ReferenceEquals(site.Operation, operation)) ||
                graph.Instructions.Where(candidate => candidate.NativeAddress == group.Key).ToArray()
                    is not [var sole] || !ReferenceEquals(sole, operation))
                return null;
            var native = body.Single(instruction => instruction.IP == group.Key);
            var shape = NativeSite(native, operation);
            if (shape == null || !group.Select(site => site.OperandIndex).OrderBy(index => index)
                    .SequenceEqual(shape.Value.Indices.OrderBy(index => index)))
                return null;
        }
        var blocks = graph.Blocks.Select(block => new BlockFact(block,
            block.Instructions.ToArray(), block.Predecessors.ToArray(), block.Successors.ToArray())).ToArray();
        var operations = blocks.Where(block => block.Block != graph.EntryBlock && block.Block != graph.ExitBlock)
            .SelectMany(block => block.Operations).ToArray();
        if (!operations.SequenceEqual(graph.Instructions) ||
            sites.Any(site => !operations.Contains(site.Operation)) ||
            operations.Any(operation => operation.OpCode == OpCode.Nop &&
                (operation.Operands.Count != 0 || operation.CallSemantics != CallSemantics.Direct ||
                 operation.IntegerBitWidth != 0)))
            return null;
        var bytes = CurrentBytes(method, body);
        return bytes == null ? null : new Binding(sites.ToArray(), body, bytes,
            facts.ToArray(), blocks, operations.Select(CaptureOperation).ToArray(), incoming, abi);
    }

    private static OperationFact CaptureOperation(ManagedInstruction operation)
    {
        var operands = operation.Operands.ToArray();
        var fields = operands.OfType<FieldReference>()
            .Select(field => new FieldFact(field, field.Field, field.Local, field.Offset)).ToArray();
        var locals = operands.OfType<LocalVariable>()
            .Concat(fields.Select(field => field.Receiver)).Distinct()
            .Select(local => new LocalFact(local, local.Register.Copy(), local.Type,
                local.IsThis, local.IsMethodInfo, local.IsReturn)).ToArray();
        return new OperationFact(operation, operation.OpCode, operation.IntegerBitWidth,
            operation.CallSemantics, operation.NativeAddress, operands, fields, locals);
    }

    private static bool SameOperation(OperationFact original, OperationFact current)
    {
        if (!ReferenceEquals(original.Operation, current.Operation) || original.OpCode != current.OpCode ||
            original.Width != current.Width || original.CallSemantics != current.CallSemantics ||
            original.Address != current.Address || original.Operands.Length != current.Operands.Length ||
            !original.Operands.Zip(current.Operands, (left, right) => ReferenceEquals(left, right)).All(equal => equal) ||
            original.Fields.Length != current.Fields.Length || original.Locals.Length != current.Locals.Length)
            return false;
        for (var i = 0; i < original.Fields.Length; i++)
            if (!ReferenceEquals(original.Fields[i].Operand, current.Fields[i].Operand) ||
                !ReferenceEquals(original.Fields[i].Field, current.Fields[i].Field) ||
                !ReferenceEquals(original.Fields[i].Receiver, current.Fields[i].Receiver) ||
                original.Fields[i].Offset != current.Fields[i].Offset)
                return false;
        for (var i = 0; i < original.Locals.Length; i++)
            if (!ReferenceEquals(original.Locals[i].Local, current.Locals[i].Local) ||
                original.Locals[i].Register != current.Locals[i].Register ||
                !ReferenceEquals(original.Locals[i].Type, current.Locals[i].Type) ||
                original.Locals[i].IsThis != current.Locals[i].IsThis ||
                original.Locals[i].IsMethodInfo != current.Locals[i].IsMethodInfo ||
                original.Locals[i].IsReturn != current.Locals[i].IsReturn)
                return false;
        return true;
    }

    private static bool OriginalMethod(MethodAnalysisContext method) =>
        method.AppContext.Binary is PE { PointerSizeBytes: 8 } &&
        X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) &&
        method.DeclaringType is { Definition: { } definition } &&
        method.Definition is { } raw && ReferenceEquals(raw.DeclaringType, definition) &&
        !method.IsStatic && !method.IsAbstract && method.Name != ".cctor" &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        method.UnderlyingPointer != 0 &&
        method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) &&
        bindings.Any(candidate => ReferenceEquals(candidate, method));

    private static bool Incoming(MethodAnalysisContext method, TypeAnalysisContext? owner,
        out LocalVariable receiver, out IOperand[] abi)
    {
        receiver = null!;
        abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var expected = abi;
        if (owner == null || expected.Length == 0 || expected[0] is not ManagedRegister
                { Name: "rcx", Version: -1 } first ||
            !expected.SequenceEqual(method.ParameterOperands) ||
            method.ParameterLocals.Where(local => local.IsThis).ToArray() is not
                [{ IsMethodInfo: false } incoming] ||
            !ReferenceEquals(incoming.Type, owner) || incoming.Register != first ||
            method.ParameterLocals.Count(local => local.Register.Number == incoming.Register.Number) != 1 ||
            method.Locals.Count(local => ReferenceEquals(local, incoming)) > 1 ||
            method.Locals.Any(local => local.Register.Number == incoming.Register.Number &&
                !ReferenceEquals(local, incoming)) ||
            method.ControlFlowGraph?.Instructions.Any(operation =>
                ReferenceEquals(operation.Destination, incoming)) == true)
            return false;
        receiver = incoming;
        return true;
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } index ||
            method.UnderlyingPointer == 0)
            return null;
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        var length = method.RawBytes.Length;
        if (length is < 2 or > 1024 || method.UnderlyingPointer > ulong.MaxValue - (ulong)length)
            return null;
        var start = method.UnderlyingPointer;
        var end = start + (ulong)length;
        var classification = index.ClassifySpan(start, end);
        if (classification.Kind != X64UnwindProof.SpanKind.NoEntry ||
            classification.Start != start || classification.End != end ||
            method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < end))
            return null;
        var cached = X86Utils.Disassemble(method.RawBytes.AsSpan(), start, false).ToArray();
        if (cached.Length is < 2 or > 256 || cached[^1].Code != Code.Retnq ||
            cached[^1].OpCount != 0 || cached[^1].NextIP != end)
            return null;
        if (X64NativeInstructionReader.Read(pe, index, start, cached.Length, length) is not { } current ||
            !cached.SequenceEqual(current) ||
            X86CallerExceptionRegionProof.Check(method, cached, new HashSet<ulong>()) != null ||
            !AllReachable(cached))
            return null;
        var bytes = CurrentBytes(method, cached);
        return bytes != null && bytes.AsSpan().SequenceEqual(method.RawBytes.AsSpan())
            ? cached : null;
    }

    private static bool AllReachable(IReadOnlyList<NativeInstruction> body)
    {
        var byAddress = body.Select((instruction, index) => (instruction.IP, index))
            .ToDictionary(pair => pair.IP, pair => pair.index);
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (!visited.Add(index))
                continue;
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Iced.Intel.Register.None)
                return false;
            switch (instruction.FlowControl)
            {
                case FlowControl.Return:
                    if (instruction.Code != Code.Retnq || instruction.OpCount != 0)
                        return false;
                    break;
                case FlowControl.ConditionalBranch:
                    if (instruction.Op0Kind != OpKind.NearBranch64 ||
                        !byAddress.TryGetValue(instruction.NearBranchTarget, out var target))
                        return false;
                    pending.Push(target);
                    goto case FlowControl.Next;
                case FlowControl.Next:
                    if (index + 1 >= body.Count)
                        return false;
                    pending.Push(index + 1);
                    break;
                default:
                    return false;
            }
        }
        return visited.Count == body.Count;
    }

    private static (int Offset, int Width, int[] Indices)? NativeSite(
        NativeInstruction native, ManagedInstruction operation)
    {
        if (native.MemoryBase != Iced.Intel.Register.RCX ||
            native.MemoryIndex != Iced.Intel.Register.None || native.MemoryIndexScale != 1 ||
            native.MemoryDisplacement64 is < 16 or > 4095 ||
            native.HasLockPrefix || native.HasRepPrefix || native.HasRepnePrefix ||
            native.SegmentPrefix != Iced.Intel.Register.None)
            return null;
        int[]? indices = native.Code switch
        {
            Code.Mov_r32_rm32 when native.Op0Register == Iced.Intel.Register.EAX &&
                operation is { OpCode: OpCode.Move, Operands.Count: 2 } => [1],
            Code.Mov_r64_rm64 when native.Op0Register is >= Iced.Intel.Register.RAX and <= Iced.Intel.Register.R15 &&
                operation is { OpCode: OpCode.Move, Operands.Count: 2 } => [1],
            Code.Mov_rm32_r32 when native.Op1Kind == OpKind.Register &&
                operation is { OpCode: OpCode.Move, Operands.Count: 2 } => [0],
            Code.Mov_rm8_imm8 when native.Immediate8 is 0 or 1 &&
                operation is { OpCode: OpCode.Move, Operands.Count: 2 } => [0],
            Code.Add_rm32_r32 when native.Op1Kind == OpKind.Register &&
                operation is { OpCode: OpCode.Add, IntegerBitWidth: 32, Operands.Count: 3 } => [0, 1],
            Code.Add_r32_rm32 when native.Op0Kind == OpKind.Register &&
                operation is { OpCode: OpCode.Add, IntegerBitWidth: 32, Operands.Count: 3 } => [2],
            _ => null,
        };
        if (indices == null || native.MemorySize.GetSize() is not (1 or 4 or 8))
            return null;
        var width = native.MemorySize.GetSize() * 8;
        if ((native.Code is Code.Add_rm32_r32 or Code.Add_r32_rm32 or Code.Mov_r32_rm32 or
                Code.Mov_rm32_r32) && width != 32 ||
            native.Code == Code.Mov_r64_rm64 && width != 64 ||
            native.Code == Code.Mov_rm8_imm8 && width != 8)
            return null;
        return ((int)native.MemoryDisplacement64, width, indices);
    }

    private static bool SameMemory(ISIL.MemoryOperand left, ISIL.MemoryOperand right) =>
        ReferenceEquals(left.Base, right.Base) && ReferenceEquals(left.Index, right.Index) &&
        left.Addend == right.Addend && left.Scale == right.Scale &&
        left.Index == null && left.Scale == 0;

    private static void CaptureRawType(LibCpp2IL.BinaryStructures.Il2CppType raw,
        List<object?> facts)
    {
        facts.Add(raw);
        facts.Add(raw.Bits);
        facts.Add(raw.Datapoint);
        facts.Add(raw.Data.Dummy);
    }

    private static byte[]? CurrentBytes(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        if (method.AppContext.Binary is not PE pe)
            return null;
        var length = checked((int)(native[^1].NextIP - native[0].IP));
        var offset = pe.MapVirtualAddressToRaw(native[0].IP, false);
        var binary = pe.GetRawBinaryContent();
        return offset >= 0 && offset <= binary.Length - length
            ? binary.Slice(checked((int)offset), length).ToArray() : null;
    }
}
