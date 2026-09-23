using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Checks the small native wrappers reached by the bounded catch/divide body.
/// This does not establish the catch funclet's unmatched-exception path.
/// </summary>
internal static class X64CatchDivideHelperProof
{
    internal static bool Check(MethodAnalysisContext method, X64CatchDivideBodyProof.Evidence body)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index)
            return false;
        var helpers = app.GetOrCreateKeyFunctionAddresses();
        if (helpers.il2cpp_vm_object_new == 0 || helpers.il2cpp_vm_exception_raise == 0 ||
            Read(pe, index, body.Allocator, 1, 16) is not [var allocate] ||
            allocate.Mnemonic != Mnemonic.Jmp || allocate.Op0Kind != OpKind.NearBranch64 ||
            allocate.NearBranchTarget != helpers.il2cpp_vm_object_new ||
            Read(pe, index, body.NullGuard, 6, 64) is not [var sub, var test, var branch,
                var add, var ret, var nullExit] ||
            !Stack(sub, Mnemonic.Sub, 0x28) || !Test(test, Register.RCX) ||
            branch.Mnemonic != Mnemonic.Je || branch.Op0Kind != OpKind.NearBranch64 ||
            branch.NearBranchTarget != nullExit.IP || !Stack(add, Mnemonic.Add, 0x28) ||
            ret.Code != Code.Retnq || nullExit.Code != Code.Call_rel32_64 ||
            Read(pe, index, body.Raiser, 12, 96) is not [var save, var push, var reserve,
                var retainException, var retainFrame, var firstAddress, var firstCall,
                var secondAddress, var secondCall, var restoreFrame, var restoreException,
                var raise] ||
            !Store(save, Register.RSP, 8, Register.RBX) || !Push(push, Register.RDI) ||
            !Stack(reserve, Mnemonic.Sub, 0x20) ||
            !Move(retainException, Register.RDI, Register.RCX) ||
            !Move(retainFrame, Register.RBX, Register.RDX) ||
            !Add(firstAddress, Register.RCX, 0x38) ||
            !Call(firstCall, out var prepare) ||
            !Address(secondAddress, Register.RCX, Register.RDI, 0x40) ||
            !Call(secondCall, prepare) ||
            !Move(restoreFrame, Register.RDX, Register.RBX) ||
            !Move(restoreException, Register.RCX, Register.RDI) ||
            !Call(raise, helpers.il2cpp_vm_exception_raise))
            return false;
        return true;
    }

    private static IReadOnlyList<Instruction>? Read(PE pe, X64UnwindProof.Index index,
        ulong address, int count, int maxBytes)
    {
        if (address < index.ImageBase || address >= ulong.MaxValue - (ulong)maxBytes ||
            address - index.ImageBase > uint.MaxValue ||
            !index.IsExecutableRva((uint)(address - index.ImageBase)))
            return null;
        var start = pe.MapVirtualAddressToRaw(address, false);
        var end = pe.MapVirtualAddressToRaw(address + (ulong)maxBytes - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (start < 0 || end < start || end - start != maxBytes - 1 || end >= raw.Length)
            return null;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(raw.Slice((int)start, maxBytes).ToArray()), address);
        var result = new Instruction[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = decoder.Decode();
            if (result[i].IsInvalid || result[i].CodeSize != CodeSize.Code64 ||
                result[i].NextIP > address + (ulong)maxBytes || result[i].HasLockPrefix ||
                result[i].HasRepPrefix || result[i].HasRepnePrefix ||
                result[i].SegmentPrefix != Register.None ||
                index.ClassifySpan(address, result[i].NextIP).Kind == X64UnwindProof.SpanKind.Unsupported)
                return null;
        }
        return result;
    }

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Test(Instruction i, Register register) => i.Mnemonic == Mnemonic.Test &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Push(Instruction i, Register register) => i.Mnemonic == Mnemonic.Push &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Add(Instruction i, Register register, ulong amount) => i.Mnemonic == Mnemonic.Add &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == amount;

    private static bool Address(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;

    private static bool Call(Instruction i, ulong target) => i.Code == Code.Call_rel32_64 &&
        i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Call(Instruction i, out ulong target)
    {
        target = i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64
            ? i.NearBranchTarget : 0;
        return target != 0;
    }
}
