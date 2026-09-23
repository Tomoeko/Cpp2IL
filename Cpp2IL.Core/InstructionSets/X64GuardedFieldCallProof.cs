using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a closed x64 tail call whose receiver and up to two arguments are
/// ordinary fields of the same instance, with an exclusive runtime null arm.
/// </summary>
internal static class X64GuardedFieldCallProof
{
    internal sealed record Evidence(MethodAnalysisContext Target, FieldAnalysisContext ReceiverField,
        IReadOnlyList<FieldAnalysisContext> ArgumentFields);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || method.IsStatic ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
            method.UnderlyingPointer == 0)
            return null;

        var region = index.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.End <= region.Start ||
            region.End - region.Start is < 30 or > 64)
            return null;
        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method)
            .TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 10 or > 27 ||
            rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length ||
            native[0].IP != region.Start ||
            native[^1].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, position) => position > 0 &&
                instruction.IP != native[position - 1].NextIP).Any() ||
            !Stack(native[0], Mnemonic.Sub) ||
            !FieldLoad(native[1], NativeRegister.RAX, NativeRegister.RCX, 8,
                out var receiverOffset) ||
            !Test(native[2], NativeRegister.RAX) ||
            native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
            !UniqueField(owner, receiverOffset, null, out var receiverField) ||
            !NullCheckedCall.IsReferenceClass(receiverField.FieldType))
            return null;

        for (var count = 1; count <= 2; count++)
        {
            var callIndex = 8 + count;
            if (native.Length < callIndex + 1 ||
                native.Skip(callIndex + 1).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, native[callIndex].NextIP, region.End) ||
                native[3].NearBranchTarget != native[callIndex].IP ||
                !Stack(native[6 + count], Mnemonic.Add) ||
                !Move(native[5 + count], NativeRegister.RCX, NativeRegister.RAX) ||
                native[7 + count].Mnemonic != Mnemonic.Jmp ||
                native[7 + count].Op0Kind != OpKind.NearBranch64 ||
                native[callIndex].Code != Code.Call_rel32_64 ||
                native[callIndex].Op0Kind != OpKind.NearBranch64 ||
                X86RuntimeNullThrowProof.TryIdentify(app, native[callIndex].NearBranchTarget) == null ||
                !app.MethodsByAddress.TryGetValue(native[7 + count].NearBranchTarget,
                    out var bindings) || bindings is not [var target] ||
                target.Parameters.Count != count || target.GenericParameters.Count != 0 ||
                !ReferenceEquals(target.DeclaringType, receiverField.FieldType) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
                method.IsVoid != target.IsVoid ||
                !NullCheckedCall.SameOrdinaryType(method.ReturnType, target.ReturnType))
                continue;

            var argumentFields = new FieldAnalysisContext[count];
            var matched = new bool[count];
            var zeroCount = 0;
            foreach (var instruction in native.Skip(4).Take(count + 1))
            {
                if (Zero(instruction, count == 1 ? NativeRegister.R8D : NativeRegister.R9D))
                {
                    zeroCount++;
                    continue;
                }
                var argument = ArgumentIndex(instruction.Op0Register);
                if (argument < 0 || argument >= count || matched[argument])
                    break;
                var parameterType = target.Parameters[argument].ParameterType;
                var width = ReferenceEquals(parameterType, app.SystemTypes.SystemInt32Type) ? 4 : 8;
                if (!FieldLoad(instruction, instruction.Op0Register, NativeRegister.RCX,
                        width, out var offset) ||
                    !UniqueField(owner, offset, parameterType, out var field))
                    break;
                argumentFields[argument] = field;
                matched[argument] = true;
            }
            if (zeroCount != 1 || matched.Any(value => !value) ||
                !CallEligible(target, receiverField, argumentFields))
                continue;
            return new Evidence(target, receiverField, argumentFields);
        }
        return null;
    }

    private static bool CallEligible(MethodAnalysisContext target, FieldAnalysisContext receiver,
        IReadOnlyList<FieldAnalysisContext> arguments)
    {
        var operands = new List<IOperand> { target };
        if (!target.IsVoid)
            operands.Add(new LocalVariable("proved-result", new ManagedRegister(null, "proved-result"),
                target.ReturnType));
        operands.Add(new LocalVariable("proved-receiver", new ManagedRegister(null, "proved-receiver"),
            receiver.FieldType));
        for (var i = 0; i < arguments.Count; i++)
            operands.Add(new LocalVariable("proved-argument", new ManagedRegister(i, null),
                arguments[i].FieldType));
        operands.Add(new Immediate(0));
        return NullCheckedCall.TryGet(new ManagedInstruction(0,
            target.IsVoid ? OpCode.CallVoid : OpCode.Call, operands), out var checkedTarget, out _) &&
               ReferenceEquals(target, checkedTarget);
    }

    private static bool UniqueField(TypeAnalysisContext owner, ulong offset,
        TypeAnalysisContext? expected, out FieldAnalysisContext field)
    {
        field = null!;
        if (offset > int.MaxValue ||
            owner.Fields.Where(candidate => !candidate.IsStatic && candidate.Offset == (long)offset)
                .ToArray() is not [{ } candidate] ||
            candidate.Name != candidate.DefaultName ||
            expected != null && !NullCheckedCall.SameOrdinaryType(candidate.FieldType, expected))
            return false;
        var local = new LocalVariable("proved-owner", new ManagedRegister(null, "proved-owner"), owner);
        var access = new FieldReference(candidate, local, (int)offset);
        var valid = ReferenceEquals(candidate.FieldType, owner.AppContext.SystemTypes.SystemInt32Type)
            ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32)
            : !candidate.FieldType.IsValueType &&
              NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access);
        if (!valid)
            return false;
        field = candidate;
        return true;
    }

    private static int ArgumentIndex(NativeRegister register) => register switch
    {
        NativeRegister.RDX or NativeRegister.EDX => 0,
        NativeRegister.R8 or NativeRegister.R8D => 1,
        _ => -1,
    };

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool FieldLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister basis, int width, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == basis && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == width && offset <= int.MaxValue;
    }

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Move(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) => instruction.Mnemonic == Mnemonic.Mov &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;
}
