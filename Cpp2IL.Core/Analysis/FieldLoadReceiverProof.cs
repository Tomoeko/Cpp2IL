using System;
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
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a reference-field snapshot to its original native load from an incoming
/// class receiver. This proof leaves the load and all surrounding effects in place.
/// </summary>
internal static class FieldLoadReceiverProof
{
    internal static bool HasBoundProducer(MethodAnalysisContext method,
        LocalVariable receiver, Instruction origin, Instruction use)
        => HasBoundRead(method, receiver, origin) && Precedes(method, origin, use);

    internal static bool HasBoundRead(MethodAnalysisContext method,
        LocalVariable receiver, Instruction origin)
    {
        var graph = method.ControlFlowGraph;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.Name is ".ctor" or ".cctor" || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            graph == null || !graph.Instructions.Contains(origin) ||
            origin is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable destination, FieldReference source], NativeAddress: { } loadAddress } ||
            !ReferenceEquals(destination, receiver) || receiver.IsThis || receiver.IsMethodInfo ||
            method.ParameterLocals.Contains(receiver) ||
            receiver.Type is not { } receiverType || !NullCheckedCall.IsReferenceClass(receiverType) ||
            !ReferenceEquals(source.Field.FieldType, receiverType) ||
            source.Field.Visibility != FieldAttributes.Public ||
            source.Field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } ||
            source.Offset < 16 || source.Field.DeclaringType.Definition is not { } sourceOwner ||
            (ulong)source.Offset + 8 > sourceOwner.RawSizes.instance_size ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(source) ||
            graph.Instructions.Count(instruction => ReferenceEquals(instruction.Destination, receiver)) != 1 ||
            !TryGetIncomingRegister(method, source.Local, out var incoming) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Any(local =>
                local.Register.Number == receiver.Register.Number ||
                local.Register.Number == source.Local.Register.Number) ||
            !HasUnchangedOrder(method, origin) ||
            !Enum.TryParse<NativeRegister>(receiver.Register.Name, true, out var producedRegister) ||
            X64NativeInstructionReader.ReadRootBody(method) is not { } body)
            return false;

        var matches = body.Where(instruction => instruction.IP == loadAddress).ToArray();
        return matches is [{ Code: Iced.Intel.Code.Mov_r64_rm64,
                Op0Kind: Iced.Intel.OpKind.Register, Op1Kind: Iced.Intel.OpKind.Memory } load] &&
               load.Op0Register == producedRegister &&
               receiver.Register.Copy() == new Register(null, X86Utils.GetRegisterName(load.Op0Register)) &&
               load.MemoryIndex == NativeRegister.None &&
               load.MemorySize.GetSize() == 8 && load.MemoryDisplacement64 == (ulong)source.Offset &&
               !load.HasLockPrefix && !load.HasRepPrefix && !load.HasRepnePrefix &&
               load.SegmentPrefix == NativeRegister.None &&
               X64NativeRegisterAliasProof.IsAlias(body, loadAddress, load.MemoryBase, incoming);
    }

    private static bool TryGetIncomingRegister(MethodAnalysisContext method,
        LocalVariable local, out NativeRegister register)
    {
        register = NativeRegister.None;
        if (!method.ParameterLocals.Contains(local) || local.IsMethodInfo ||
            local.Type is not { } type || !NullCheckedCall.IsReferenceClass(type) ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false))
            return false;
        int slot;
        if (local.IsThis)
        {
            if (method.IsStatic || !ReferenceEquals(local.Type, method.DeclaringType))
                return false;
            slot = 0;
        }
        else
        {
            if (LocalVariables.GetIncomingParameterIndex(method, local) is not { } index)
                return false;
            var parameter = method.Parameters[index];
            if (parameter.IsRef || parameter.OverrideParameterType != null ||
                !ReferenceEquals(parameter.ParameterType, local.Type) ||
                !ReferenceEquals(parameter.DefaultParameterType, local.Type) ||
                parameter.Attributes != parameter.DefaultAttributes || parameter.Definition?.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
            slot = index + (method.IsStatic ? 0 : 1);
        }
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        return slot < abi.Length && slot < method.ParameterOperands.Count &&
               abi[slot] is Register original && method.ParameterOperands[slot] is Register current &&
               original == current && local.Register == original &&
               Enum.TryParse(original.Name, true, out register) &&
               register is >= NativeRegister.RAX and <= NativeRegister.R15 && register != NativeRegister.RSP;
    }

    private static bool Precedes(MethodAnalysisContext method, Instruction origin, Instruction use)
    {
        var graph = method.ControlFlowGraph!;
        if (!graph.Instructions.Contains(use) || graph.FindBlockByInstruction(origin) is not { } producer ||
            graph.FindBlockByInstruction(use) is not { } consumer)
            return false;
        return ReferenceEquals(producer, consumer)
            ? producer.Instructions.IndexOf(origin) < consumer.Instructions.IndexOf(use)
            : new DominatorInfo(graph).Dominates(producer, consumer);
    }

    private static bool HasUnchangedOrder(MethodAnalysisContext method, Instruction origin)
    {
        var graph = method.ControlFlowGraph!;
        // IL emission visits these lists in order. Dominance alone would allow a
        // captured read to cross an earlier field write or a later observable call.
        var operations = graph.Blocks.SelectMany(block => block.Instructions).ToArray();
        var position = Array.IndexOf(operations, origin);
        if (position < 0)
            return false;
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = operations[index];
            if (ReferenceEquals(operation, origin) || !IsObservable(operation))
                continue;
            if (operation.NativeAddress is not { } address || address == origin.NativeAddress ||
                (index < position) != (address < origin.NativeAddress))
                return false;
        }
        return true;
    }

    private static bool IsObservable(Instruction instruction)
        => instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall or
            OpCode.Throw or OpCode.RuntimeNullThrow or OpCode.Newobj or OpCode.NewArr or
            OpCode.Divide or OpCode.DivideUnsigned or OpCode.Modulo or OpCode.ModuloUnsigned ||
           instruction.Operands.Any(operand => operand is FieldReference or ArrayAccess or ArrayLength or ISIL.MemoryOperand);
}
