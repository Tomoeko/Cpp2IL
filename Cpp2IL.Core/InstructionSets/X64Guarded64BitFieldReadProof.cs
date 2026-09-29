using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilFieldReference = Cpp2IL.Core.ISIL.FieldReference;
using IsilLocalVariable = Cpp2IL.Core.ISIL.LocalVariable;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds an exact x64 null diamond to a 64-bit reference or native-sized integer field load.
/// One preceding reference-field load is allowed, but no intervening effects or alternative exits are.
/// </summary>
internal static class X64Guarded64BitFieldReadProof
{
    internal sealed record Proof(FieldAnalysisContext Field, FieldAnalysisContext? ReceiverField,
        ulong LoadIp, Register ReceiverRegister);
    internal sealed record Shape(long? ReceiverOffset, Register ReceiverRegister,
        long FieldOffset, ulong LoadIp, int CallIndex);

    internal static Proof? Find(MethodAnalysisContext method, IReadOnlyList<Instruction> body)
    {
        var app = method.AppContext;
        var shape = TryProveShape(body);
        IReadOnlyList<Instruction> provedBody = body;
        if (shape == null && TryReadPaddedRoot(method, out var paddedRoot,
                out var paddedCode))
        {
            shape = TryProveShape(paddedCode);
            provedBody = paddedRoot;
            body = paddedCode;
        }
        if (shape == null || app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.Definition is not { GenericContainer: null } definition ||
            method.DeclaringType?.Definition is not { GenericContainer: null } owner ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.IsVoid ||
            method.OverrideReturnType != null ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            !(rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_STRING &&
              ReferenceEquals(method.ReturnType, app.SystemTypes.SystemStringType) ||
              rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT &&
              ReferenceEquals(method.ReturnType, app.SystemTypes.SystemObjectType) ||
              rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
              ISIL.NullCheckedCall.IsReferenceClass(method.ReturnType) ||
              rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY &&
              IsSupportedArrayType(method.ReturnType) ||
              rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_I &&
              ReferenceEquals(method.ReturnType, app.SystemTypes.SystemIntPtrType) ||
              rawReturn.Type == Il2CppTypeEnum.IL2CPP_TYPE_U &&
              ReferenceEquals(method.ReturnType, app.SystemTypes.SystemUIntPtrType)) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 ||
            method.UnderlyingPointer == 0 || body[0].IP != method.UnderlyingPointer ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false))
            return null;

        TypeAnalysisContext box;
        FieldAnalysisContext? receiverField = null;
        if (shape.ReceiverOffset is null)
        {
            if (shape.ReceiverRegister == Register.RDX)
            {
                if (method.IsStatic || method.Parameters.Count != 1 ||
                    definition.parameterCount != 1 ||
                    definition.InternalParameterData?.Length != 1 ||
                    !HasUnchangedInstanceArgumentAbi(method) ||
                    !TryReadPaddedRoot(method, out var boundRoot, out var boundCode) ||
                    !boundCode.SequenceEqual(body))
                    return null;
                provedBody = boundRoot;
                var parameter = method.Parameters[0];
                if (parameter.Definition == null ||
                    !ReferenceEquals(parameter.Definition, definition.InternalParameterData[0]) ||
                    parameter.ParameterIndex != 0 || !ReferenceEquals(parameter.DeclaringMethod, method) ||
                    parameter.IsRef || parameter.Attributes != parameter.DefaultAttributes ||
                    parameter.OverrideParameterType != null ||
                    parameter.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 })
                    return null;
                box = parameter.ParameterType;
            }
            else if (method.IsStatic)
            {
                if (shape.ReceiverRegister != Register.RCX)
                    return null;
                if (method.Parameters.Count != 1 || definition.parameterCount != 1 ||
                    definition.InternalParameterData?.Length != 1)
                    return null;
                var parameter = method.Parameters[0];
                if (parameter.Definition == null ||
                    !ReferenceEquals(parameter.Definition, definition.InternalParameterData[0]) ||
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
                if (shape.ReceiverRegister != Register.RCX)
                    return null;
                if (method.Parameters.Count != 0 || definition.parameterCount != 0 ||
                    (definition.InternalParameterData?.Length ?? 0) != 0 ||
                    !ISIL.NullCheckedCall.IsReferenceClass(method.DeclaringType))
                    return null;
                box = method.DeclaringType;
            }
        }
        else
        {
            if (shape.ReceiverRegister != Register.RAX || method.IsStatic ||
                method.Parameters.Count != 0 || definition.parameterCount != 0 ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                !ISIL.NullCheckedCall.IsReferenceClass(method.DeclaringType))
                return null;
            var parents = method.DeclaringType.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.ReceiverOffset &&
                field.BackingData?.Field.RawFieldType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
            if (parents is not [{ } matched] || matched.Name != matched.DefaultName)
                return null;
            receiverField = matched;
            var ownerLocal = new IsilLocalVariable("native-receiver", new IsilRegister(null, "rcx"),
                method.DeclaringType);
            var parentAccess = new IsilFieldReference(receiverField, ownerLocal, (int)receiverField.Offset);
            if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(parentAccess))
                return null;
            box = receiverField.FieldType;
        }

        if (box.Definition is not { GenericContainer: null } ||
            box.IsGenericInstance || box.GenericParameters.Count != 0 ||
            box.Attributes != box.DefaultAttributes ||
            !ISIL.NullCheckedCall.IsReferenceClass(box))
            return null;

        var fields = box.Fields.Where(field => !field.IsStatic &&
            field.Offset == shape.FieldOffset &&
            ISIL.NullCheckedCall.SameOrdinaryType(field.FieldType, method.ReturnType) &&
            field.BackingData?.Field.RawFieldType is { NumMods: 0, Byref: 0, Pinned: 0 } rawField &&
            rawField.Type == rawReturn.Type).ToArray();
        if (fields is not [{ } referenceField] || referenceField.Name != referenceField.DefaultName)
            return null;
        var receiver = new IsilLocalVariable("native-receiver", new IsilRegister(null,
            shape.ReceiverOffset is null ? shape.ReceiverRegister.ToString().ToLowerInvariant() : "rax"), box);
        var access = new IsilFieldReference(referenceField, receiver, (int)referenceField.Offset);
        if (!(IsSupportedNativeIntType(method.ReturnType)
                ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 64)
                : NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access)))
            return null;

        var call = body[shape.CallIndex];
        if (X86RuntimeNullThrowProof.TryIdentify(app, call.NearBranchTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, provedBody, new HashSet<ulong> { call.IP }) != null)
            return null;
        return new Proof(referenceField, receiverField, shape.LoadIp,
            shape.ReceiverRegister);
    }

    internal static bool HasUnchangedInstanceArgumentAbi(MethodAnalysisContext method)
    {
        if (method.IsStatic || method.Parameters.Count != 1)
            return false;
        var resolver = new X64CallingConventionResolver();
        if (resolver.ReturnsViaHiddenBuffer(method))
            return false;
        var incoming = resolver.ResolveForParameters(method);
        return incoming.Length == 3 && method.ParameterOperands.Count == incoming.Length &&
               incoming[0] is IsilRegister { Name: "rcx" } first &&
               incoming[1] is IsilRegister { Name: "rdx" } second &&
               incoming[2] is IsilRegister { Name: "r8" } third &&
               method.ParameterOperands[0] is IsilRegister currentFirst && currentFirst == first &&
               method.ParameterOperands[1] is IsilRegister currentSecond && currentSecond == second &&
               method.ParameterOperands[2] is IsilRegister currentThird && currentThird == third;
    }

    internal static bool HasBoundInstanceArgument(MethodAnalysisContext method,
        IsilLocalVariable receiver) =>
        HasUnchangedInstanceArgumentAbi(method) && !receiver.IsThis &&
        !receiver.IsMethodInfo && method.ParameterLocals.Contains(receiver) &&
        receiver.Register == (IsilRegister)method.ParameterOperands[1] &&
        LocalVariables.GetIncomingParameterIndex(method, receiver) == 0 &&
        !method.ControlFlowGraph!.Instructions.Any(instruction =>
            ReferenceEquals(instruction.Destination, receiver));

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count is not (7 or 8) || !Stack(body[0], Mnemonic.Sub))
            return null;
        var nested = body.Count == 8;
        for (var index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                index > 0 && instruction.IP != body[index - 1].NextIP)
                return null;
        }

        long? receiverOffset = null;
        if (nested)
        {
            if (!FieldLoad(body[1], Register.RCX))
                return null;
            receiverOffset = (long)body[1].MemoryDisplacement64;
        }
        var tested = nested ? Register.RAX : body[1].Op0Register;
        if (!nested && tested is not (Register.RCX or Register.RDX))
            return null;
        var test = body[nested ? 2 : 1];
        var branch = body[nested ? 3 : 2];
        var load = body[nested ? 4 : 3];
        var call = body[^1];
        if (test.Mnemonic != Mnemonic.Test || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register || test.Op0Register != tested ||
            test.Op1Register != tested ||
            branch.Mnemonic != Mnemonic.Je || branch.Op0Kind != OpKind.NearBranch64 ||
            branch.NearBranchTarget != call.IP ||
            !FieldLoad(load, tested) ||
            !Stack(body[^3], Mnemonic.Add) ||
            body[^2].Code != Code.Retnq || body[^2].OpCount != 0 ||
            call.Code != Code.Call_rel32_64 || call.Op0Kind != OpKind.NearBranch64 ||
            call.NearBranchTarget == 0)
            return null;
        return new Shape(receiverOffset, tested, (long)load.MemoryDisplacement64,
            load.IP, body.Count - 1);
    }

    private static bool TryReadPaddedRoot(MethodAnalysisContext method,
        out Instruction[] root, out Instruction[] code)
    {
        root = [];
        code = [];
        if (method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            X64NativeInstructionReader.ReadRootBody(method) is not { Length: 8 } read ||
            read[^1].Code != Code.Int3 || read[^1].Length != 1 ||
            !unwind.MatchesUnwind(method.UnderlyingPointer, read[^1].NextIP,
                4, 0, new byte[] { 4, 0x42 }) ||
            !X64NativePaddingProof.HasInt3Padding(pe, read[^1].IP, read[^1].NextIP))
            return false;
        root = read;
        code = read.Take(read.Length - 1).ToArray();
        return true;
    }

    private static bool FieldLoad(Instruction instruction, Register receiver) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RAX && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == receiver && instruction.MemoryIndex == Register.None &&
        instruction.MemorySize.GetSize() == 8 && instruction.MemoryDisplacement64 <= int.MaxValue;

    internal static bool IsSupportedArrayType(TypeAnalysisContext type) =>
        type is SzArrayTypeAnalysisContext array &&
        (ReferenceEquals(array.ElementType, type.AppContext.SystemTypes.SystemInt32Type) ||
         ReferenceEquals(array.ElementType, type.AppContext.SystemTypes.SystemStringType) ||
         ReferenceEquals(array.ElementType, type.AppContext.SystemTypes.SystemObjectType));

    internal static bool IsSupportedNativeIntType(TypeAnalysisContext type) =>
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemIntPtrType) ||
        ReferenceEquals(type, type.AppContext.SystemTypes.SystemUIntPtrType);

    private static bool Stack(Instruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == Register.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;
}
