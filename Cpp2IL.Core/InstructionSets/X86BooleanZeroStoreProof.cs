using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds an exact target-runtime null diamond and byte-sized zero store to one Boolean
/// instance field. The guard stays in ISIL until the field store can replace its throw arm.
/// </summary>
internal static class X86BooleanZeroStoreProof
{
    internal const string EvidenceKey = "X86BooleanZeroStoreProof";
    internal sealed record Proof(FieldAnalysisContext Field);

    public static Proof? Find(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        var app = method.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.Definition is not { GenericContainer: null } definition ||
            method.DeclaringType?.Definition is not { GenericContainer: null } owner ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            !method.IsStatic || !method.IsVoid || method.Parameters.Count != 1 ||
            definition.parameterCount != 1 || definition.InternalParameterData?.Length != 1 ||
            method.UnderlyingPointer == 0 ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var binding) ||
            binding.Count != 1 || !ReferenceEquals(binding[0], method) ||
            method.OverrideReturnType != null ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 || body.Count < 7 ||
            body[0].IP != method.UnderlyingPointer)
            return null;

        var parameter = method.Parameters[0];
        var box = parameter.ParameterType;
        if (parameter.Definition == null ||
            parameter.Definition != definition.InternalParameterData[0] ||
            parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            parameter.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            box.Definition is not { GenericContainer: null } ||
            box.IsGenericInstance || box.GenericParameters.Count != 0 ||
            box.Attributes != box.DefaultAttributes ||
            (box.Attributes & TypeAttributes.Sealed) == 0 ||
            !ISIL.NullCheckedCall.IsReferenceClass(box) ||
            !TryProveShape(body, out var offset))
            return null;

        var fields = box.Fields.Where(field => !field.IsStatic && field.Offset == offset &&
            ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (fields is not [{ } matched] ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[6].NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { body[6].IP }) != null)
            return null;
        return new Proof(matched);
    }

    internal static bool TryProveShape(IReadOnlyList<Instruction> body, out long offset)
    {
        offset = -1;
        if (body.Count < 7)
            return false;
        for (var index = 0; index < 7; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                (index > 0 && instruction.IP != body[index - 1].NextIP))
                return false;
        }
        var store = body[3];
        if (!Stack(body[0], Mnemonic.Sub) ||
            body[1].Mnemonic != Mnemonic.Test || body[1].Op0Kind != OpKind.Register ||
            body[1].Op1Kind != OpKind.Register ||
            body[1].Op0Register != Register.RCX || body[1].Op1Register != Register.RCX ||
            body[2].Mnemonic != Mnemonic.Je || body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[6].IP ||
            store.Code != Code.Mov_rm8_imm8 || store.Op0Kind != OpKind.Memory ||
            store.MemoryBase != Register.RCX || store.MemoryIndex != Register.None ||
            store.MemorySize.GetSize() != 1 || store.Op1Kind != OpKind.Immediate8 ||
            store.Immediate8 != 0 || store.MemoryDisplacement64 > int.MaxValue ||
            !Stack(body[4], Mnemonic.Add) ||
            body[5].Code != Code.Retnq || body[5].OpCount != 0 ||
            body[6].Code != Code.Call_rel32_64 || body[6].Op0Kind != OpKind.NearBranch64 ||
            body[6].NearBranchTarget == 0)
            return false;
        offset = (long)store.MemoryDisplacement64;
        return true;
    }

    private static bool Stack(Instruction instruction, Mnemonic mnemonic)
        => instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
           instruction.Op0Register == Register.RSP &&
           instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           instruction.GetImmediate(1) == 0x28;
}
