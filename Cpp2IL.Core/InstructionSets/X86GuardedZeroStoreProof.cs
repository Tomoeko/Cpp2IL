using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilFieldReference = Cpp2IL.Core.ISIL.FieldReference;
using IsilLocalVariable = Cpp2IL.Core.ISIL.LocalVariable;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a closed target-runtime null diamond to an ordinary Boolean zero store, or to
/// one reference-field load followed by a signed 32-bit zero store. The guard remains
/// in ISIL until the corresponding managed field access can retain its null failure.
/// </summary>
internal static class X86GuardedZeroStoreProof
{
    internal const string EvidenceKey = "X86GuardedZeroStoreProof";
    internal sealed record Proof(FieldAnalysisContext Field, FieldAnalysisContext? ReceiverField);
    internal sealed record Shape(long? ReceiverOffset, long StoreOffset, int StoreWidth, int CallIndex);

    public static Proof? Find(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        var app = method.AppContext;
        var shape = TryProveShape(body);
        if (shape == null || app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.Definition is not { GenericContainer: null } definition ||
            method.DeclaringType?.Definition is not { GenericContainer: null } owner ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            !method.IsVoid || method.OverrideReturnType != null ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var binding) ||
            binding.Count != 1 || !ReferenceEquals(binding[0], method))
            return null;

        TypeAnalysisContext box;
        FieldAnalysisContext? receiverField = null;
        if (shape.ReceiverOffset is null)
        {
            if (!method.IsStatic || method.Parameters.Count != 1 ||
                definition.parameterCount != 1 || definition.InternalParameterData?.Length != 1)
                return null;
            var parameter = method.Parameters[0];
            if (parameter.Definition == null ||
                parameter.Definition != definition.InternalParameterData[0] ||
                parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
                parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                parameter.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 })
                return null;
            box = parameter.ParameterType;
        }
        else
        {
            if (method.IsStatic || method.Parameters.Count != 0 || definition.parameterCount != 0 ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                method.DeclaringType.IsGenericInstance ||
                method.DeclaringType.GenericParameters.Count != 0 ||
                method.DeclaringType.Attributes != method.DeclaringType.DefaultAttributes ||
                (method.DeclaringType.Attributes & TypeAttributes.Sealed) == 0)
                return null;
            var parents = method.DeclaringType.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.ReceiverOffset &&
                field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
            if (parents is not [{ } matched])
                return null;
            receiverField = matched;
            var ownerLocal = new IsilLocalVariable("native-receiver", new IsilRegister(null, "rcx"),
                method.DeclaringType);
            var parentAccess = new IsilFieldReference(receiverField, ownerLocal, (int)receiverField.Offset);
            if (receiverField.Name != receiverField.DefaultName ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(parentAccess))
                return null;
            box = receiverField.FieldType;
        }

        if (box.Definition is not { GenericContainer: null } ||
            box.IsGenericInstance || box.GenericParameters.Count != 0 ||
            box.Attributes != box.DefaultAttributes ||
            (box.Attributes & TypeAttributes.Sealed) == 0 ||
            !ISIL.NullCheckedCall.IsReferenceClass(box))
            return null;

        var expected = shape.StoreWidth == 1 ? app.SystemTypes.SystemBooleanType :
            app.SystemTypes.SystemInt32Type;
        var rawType = shape.StoreWidth == 1 ? Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN :
            Il2CppTypeEnum.IL2CPP_TYPE_I4;
        var fields = box.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.StoreOffset && ReferenceEquals(field.FieldType, expected) &&
            field.BackingData?.Field.RawFieldType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
            raw.Type == rawType).ToArray();
        var call = body[shape.CallIndex];
        if (fields is not [{ } storeField] ||
            X86RuntimeNullThrowProof.TryIdentify(app, call.NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { call.IP }) != null)
            return null;
        return new Proof(storeField, receiverField);
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count < 7 || !Stack(body[0], Mnemonic.Sub))
            return null;
        var nested = body[1].Mnemonic == Mnemonic.Mov;
        var count = nested ? 8 : 7;
        if (body.Count < count)
            return null;
        for (var index = 0; index < count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                (index > 0 && instruction.IP != body[index - 1].NextIP))
                return null;
        }

        long? receiverOffset = null;
        var tested = nested ? Register.RAX : Register.RCX;
        if (nested)
        {
            var load = body[1];
            if (load.Code != Code.Mov_r64_rm64 || load.Op0Kind != OpKind.Register ||
                load.Op0Register != Register.RAX || load.Op1Kind != OpKind.Memory ||
                load.MemoryBase != Register.RCX || load.MemoryIndex != Register.None ||
                load.MemorySize.GetSize() != 8 || load.MemoryDisplacement64 > int.MaxValue)
                return null;
            receiverOffset = (long)load.MemoryDisplacement64;
        }

        var testIndex = nested ? 2 : 1;
        var branchIndex = testIndex + 1;
        var storeIndex = testIndex + 2;
        var callIndex = count - 1;
        var test = body[testIndex];
        var branch = body[branchIndex];
        var store = body[storeIndex];
        var width = nested ? 4 : 1;
        if (test.Mnemonic != Mnemonic.Test || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register || test.Op0Register != tested ||
            test.Op1Register != tested ||
            branch.Mnemonic != Mnemonic.Je || branch.Op0Kind != OpKind.NearBranch64 ||
            branch.NearBranchTarget != body[callIndex].IP ||
            store.Code != (nested ? Code.Mov_rm32_imm32 : Code.Mov_rm8_imm8) ||
            store.Op0Kind != OpKind.Memory || store.MemoryBase != tested ||
            store.MemoryIndex != Register.None || store.MemorySize.GetSize() != width ||
            (nested ? store.Op1Kind != OpKind.Immediate32 || store.Immediate32 != 0 :
                store.Op1Kind != OpKind.Immediate8 || store.Immediate8 != 0) ||
            store.MemoryDisplacement64 > int.MaxValue ||
            !Stack(body[storeIndex + 1], Mnemonic.Add) ||
            body[storeIndex + 2].Code != Code.Retnq || body[storeIndex + 2].OpCount != 0 ||
            body[callIndex].Code != Code.Call_rel32_64 ||
            body[callIndex].Op0Kind != OpKind.NearBranch64 ||
            body[callIndex].NearBranchTarget == 0)
            return null;
        return new Shape(receiverOffset, (long)store.MemoryDisplacement64, width, callIndex);
    }

    private static bool Stack(Instruction instruction, Mnemonic mnemonic)
        => instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
           instruction.Op0Register == Register.RSP &&
           instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           instruction.GetImmediate(1) == 0x28;
}
