using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a closed x64 tail call whose receiver and up to two arguments are
/// ordinary fields of the same instance, with an exclusive runtime null arm.
/// </summary>
internal static partial class X64GuardedFieldCallProof
{
    internal sealed record Evidence(MethodAnalysisContext Target, FieldAnalysisContext ReceiverField,
        IReadOnlyList<FieldAnalysisContext> ArgumentFields);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        if ((FindZeroArgumentInt32(method) ?? FindZeroArgumentBoolean(method) ??
             FindZeroArgumentClass(method) ?? FindZeroArgumentInt32Enum(method)) is { } zeroArgument)
            return zeroArgument;

        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || method.IsStatic ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.OverrideReturnType != null ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return null;

        for (var count = 1; count <= 2; count++)
        {
            var callIndex = 8 + count;
            if (X64Stack28BodyProof.Read(method, callIndex + 1, 64) is not { } native ||
                !FieldLoad(native[1], NativeRegister.RAX, NativeRegister.RCX, 8,
                    out var receiverOffset) || receiverOffset > 0x1000 - 8 ||
                !Test(native[2], NativeRegister.RAX) ||
                native[3].Mnemonic != Mnemonic.Je || native[3].Op0Kind != OpKind.NearBranch64 ||
                !UniqueField(owner, receiverOffset, null, out var receiverField) ||
                !NullCheckedCall.IsReferenceClass(receiverField.FieldType) ||
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
                target.UnderlyingPointer != native[7 + count].NearBranchTarget ||
                target.Parameters.Count != count || target.GenericParameters.Count != 0 ||
                target.DeclaringType is not { } targetOwner ||
                !HasUnchangedReceiverBase(receiverField.FieldType, targetOwner) ||
                !X64GuardedEnumParameterCallProof.AccessibleTarget(owner, target) ||
                !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                    owner.DeclaringAssembly, targetOwner.DeclaringAssembly) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
                RuntimeNullGuardCoalescer.HasOutputOptions(target) ||
                method.IsVoid != target.IsVoid ||
                !NullCheckedCall.SameOrdinaryType(method.ReturnType, target.ReturnType) ||
                X86CallerExceptionRegionProof.Check(method, native,
                    new HashSet<ulong> { native[callIndex].IP }) != null)
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

    private static bool HasUnchangedReceiverBase(TypeAnalysisContext receiver,
        TypeAnalysisContext owner)
    {
        // A base member uses the same reference receiver and its evidenced null
        // guard. Authenticate every original class descriptor on the path.
        var visited = new HashSet<TypeAnalysisContext>();
        for (var type = receiver; type != null && visited.Add(type); type = type.BaseType)
        {
            if (!NullCheckedCall.IsReferenceClass(type) ||
                type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
                type.Definition is not { RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or
                    Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 } })
                return false;
            if (ReferenceEquals(type, owner))
                return true;
        }
        return false;
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
