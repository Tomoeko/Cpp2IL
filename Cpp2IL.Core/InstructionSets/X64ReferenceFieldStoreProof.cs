using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Recognizes the complete null-guarded reference store whose native tail transfer
/// reaches the installed GC card marker. A managed stfld supplies both the null
/// failure and the write barrier; neither native helper is a managed call.
/// </summary>
internal static class X64ReferenceFieldStoreProof
{
    internal sealed record Evidence(FieldAnalysisContext Field);
    internal sealed record Shape(int FieldOffset, ulong BarrierTarget, ulong NullTarget);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body)
    {
        if (Find(method, body) is not { } evidence)
            return null;
        var address = new ISIL.MemoryOperand(new ISIL.Register(null, "rcx"),
            null, evidence.Field.Offset);
        return
        [
            new(0, ISIL.OpCode.Move, address, new ISIL.Register(null, "rdx")),
            new(1, ISIL.OpCode.Return),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body)
    {
        var app = method.AppContext;
        var shape = TryProveShape(body);
        if (shape == null || !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } declaringType ||
            method.DeclaringType.IsGenericInstance || method.DeclaringType.GenericParameters.Count != 0 ||
            method.DeclaringType.Attributes != method.DeclaringType.DefaultAttributes ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, declaringType.Definition) ||
            definition.InternalParameterData is not [var ownerDefinition, var valueDefinition] ||
            !method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters.Count != 2 ||
            method.GenericParameters.Count != 0 || !method.IsVoid ||
            method.OverrideReturnType != null || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var binding) ||
            binding is not [var bound] || !ReferenceEquals(bound, method))
            return null;

        var owner = method.Parameters[0];
        var value = method.Parameters[1];
        if (owner.Definition != ownerDefinition || value.Definition != valueDefinition ||
            owner.ParameterIndex != 0 || value.ParameterIndex != 1 ||
            !ReferenceEquals(owner.DeclaringMethod, method) ||
            !ReferenceEquals(value.DeclaringMethod, method) ||
            owner.IsRef || value.IsRef ||
            owner.Attributes != owner.DefaultAttributes ||
            value.Attributes != value.DefaultAttributes ||
            owner.OverrideParameterType != null || value.OverrideParameterType != null ||
            ownerDefinition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            valueDefinition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            owner.ParameterType is not { Definition: { GenericContainer: null } } box ||
            !ISIL.NullCheckedCall.IsReferenceClass(box) || box.IsGenericInstance ||
            box.GenericParameters.Count != 0 || box.Attributes != box.DefaultAttributes ||
            !ReferenceEquals(value.ParameterType, box))
            return null;

        var candidates = box.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset &&
            ReferenceEquals(field.FieldType, value.ParameterType) &&
            field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (candidates is not [{ } storedField] ||
            storedField.Name != storedField.DefaultName ||
            (storedField.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0 ||
            !ReferenceEquals(declaringType, box) &&
            (storedField.Visibility != FieldAttributes.Public ||
             box.Visibility != TypeAttributes.Public || box.DeclaringType != null) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new ISIL.FieldReference(storedField,
                    new ISIL.LocalVariable("proved-owner", new ISIL.Register(null, "rcx"), box),
                    shape.FieldOffset)))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, body[^1].NextIP);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End < body[^1].NextIP || region.End - body[^1].NextIP > 15 ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
            !unwind.MatchesUnwind(region.Start, region.End, 4, 0, new byte[] { 4, 0x42 }) ||
            X86RuntimeNullThrowProof.TryIdentify(app, shape.NullTarget) == null ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.BarrierTarget) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[^1].IP }) != null)
            return null;
        return new Evidence(storedField);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 8)
            return null;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }
        if (!Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Test_rm64_r64 || !Registers(body[1], Register.RCX, Register.RCX) ||
            body[2].Mnemonic != Mnemonic.Je || body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget != body[7].IP ||
            body[3].Code is not (Code.Add_rm64_imm8 or Code.Add_rm64_imm32) ||
            body[3].Op0Kind != OpKind.Register || body[3].Op0Register != Register.RCX ||
            body[3].Op1Kind is not (OpKind.Immediate8to64 or OpKind.Immediate32to64) ||
            body[3].GetImmediate(1) is < 16 or > int.MaxValue ||
            body[4].Code != Code.Mov_rm64_r64 || body[4].Op0Kind != OpKind.Memory ||
            body[4].MemoryBase != Register.RCX || body[4].MemoryIndex != Register.None ||
            body[4].MemoryDisplacement64 != 0 || body[4].MemorySize.GetSize() != 8 ||
            body[4].Op1Kind != OpKind.Register || body[4].Op1Register != Register.RDX ||
            !Stack(body[5], Mnemonic.Add) ||
            body[6].Code != Code.Jmp_rel32_64 || body[6].Op0Kind != OpKind.NearBranch64 ||
            body[6].NearBranchTarget == 0 ||
            body[7].Code != Code.Call_rel32_64 || body[7].Op0Kind != OpKind.NearBranch64 ||
            body[7].NearBranchTarget == 0)
            return null;
        var offset = body[3].GetImmediate(1);
        return new Shape((int)offset, body[6].NearBranchTarget, body[7].NearBranchTarget);
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool Registers(NativeInstruction instruction, Register destination, Register source) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;
}
