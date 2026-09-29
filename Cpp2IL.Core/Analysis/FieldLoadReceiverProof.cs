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
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
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
            !HasSourceVisibility(method, source) ||
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
               X64NativeRegisterAliasProof.IsAlias(body, loadAddress, load.MemoryBase, incoming) &&
               (source.Field.Visibility == FieldAttributes.Public ||
                HasProtectedCallBeforeLoad(method, body, origin, receiver, source));
    }

    private static bool HasProtectedCallBeforeLoad(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body, Instruction origin,
        LocalVariable receiver, FieldReference source)
    {
        var graph = method.ControlFlowGraph!;
        if (body.Count != 14 || method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            !unwind.MatchesUnwind(method.UnderlyingPointer, body[^1].NextIP,
                6, 0, [6, 0x32, 2, 0x30]) ||
            method.IsStatic || method.IsVirtual || !method.IsVoid ||
            method.OverrideReturnType != null || method.Definition?.RawReturnType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            body[0].IP != method.UnderlyingPointer ||
            body[0].NextIP != method.UnderlyingPointer + 2 ||
            body[1].NextIP != method.UnderlyingPointer + 6 ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            !SingleRegister(body[0], Code.Push_r64, NativeRegister.RBX) ||
            !Stack(body[1], Code.Sub_rm64_imm8) ||
            !Registers(body[2], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[3], Code.Mov_r64_rm64, NativeRegister.RBX, NativeRegister.RCX) ||
            body[4].Code != Code.Call_rel32_64 || body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget == 0 ||
            body[5].Code != Code.Mov_r64_rm64 || body[5].IP != origin.NativeAddress ||
            body[5].Op0Kind != OpKind.Register || body[5].Op0Register != NativeRegister.RAX ||
            body[5].Op1Kind != OpKind.Memory || body[5].MemoryBase != NativeRegister.RBX ||
            body[5].MemoryIndex != NativeRegister.None || body[5].MemorySize.GetSize() != 8 ||
            body[5].MemoryDisplacement64 != (ulong)source.Offset ||
            !Registers(body[6], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            body[7].Code != Code.Je_rel8_64 || body[7].Op0Kind != OpKind.NearBranch64 ||
            body[7].NearBranchTarget != body[12].IP ||
            body[8].Code != Code.Mov_rm8_imm8 || body[8].Op0Kind != OpKind.Memory ||
            body[8].MemoryBase != NativeRegister.RAX || body[8].MemoryIndex != NativeRegister.None ||
            body[8].MemorySize.GetSize() != 1 || body[8].Op1Kind != OpKind.Immediate8 ||
            body[8].Immediate8 != 1 ||
            !Stack(body[9], Code.Add_rm64_imm8) ||
            !SingleRegister(body[10], Code.Pop_r64, NativeRegister.RBX) ||
            body[11].Code != Code.Retnq || body[11].OpCount != 0 ||
            body[12].Code != Code.Call_rel32_64 || body[12].Op0Kind != OpKind.NearBranch64 ||
            body[13].Code != Code.Int3 || body[13].OpCount != 0 ||
            body[13].IP != body[12].NextIP ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[12].NextIP, body[13].NextIP) ||
            X86RuntimeNullThrowProof.TryIdentify(method.AppContext,
                body[12].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, body.Take(13).ToArray(),
                new HashSet<ulong> { body[12].IP }) != null)
            return false;

        var calls = graph.Instructions.Where(instruction =>
            instruction.NativeAddress == body[4].IP).ToArray();
        if (calls is not [{ OpCode: OpCode.CallVoid, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [MethodAnalysisContext target, LocalVariable calledReceiver, ..] } call] ||
            call.Operands.Count is not (2 or 3) ||
            call.Operands.Count == 3 && call.Operands[2] is not Immediate { Value: 0 } ||
            !ReferenceEquals(calledReceiver, source.Local) || target.IsStatic || target.IsVirtual ||
            !target.IsVoid || target.Parameters.Count != 0 ||
            target.OverrideReturnType != null || target.Definition?.RawReturnType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(target.DeclaringType, method.DeclaringType) ||
            target.UnderlyingPointer != body[4].NearBranchTarget ||
            target.Name != target.DefaultName ||
            target.Attributes != target.DefaultAttributes ||
            target.ImplAttributes != target.DefaultImplAttributes ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(target))
            return false;

        var stores = graph.Instructions.Where(instruction =>
            instruction.NativeAddress == body[8].IP).ToArray();
        return stores is [{ OpCode: OpCode.Move, IntegerBitWidth: 0,
            CallSemantics: CallSemantics.Direct,
            Operands: [FieldReference stored, Immediate literal] }] &&
            ReferenceEquals(stored.Local, receiver) &&
            ReferenceEquals(stored.Field.FieldType,
                method.AppContext.SystemTypes.SystemBooleanType) &&
            stored.Field.BackingData?.Field.RawFieldType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, NumMods: 0, Byref: 0, Pinned: 0 } &&
            literal.Value == body[8].Immediate8 &&
            stored.Offset == (long)body[8].MemoryDisplacement64;
    }

    private static bool SingleRegister(NativeInstruction instruction, Code code,
        NativeRegister register) => instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Stack(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.Immediate8 == 0x20;

    private static bool HasSourceVisibility(MethodAnalysisContext method, FieldReference source)
    {
        var field = source.Field;
        if (field.Visibility == FieldAttributes.Public)
            return true;

        // A derived method can name an unchanged protected field on its immediate
        // base through `this`. Keep private ancestor fields unresolved even when
        // their native offset happens to match: ordinary C# cannot express that access.
        var derived = method.DeclaringType;
        var @base = derived?.BaseType;
        return field.Visibility == FieldAttributes.Family &&
               !method.IsStatic && source.Local.IsThis &&
               ReferenceEquals(source.Local.Type, derived) &&
               derived is { Definition: { GenericContainer: null }, IsGenericInstance: false } &&
               ReferenceEquals(method.Definition?.DeclaringType, derived.Definition) &&
               @base is { Definition: { GenericContainer: null }, IsGenericInstance: false } &&
               ReferenceEquals(field.DeclaringType, @base) &&
               ReferenceEquals(field.BackingData?.Field.DeclaringType, @base.Definition) &&
               field.Attributes == field.DefaultAttributes &&
               field.Name == field.DefaultName &&
               derived.Attributes == derived.DefaultAttributes &&
               @base.Attributes == @base.DefaultAttributes &&
               ReferenceEquals(derived.BaseType, derived.DefaultBaseType) &&
               ReferenceEquals(@base.BaseType, @base.DefaultBaseType);
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
