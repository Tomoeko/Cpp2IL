using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using MemoryOperand = Cpp2IL.Core.ISIL.MemoryOperand;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    private sealed record ReferenceStore(Instruction Operation, FieldAnalysisContext Field, int OwnerEntry,
        Origin Value, ulong Setup, ulong Address, ulong Barrier, ulong BarrierTarget);

    // A retained write through this+fieldOffset is still a field write. Bind its
    // exact address and value snapshots, including the independently identified
    // GC card-marker call, before replacing only its operand representation.
    private static bool NormalizeReferenceStores(MethodAnalysisContext caller, NativeInstruction[] body,
        X64NativeInvocationValues values, List<Action> undo, out ReferenceStore[] stores)
    {
        var results = new List<ReferenceStore>();
        stores = [];
        var graph = caller.ControlFlowGraph!;
        foreach (var operation in graph.Instructions.ToArray())
        {
            if (operation.Operands is not [MemoryOperand, _]) continue;
            if (operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    NativeAddress: { } address, Operands: [MemoryOperand { Base: LocalVariable pointer, Index: null,
                        Addend: 0, Scale: 0 }, LocalVariable stored] } ||
                Escaped(caller, pointer) || !ReachingDefinition(caller, pointer, operation, out var setup) ||
                setup is not { OpCode: OpCode.Add, IntegerBitWidth: 64, CallSemantics: CallSemantics.Direct,
                    NativeAddress: { } setupIp, Operands: [LocalVariable destination, LocalVariable owner,
                        Immediate { Value: >= 16 and <= int.MaxValue } offset] } ||
                !ReferenceEquals(destination, pointer) ||
                graph.Instructions.SelectMany(OperandEffects.ReadLocals).Count(local => ReferenceEquals(local, pointer)) != 1 ||
                !TryOrigin(caller, owner, setup, out var ownerOrigin) || ownerOrigin.Definition != null ||
                !Incoming(caller, ownerOrigin.Entry, ownerOrigin.Type, out var incoming) ||
                !TryOrigin(caller, stored, operation, out var valueOrigin) ||
                ownerOrigin.Type.Fields.Where(field => !field.IsStatic && field.Offset == offset.Value &&
                    ReferenceEquals(field.FieldType, valueOrigin.Type)).ToArray() is not [var field] ||
                !AccessibleField(caller, field) || (field.Attributes & FieldAttributes.InitOnly) != 0 ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(new FieldReference(field, owner, field.Offset)) ||
                body.SingleOrDefault(native => native.IP == setupIp) is not
                    { Code: Code.Add_rm64_imm8 or Code.Add_rm64_imm32, Op0Kind: OpKind.Register,
                        Op0Register: NativeRegister.RCX } addition || addition.GetImmediate(1) != (ulong)offset.Value ||
                !values.Matches(setupIp, NativeRegister.RCX, 64, new(incoming)) ||
                body.SingleOrDefault(native => native.IP == address) is not { Code: Code.Mov_rm64_r64,
                    Op0Kind: OpKind.Memory, Op1Kind: OpKind.Register, Op1Register: NativeRegister.RDX } nativeStore ||
                nativeStore.MemoryBase != NativeRegister.RCX || nativeStore.MemoryIndex != NativeRegister.None ||
                nativeStore.MemoryDisplacement64 != 0 || nativeStore.MemorySize.GetSize() != 8 ||
                !values.Dominates(setupIp, address) ||
                !PointerUnchanged(body, setupIp, address) ||
                !BindReceiver(caller, valueOrigin, body, values, address, NativeRegister.RDX) ||
                body.SingleOrDefault(native => native.IP == nativeStore.NextIP) is not { Code: Code.Call_rel32_64 } barrier ||
                !values.HasCallFrame(barrier.IP, false) || caller.AppContext.Binary is not PE pe ||
                X64UnwindProof.ForApplication(caller.AppContext) is not { } unwind ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, barrier.NearBranchTarget))
                return false;
            var originalOperands = operation.Operands.ToList();
            var setupOperands = setup.Operands.ToList();
            undo.Add(() =>
            {
                operation.SetOperands(originalOperands);
                setup.OpCode = OpCode.Add;
                setup.IntegerBitWidth = 64;
                setup.SetOperands(setupOperands);
            });
            operation.SetOperand(0, new FieldReference(field, owner, field.Offset));
            setup.OpCode = OpCode.Nop;
            setup.IntegerBitWidth = 0;
            setup.SetOperands();
            results.Add(new(operation, field, ownerOrigin.Entry, valueOrigin, setupIp, address,
                barrier.IP, barrier.NearBranchTarget));
        }
        stores = results.ToArray();
        return true;
    }

    private static bool ReferenceStoreRetained(MethodAnalysisContext caller, NativeInstruction[] body,
        X64NativeInvocationValues values, ReferenceStore store) =>
        caller.ControlFlowGraph!.Instructions.Contains(store.Operation) &&
        store.Operation is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
            Operands: [FieldReference field, LocalVariable written] } &&
        ReferenceEquals(field.Field, store.Field) && field.Offset == store.Field.Offset &&
        store.Operation.NativeAddress == store.Address && AccessibleField(caller, store.Field) &&
        (store.Field.Attributes & FieldAttributes.InitOnly) == 0 &&
        NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(field) &&
        TryOrigin(caller, field.Local, store.Operation, out var owner) && owner.Definition == null &&
        owner.Entry == store.OwnerEntry && Incoming(caller, owner.Entry, owner.Type, out var register) &&
        values.Matches(store.Setup, NativeRegister.RCX, 64, new(register)) &&
        TryOrigin(caller, written, store.Operation, out var value) && value == store.Value &&
        BindReceiver(caller, value, body, values, store.Address, NativeRegister.RDX) &&
        body.Any(native => native.IP == store.Barrier && native.Code == Code.Call_rel32_64 &&
            native.NearBranchTarget == store.BarrierTarget) && caller.AppContext.Binary is PE pe &&
        X64UnwindProof.ForApplication(caller.AppContext) is { } unwind &&
        X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, store.BarrierTarget);

    private static bool PointerUnchanged(NativeInstruction[] body, ulong setup, ulong store)
    {
        var information = new InstructionInfoFactory();
        foreach (var native in body.Where(native => native.IP > setup && native.IP < store))
            if (native.FlowControl != FlowControl.Next || information.GetInfo(native).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == NativeRegister.RCX && used.Access is
                        OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                return false;
        return true;
    }
}
