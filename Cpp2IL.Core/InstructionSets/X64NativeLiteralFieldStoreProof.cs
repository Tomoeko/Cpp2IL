using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates a byte or dword immediate store at its original caller site.</summary>
internal static class X64NativeLiteralFieldStoreProof
{
    internal static bool IsValidFor(MethodAnalysisContext caller, PE pe,
        X64UnwindProof.Index unwind, ulong address, FieldReference access,
        uint value, int width, bool proveReceiverOrigin = false)
    {
        if (width is not (8 or 32) || width == 8 && value > byte.MaxValue ||
            address < caller.UnderlyingPointer ||
            address - caller.UnderlyingPointer > int.MaxValue - 15 ||
            !Enum.TryParse<NativeRegister>(access.Local.Register.Name,
                true, out var receiverRegister))
            return false;
        try
        {
            if (caller.RawBytes.Length == 0)
                caller.EnsureRawBytes();
            var body = X86Utils.Iterate(caller).ToArray();
            var native = body.Where(instruction =>
                instruction.IP == address).ToArray();
            if (native is not [var store] ||
                store.Code != (width == 8 ? Code.Mov_rm8_imm8 : Code.Mov_rm32_imm32) ||
                store.Op0Kind != OpKind.Memory ||
                store.Op1Kind != (width == 8 ? OpKind.Immediate8 : OpKind.Immediate32) ||
                store.IsInvalid || store.CodeSize != CodeSize.Code64 ||
                store.HasLockPrefix || store.HasRepPrefix || store.HasRepnePrefix ||
                store.SegmentPrefix != NativeRegister.None ||
                (!proveReceiverOrigin && store.MemoryBase != receiverRegister) ||
                store.MemoryIndex != NativeRegister.None ||
                store.MemorySize.GetSize() != width / 8 ||
                store.MemoryDisplacement64 != (ulong)access.Offset ||
                (width == 8 ? store.Immediate8 : store.Immediate32) != value ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, address, (uint)store.Length) ||
                unwind.ClassifySpan(address, store.NextIP).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree)
                return false;
            if (proveReceiverOrigin)
            {
                if (X64NativeInstructionReader.ReadRootBody(caller) is not { } rootBody)
                    return false;
                body = rootBody;
                ulong? producerCall = null;
                if (caller.ParameterLocals.Contains(access.Local))
                {
                    var index = access.Local.IsThis ? 0 :
                        Analysis.LocalVariables.GetIncomingParameterIndex(caller, access.Local) is { } parameter
                            ? parameter + (caller.IsStatic ? 0 : 1) : -1;
                    var abi = new X64CallingConventionResolver().ResolveForParameters(caller);
                    if (index < 0 || index >= abi.Length ||
                        index >= caller.ParameterOperands.Count ||
                        abi[index] is not ISIL.Register original ||
                        caller.ParameterOperands[index] is not ISIL.Register current ||
                        current != original || access.Local.Register != original ||
                        !Enum.TryParse(original.Name, true, out receiverRegister))
                        return false;
                }
                else
                {
                    if (caller.ControlFlowGraph?.Instructions.Where(instruction =>
                            ReferenceEquals(instruction.Destination, access.Local)).ToArray() is not
                        [var origin] || origin.NativeAddress is not { } produced)
                        return false;
                    if (origin.OpCode == OpCode.Call)
                        producerCall = produced;
                    else
                    {
                        var operations = caller.ControlFlowGraph.Instructions.Where(instruction =>
                            instruction.NativeAddress == address &&
                            ReferenceEquals(instruction.Operands.ElementAtOrDefault(0), access)).ToArray();
                        return operations is [var operation] &&
                               Analysis.FieldLoadReceiverProof.HasBoundProducer(caller,
                                   access.Local, origin, operation) &&
                               X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
                                   address, store.MemoryBase, produced);
                    }
                }
                if (!X64NativeRegisterAliasProof.IsAlias(body, address,
                        store.MemoryBase, receiverRegister, producerCall))
                    return false;
            }
            var offset = checked((int)(address - caller.UnderlyingPointer));
            return offset <= caller.RawBytes.Length - store.Length &&
                   X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                       unwind, caller.RawBytes.AsSpan().Slice(offset, store.Length), address);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }
}
