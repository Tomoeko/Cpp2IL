using System;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds one x64 signed-division body and its explicit exception construction to
/// player metadata. The called native helpers and the unmatched catch path still
/// need semantic proofs before this can become managed IL.
/// </summary>
internal static class X64CatchDivideBodyProof
{
    internal sealed record Evidence(TypeAnalysisContext ExceptionClass, MethodAnalysisContext Constructor,
        int CaughtReturn, ulong Allocator, ulong NullGuard, ulong Raiser);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || !method.IsStatic ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            method.Parameters.Count != 2 || method.Parameters.Any(parameter =>
                !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type)) ||
            method.GenericParameters.Count != 0 ||
            X64CatchFuncletClassProof.Find(method) is not
            { ConstantContinuationReturn: int caughtReturn } catchProof)
            return null;

        method.EnsureRawBytes();
        if (X64UnwindProof.ForApplication(app) is not { } index ||
            index.GetHandler(method.UnderlyingPointer) is not { } region ||
            region.Start != method.UnderlyingPointer)
            return null;
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var metadataHelper = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
        if (native.Length is < 28 or > 44 || app.Binary is not PE pe ||
            native.Skip(28).Any(instruction => instruction.Code != Code.Int3) ||
            !HasInt3Padding(pe, native[27].NextIP, region.End) || metadataHelper == 0 ||
            !Push(native[0], Register.RBX) || !Stack(native[1], Mnemonic.Sub, 0x30) ||
            !Move(native[2], Register.R8D, Register.EDX) ||
            !Test(native[3], Register.EDX) || !Branch(native[4], Mnemonic.Je, native[13].IP) ||
            !Move(native[5], Register.EAX, Register.ECX) ||
            native[6].Mnemonic != Mnemonic.Cdq || native[6].OpCount != 0 ||
            native[7].Mnemonic != Mnemonic.Idiv || native[7].Op0Kind != OpKind.Register ||
            native[7].Op0Register != Register.R8D ||
            !Branch(native[8], Mnemonic.Jmp, native[10].IP) ||
            !Immediate(native[9], Register.EAX, unchecked((uint)caughtReturn)) ||
            !Stack(native[10], Mnemonic.Add, 0x30) || !Pop(native[11], Register.RBX) ||
            native[12].Code != Code.Retnq ||
            !Address(native[13], Register.RCX, catchProof.MetadataSlot) ||
            !Call(native[14], metadataHelper) || !Move(native[15], Register.RCX, Register.RAX) ||
            !Call(native[16], out var allocator) || !Move(native[17], Register.RBX, Register.RAX) ||
            !Move(native[18], Register.RCX, Register.RAX) ||
            !Call(native[19], out var nullGuard) ||
            !Zero(native[20], Register.EDX) || !Move(native[21], Register.RCX, Register.RBX) ||
            !Call(native[22], out var constructorAddress) ||
            native[23].Mnemonic != Mnemonic.Lea || native[23].Op0Kind != OpKind.Register ||
            native[23].Op0Register != Register.RCX || native[23].Op1Kind != OpKind.Memory ||
            native[23].MemoryBase != Register.RIP || native[23].MemoryIndex != Register.None ||
            !Call(native[24], metadataHelper) || !Move(native[25], Register.RDX, Register.RAX) ||
            !Move(native[26], Register.RCX, Register.RBX) || !Call(native[27], out var raiser) ||
            app.LibCpp2IlContext.GetMethodDefinitionByGlobalAddress(native[23].IPRelativeMemoryAddress) !=
            method.Definition ||
            !app.MethodsByAddress.TryGetValue(constructorAddress, out var constructors) ||
            constructors is not [var constructor] || constructor.Name != ".ctor" || constructor.IsStatic ||
            constructor.Parameters.Count != 0 || constructor.GenericParameters.Count != 0 ||
            !ReferenceEquals(constructor.DeclaringType, catchProof.CheckedClass) ||
            !ReferenceEquals(constructor.ReturnType, app.SystemTypes.SystemVoidType) ||
            allocator == 0 || nullGuard == 0 || raiser == 0)
            return null;

        // This native body starts its EH state at the potentially throwing metadata
        // initialization and leaves that state only after the terminal raise. A
        // different state map needs its own managed-region proof.
        if (X64Eh4MapProof.Parse(pe.GetRawBinaryContent(), index, region) is not
                { TryBlocks: [{ LowState: 0, HighState: 0, CatchHighState: 1 }],
                  IpStates: [{ Rva: var tryStart, State: 0 }, { Rva: var tryEnd, State: -1 }] } ||
            native[14].IP < index.ImageBase || native[27].NextIP < index.ImageBase ||
            region.End < index.ImageBase ||
            tryStart != native[14].IP - index.ImageBase ||
            tryEnd < native[27].NextIP - index.ImageBase ||
            tryEnd > region.End - index.ImageBase)
            return null;

        return new Evidence(catchProof.CheckedClass, constructor, caughtReturn, allocator, nullGuard, raiser);
    }

    private static bool HasInt3Padding(PE pe, ulong start, ulong end)
    {
        if (end < start || end - start > 16)
            return false;
        if (end == start)
            return true;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var bytes = pe.GetRawBinaryContent();
        return first >= 0 && last >= first && (ulong)(last - first) == end - start - 1 &&
               last < bytes.Length && bytes.Slice((int)first, (int)(end - start)).ToArray()
                   .All(value => value == 0xCC);
    }

    private static bool Push(Instruction i, Register register) => i.Mnemonic == Mnemonic.Push &&
        i.OpCount == 1 && i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Pop(Instruction i, Register register) => i.Mnemonic == Mnemonic.Pop &&
        i.OpCount == 1 && i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Move(Instruction i, Register target, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
        i.Op0Register == target && i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Test(Instruction i, Register register) => i.Mnemonic == Mnemonic.Test &&
        i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;

    private static bool Zero(Instruction i, Register register) => i.Mnemonic == Mnemonic.Xor &&
        i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size) =>
        i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
        i.Op0Register == Register.RSP && i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        i.GetImmediate(1) == size;

    private static bool Branch(Instruction i, Mnemonic mnemonic, ulong target) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Call(Instruction i, ulong target) =>
        i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Call(Instruction i, out ulong target)
    {
        target = i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64
            ? i.NearBranchTarget : 0;
        return target != 0;
    }

    private static bool Address(Instruction i, Register target, ulong address) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RIP &&
        i.MemoryIndex == Register.None && i.IPRelativeMemoryAddress == address;

    private static bool Immediate(Instruction i, Register target, uint value) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        i.Op1Kind == OpKind.Immediate32 && i.Immediate32 == value;
}
