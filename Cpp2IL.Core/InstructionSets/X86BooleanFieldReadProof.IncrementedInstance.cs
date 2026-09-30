using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using IsilFieldReference = Cpp2IL.Core.ISIL.FieldReference;
using IsilLocalVariable = Cpp2IL.Core.ISIL.LocalVariable;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X86BooleanFieldReadProof
{
    internal sealed record IncrementedInstanceShape(int CounterOffset, int FieldOffset, ulong LoadIp);

    // The existing lifter preserves the preceding increment. This proof only
    // authenticates the byte load's managed type; it does not replace the body
    // with a getter or remove the original field effect.
    private static Proof? FindIncrementedInstanceRead(MethodAnalysisContext method,
        IReadOnlyList<Instruction> suppliedBody)
    {
        if (suppliedBody.Count < 3 ||
            TryProveIncrementedInstanceShape(suppliedBody.Take(3).ToArray()) is not { } candidate ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !ISIL.NullCheckedCall.IsReferenceClass(owner) ||
            owner.Name != owner.DefaultName || owner.OverrideNamespace != null ||
            owner.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.IsStatic || method.IsVoid || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemBooleanType) ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            X64NativeInstructionReader.ReadFramelessLeaf(method, 3, 32) is not { } body ||
            !body.SequenceEqual(suppliedBody.Take(3)) ||
            TryProveIncrementedInstanceShape(body) is not { } shape || shape != candidate ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return null;

        var counter = OwnFieldAt(shape.CounterOffset);
        var field = OwnFieldAt(shape.FieldOffset);
        if (counter?.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            (counter.Attributes & FieldAttributes.InitOnly) != 0 ||
            !ReferenceEquals(counter.FieldType, method.AppContext.SystemTypes.SystemInt32Type) ||
            field?.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemBooleanType))
            return null;
        var receiver = new IsilLocalVariable("proved-incremented-boolean-owner",
            new IsilRegister(null, "rcx"), owner);
        return NarrowFieldEqualityProof.HasUnchangedFieldLayout(
                   new IsilFieldReference(counter, receiver, counter.Offset), 32) &&
               NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(
                   new IsilFieldReference(field, receiver, field.Offset))
            ? new Proof(field, shape.LoadIp, Register.RCX) : null;

        FieldAnalysisContext? OwnFieldAt(int offset)
        {
            var fields = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
            return fields is [{ } field] && field.Name == field.DefaultName &&
                   ReferenceEquals(field.DeclaringType, owner) &&
                   ReferenceEquals(field.BackingData?.Field.DeclaringType, owner.Definition)
                ? field : null;
        }
    }

    internal static IncrementedInstanceShape? TryProveIncrementedInstanceShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count != 3 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            body[0].NextIP != body[1].IP || body[1].NextIP != body[2].IP ||
            body[0].Code != Code.Inc_rm32 || body[0].OpCount != 1 ||
            body[0].Op0Kind != OpKind.Memory || !OwnField(body[0], 4) ||
            body[1].Code != Code.Movzx_r32_rm8 || body[1].OpCount != 2 ||
            body[1].Op0Kind != OpKind.Register || body[1].Op0Register != Register.EAX ||
            body[1].Op1Kind != OpKind.Memory || !OwnField(body[1], 1) ||
            body[2].Code != Code.Retnq || body[2].OpCount != 0)
            return null;
        return new IncrementedInstanceShape((int)body[0].MemoryDisplacement64,
            (int)body[1].MemoryDisplacement64, body[1].IP);

        static bool OwnField(Instruction instruction, int width) =>
            instruction.MemoryBase == Register.RCX && instruction.MemoryIndex == Register.None &&
            instruction.MemoryIndexScale == 1 && instruction.MemorySize.GetSize() == width &&
            instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue;
    }
}
