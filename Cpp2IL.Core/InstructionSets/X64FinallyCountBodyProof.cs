using System;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using Instruction = Iced.Intel.Instruction;
using Register = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a bounded signed-division/finally shape to its player metadata,
/// EH4 cleanup chain, native continuation, and rethrow path. It makes no claim
/// for other native finally shapes.
/// </summary>
internal static class X64FinallyCountBodyProof
{
    internal sealed record Evidence(TypeAnalysisContext ExceptionClass,
        MethodAnalysisContext Constructor, int Dividend);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || !method.IsStatic ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            method.Parameters.Count != 2 || method.GenericParameters.Count != 0 ||
            !ReferenceEquals(method.Parameters[0].ParameterType, app.SystemTypes.SystemInt32Type) ||
            method.Parameters[1].ParameterType is not ByRefTypeAnalysisContext byRef ||
            !ReferenceEquals(byRef.ElementType, app.SystemTypes.SystemInt32Type) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
            index.GetHandler(method.UnderlyingPointer) is not { } region ||
            region.Start != method.UnderlyingPointer ||
            X64Eh4MapProof.Parse(pe.GetRawBinaryContent(), index, region) is not
                { TryBlocks: [{ LowState: 1, HighState: 1, CatchHighState: 2,
                                Handlers: [{ } handler] }],
                  UnwindActions: [{ Kind: 1, TargetState: -1, ObjectOffset: 0x28,
                                    ActionRva: uint actionRva },
                                  { Kind: 0, TargetState: 0 },
                                  { Kind: 0, TargetState: 0 }],
                  IpStates: [{ Rva: var tryRva, State: 1 },
                             { Rva: var afterRva, State: -1 }] } ||
            handler.ContinuationRvas is not [var continuationRva] ||
            handler.FuncletRva == 0 || handler.FuncletRva > ulong.MaxValue - index.ImageBase ||
            actionRva > ulong.MaxValue - index.ImageBase ||
            !X64FinallyFuncletProof.Check(pe, index, index.ImageBase + handler.FuncletRva, 0x28) ||
            !X64FinallyCleanupActionProof.Check(pe, index, index.ImageBase + actionRva))
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var metadata = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
        if (native.Length is < 43 or > 59 || metadata == 0 ||
            native.Skip(43).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[42].NextIP, region.End) ||
            !Store(native[0], Register.RSP, 0x10, Register.RDX) ||
            !Push(native[1], Register.RBX) || !Stack(native[2], Mnemonic.Sub, 0x40) ||
            !Zero(native[3], Register.EAX) || !Store(native[4], Register.RSP, 0x50, Register.EAX) ||
            !Store(native[5], Register.RSP, 0x28, Register.RAX) ||
            !Address(native[6], Register.RAX, Register.RSP, 0x58) ||
            !Store(native[7], Register.RSP, 0x30, Register.RAX) ||
            !Test(native[8], Register.ECX) || !Branch(native[9], Mnemonic.Je, native[26].IP) ||
            !Immediate(native[10], Register.EAX, out var dividend) ||
            dividend == 0x80000000 ||
            native[11].Mnemonic != Mnemonic.Cdq || native[11].OpCount != 0 ||
            native[12].Mnemonic != Mnemonic.Idiv || native[12].Op0Kind != OpKind.Register ||
            native[12].Op0Register != Register.ECX ||
            !Store(native[13], Register.RSP, 0x50, Register.EAX) ||
            !Load(native[14], Register.RDX, Register.RSP, 0x58) ||
            !Increment(native[15], Register.RDX) ||
            !Branch(native[16], Mnemonic.Jmp, native[23].IP) ||
            !Load(native[17], Register.RAX, Register.RSP, 0x58) ||
            !Increment(native[18], Register.RAX) ||
            !Load(native[19], Register.RCX, Register.RSP, 0x28) ||
            !Test(native[20], Register.RCX) ||
            !Branch(native[21], Mnemonic.Jne, native[42].IP) ||
            !Load(native[22], Register.EAX, Register.RSP, 0x50) ||
            !Stack(native[23], Mnemonic.Add, 0x40) || !Pop(native[24], Register.RBX) ||
            native[25].Code != Code.Retnq ||
            !RipAddress(native[26], Register.RCX, out var typeSlot) ||
            !Call(native[27], metadata) || !Move(native[28], Register.RCX, Register.RAX) ||
            !Call(native[29], out var allocator) || !Move(native[30], Register.RBX, Register.RAX) ||
            !Move(native[31], Register.RCX, Register.RAX) ||
            !Call(native[32], out var nullGuard) || !Zero(native[33], Register.EDX) ||
            !Move(native[34], Register.RCX, Register.RBX) ||
            !Call(native[35], out var constructorAddress) ||
            !RipAddress(native[36], Register.RCX, out var methodSlot) ||
            !Call(native[37], metadata) || !Move(native[38], Register.RDX, Register.RAX) ||
            !Move(native[39], Register.RCX, Register.RBX) ||
            !Call(native[40], out var raiser) || native[41].Mnemonic != Mnemonic.Nop ||
            !Call(native[42], out var rethrow) ||
            !X64FinallyRethrowProof.Check(pe, index, rethrow) ||
            !X64ManagedThrowHelperProof.Check(method, allocator, nullGuard, raiser) ||
            native[17].IP < index.ImageBase || native[27].IP < index.ImageBase ||
            native[42].IP < index.ImageBase ||
            continuationRva != native[17].IP - index.ImageBase ||
            tryRva != native[27].IP - index.ImageBase ||
            afterRva != native[42].IP - index.ImageBase ||
            app.LibCpp2IlContext.GetMethodDefinitionByGlobalAddress(methodSlot) != method.Definition ||
            app.LibCpp2IlContext.GetTypeGlobalByAddress(typeSlot) is not { } rawType ||
            app.ResolveIl2CppType(rawType) is not { Definition: not null } exceptionClass ||
            !NullCheckedCall.IsReferenceClass(exceptionClass) ||
            !DerivesFrom(exceptionClass, app.SystemTypes.SystemExceptionType) ||
            !app.MethodsByAddress.TryGetValue(constructorAddress, out var constructors) ||
            constructors is not [var constructor] || constructor.Name != ".ctor" ||
            constructor.IsStatic || constructor.Parameters.Count != 0 ||
            constructor.GenericParameters.Count != 0 ||
            !ReferenceEquals(constructor.DeclaringType, exceptionClass) ||
            !ReferenceEquals(constructor.ReturnType, app.SystemTypes.SystemVoidType))
            return null;

        return new Evidence(exceptionClass, constructor, unchecked((int)dividend));
    }

    private static bool DerivesFrom(TypeAnalysisContext type, TypeAnalysisContext ancestor)
    {
        var depth = 0;
        for (TypeAnalysisContext? current = type; current != null && depth++ < 32;
             current = current.BaseType)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Load(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;

    private static bool Increment(Instruction i, Register basis) =>
        i.Mnemonic == Mnemonic.Inc && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == 0 &&
        i.MemorySize.GetSize() == 4;

    private static bool Push(Instruction i, Register register) => i.Mnemonic == Mnemonic.Push &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Pop(Instruction i, Register register) => i.Mnemonic == Mnemonic.Pop &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Test(Instruction i, Register register) => i.Mnemonic == Mnemonic.Test &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;

    private static bool Zero(Instruction i, Register register) => i.Mnemonic == Mnemonic.Xor &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong value) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == value;

    private static bool Address(Instruction i, Register destination, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset;

    private static bool RipAddress(Instruction i, Register destination, out ulong address)
    {
        address = i.IPRelativeMemoryAddress;
        return i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register &&
               i.Op0Register == destination && i.Op1Kind == OpKind.Memory &&
               i.MemoryBase == Register.RIP && i.MemoryIndex == Register.None && address != 0;
    }

    private static bool Branch(Instruction i, Mnemonic mnemonic, ulong target) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Immediate(Instruction i, Register destination, out uint value)
    {
        value = i.Immediate32;
        return i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register &&
               i.Op0Register == destination && i.Op1Kind == OpKind.Immediate32;
    }

    private static bool Call(Instruction i, ulong target) => i.Code == Code.Call_rel32_64 &&
        i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Call(Instruction i, out ulong target)
    {
        target = i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64
            ? i.NearBranchTarget : 0;
        return target != 0;
    }
}
