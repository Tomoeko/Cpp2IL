using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Restores a reference auto-property setter when Release IL2CPP folds its
/// three-instruction field store and GC card-marker tail with other setters.
/// The field, accessor metadata, exact native bytes, and installed barrier
/// each have to agree before the native tail can become a managed stfld.
/// </summary>
internal static class X64ReferencePropertySetterProof
{
    internal sealed record Shape(int FieldOffset, ulong BarrierTarget);
    internal sealed record Evidence(FieldAnalysisContext Field);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body)
    {
        if (Find(method, body) is not { } proof)
            return null;
        return
        [
            new(0, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(new ISIL.Register(null, "rcx"), null,
                    proof.Field.Offset), new ISIL.Register(null, "rdx"))
                { NativeAddress = body[1].IP },
            new(1, ISIL.OpCode.Return) { NativeAddress = body[2].IP },
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body)
    {
        try
        {
            var shape = TryProveShape(body);
            var app = method.AppContext;
            if (shape == null || !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { } owner ||
                !X64ClassCastLookupProof.PublicOrdinaryClass(owner) ||
                owner.Definition is not { GenericContainer: null,
                    HasCctor: false } ownerDefinition ||
                method.Definition is not { GenericContainer: null,
                    parameterCount: 1,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                !ReferenceEquals(definition.DeclaringType, ownerDefinition) ||
                definition.InternalParameterData is not [var rawParameter] ||
                rawParameter.RawType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                method.Parameters is not [var parameter] ||
                !ReferenceEquals(parameter.Definition, rawParameter) ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                parameter.ParameterIndex != 0 || parameter.IsRef ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                method.IsStatic || method.IsVirtual && !method.IsFinal ||
                !method.IsVoid || method.GenericParameters.Count != 0 ||
                method.OverrideReturnType != null ||
                method.Name != method.DefaultName ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.SpecialName |
                                      MethodAttributes.Abstract |
                                      MethodAttributes.PinvokeImpl)) !=
                    MethodAttributes.SpecialName ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                          MethodImplAttributes.ManagedMask |
                                          MethodImplAttributes.InternalCall)) != 0 ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                    requireUniqueBinding: false) ||
                !ReferenceEquals(parameter.ParameterType,
                    app.ResolveIl2CppType(rawParameter.RawType)) ||
                method.UnderlyingPointer == 0 ||
                body[0].IP != method.UnderlyingPointer ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var aliases) ||
                aliases.Count(candidate => ReferenceEquals(candidate, method)) != 1 ||
                aliases.Where(candidate => ReferenceEquals(candidate.DeclaringType,
                    owner)).ToArray() is not [var ownerBinding] ||
                !ReferenceEquals(ownerBinding, method))
                return null;

            var fields = owner.Fields.Where(candidate => !candidate.IsStatic &&
                candidate.Offset == shape.FieldOffset).ToArray();
            if (fields is not [var field] ||
                !ReferenceEquals(field.FieldType, parameter.ParameterType) ||
                field.Visibility != FieldAttributes.Private ||
                field.Name != field.DefaultName ||
                field.Attributes != field.DefaultAttributes ||
                (field.Attributes & (FieldAttributes.InitOnly |
                                     FieldAttributes.Literal)) != 0 ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new ISIL.FieldReference(field,
                        new ISIL.LocalVariable("proved-owner",
                            new ISIL.Register(null, "rcx"), owner),
                        shape.FieldOffset)))
                return null;

            var properties = owner.Properties.Where(candidate =>
                candidate.Setter != null &&
                candidate.Name == candidate.DefaultName &&
                candidate.Attributes == candidate.DefaultAttributes &&
                candidate.OverridePropertyType == null &&
                candidate.Definition?.RawPropertyType is
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } &&
                ReferenceEquals(candidate.DefaultPropertyType,
                    field.FieldType)).ToArray();
            if (properties is not [var property] ||
                !ReferenceEquals(property.Setter, method) ||
                !ReferenceEquals(property.Definition?.Setter, definition) ||
                method.Name != "set_" + property.Name ||
                field.Name != "<" + property.Name + ">k__BackingField")
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            var end = body[2].NextIP;
            if ((ulong)method.RawBytes.Length != end - start ||
                unwind.ClassifySpan(start, end).Kind !=
                    X64UnwindProof.SpanKind.NoEntry ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, method.RawBytes.AsSpan(), start) ||
                !X86Utils.Iterate(method.RawBytes.AsSpan(), start, false)
                    .SequenceEqual(body) ||
                Enumerable.Range(1, checked((int)(end - start - 1)))
                    .Any(offset => app.MethodsByAddress.ContainsKey(
                        start + (ulong)offset)) ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind,
                    shape.BarrierTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong>()) != null)
                return null;
            return new Evidence(field);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 3 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            body[1].IP != body[0].NextIP ||
            body[2].IP != body[1].NextIP ||
            body[0].Code is not (Code.Add_rm64_imm8 or Code.Add_rm64_imm32) ||
            body[0].Op0Kind != OpKind.Register ||
            body[0].Op0Register != Register.RCX ||
            body[0].Op1Kind is not (OpKind.Immediate8to64 or
                                    OpKind.Immediate32to64) ||
            body[0].GetImmediate(1) is < 16 or > int.MaxValue - 8 ||
            body[1].Code != Code.Mov_rm64_r64 ||
            body[1].Op0Kind != OpKind.Memory ||
            body[1].MemoryBase != Register.RCX ||
            body[1].MemoryIndex != Register.None ||
            body[1].MemoryDisplacement64 != 0 ||
            body[1].MemorySize.GetSize() != 8 ||
            body[1].Op1Kind != OpKind.Register ||
            body[1].Op1Register != Register.RDX ||
            body[2].Code != Code.Jmp_rel32_64 ||
            body[2].Op0Kind != OpKind.NearBranch64 ||
            body[2].NearBranchTarget == 0)
            return null;
        return new Shape(checked((int)body[0].GetImmediate(1)),
            body[2].NearBranchTarget);
    }
}
