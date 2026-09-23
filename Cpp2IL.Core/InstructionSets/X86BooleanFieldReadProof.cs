using System;
using System.Collections.Generic;
using System.Linq;
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
/// Proves one null-guarded byte load into EAX is an ordinary Boolean field read.
/// Unproved MOVZX instructions retain their unsupported disposition.
/// </summary>
internal static class X86BooleanFieldReadProof
{
    internal const string EvidenceKey = "X86BooleanFieldReadProof";
    internal sealed record Proof(FieldAnalysisContext Field, ulong LoadIp,
        Register ReceiverRegister);
    internal sealed record Shape(long FieldOffset, ulong LoadIp, int CallIndex,
        Register ReceiverRegister);

    internal static Proof? Find(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        var app = method.AppContext;
        var shape = TryProveShape(body);
        if (shape == null || app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.Definition is not { GenericContainer: null } definition ||
            method.DeclaringType?.Definition is not { GenericContainer: null } owner ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            method.IsStatic != (shape.ReceiverRegister == Register.RCX) ||
            method.IsVoid || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 1 ||
            definition.parameterCount != 1 || definition.InternalParameterData?.Length != 1 ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var binding) ||
            binding.Count != 1 || !ReferenceEquals(binding[0], method))
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

        var box = parameter.ParameterType;
        if (box.Definition is not { GenericContainer: null } ||
            box.IsGenericInstance || box.GenericParameters.Count != 0 ||
            box.Attributes != box.DefaultAttributes ||
            !ISIL.NullCheckedCall.IsReferenceClass(box))
            return null;

        var fields = box.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset &&
            ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (fields is not [{ } matched] || matched.Name != matched.DefaultName)
            return null;
        var receiver = new IsilLocalVariable("native-receiver",
            new IsilRegister(null, shape.ReceiverRegister == Register.RCX ? "rcx" : "rdx"), box);
        var access = new IsilFieldReference(matched, receiver, (int)matched.Offset);
        if (!NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(access))
            return null;

        var call = body[shape.CallIndex];
        if (X86RuntimeNullThrowProof.TryIdentify(app, call.NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong> { call.IP }) != null)
            return null;
        return new Proof(matched, shape.LoadIp, shape.ReceiverRegister);
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count < 7)
            return null;
        for (var index = 0; index < 7; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }
        var test = body[1];
        var branch = body[2];
        var load = body[3];
        var call = body[6];
        var receiverRegister = test.Op0Register;
        if (!Stack(body[0], Mnemonic.Sub) ||
            test.Mnemonic != Mnemonic.Test || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register ||
            receiverRegister is not (Register.RCX or Register.RDX) ||
            test.Op1Register != receiverRegister ||
            branch.Mnemonic != Mnemonic.Je || branch.Op0Kind != OpKind.NearBranch64 ||
            branch.NearBranchTarget != call.IP ||
            load.Code != Code.Movzx_r32_rm8 || load.Op0Kind != OpKind.Register ||
            load.Op0Register != Register.EAX || load.Op1Kind != OpKind.Memory ||
            load.MemoryBase != receiverRegister || load.MemoryIndex != Register.None ||
            load.MemorySize.GetSize() != 1 || load.MemoryDisplacement64 > int.MaxValue ||
            !Stack(body[4], Mnemonic.Add) ||
            body[5].Code != Code.Retnq || body[5].OpCount != 0 ||
            call.Code != Code.Call_rel32_64 || call.Op0Kind != OpKind.NearBranch64 ||
            call.NearBranchTarget == 0)
            return null;
        return new Shape((long)load.MemoryDisplacement64, load.IP, 6, receiverRegister);
    }

    private static bool Stack(Instruction instruction, Mnemonic mnemonic)
        => instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
           instruction.Op0Register == Register.RSP &&
           instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           instruction.GetImmediate(1) == 0x28;
}
