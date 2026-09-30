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
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates a reference transfer followed by captured scalar field effects.</summary>
internal static class X64ReferenceScalarFieldEffectsProof
{
    internal const string EvidenceKey = "X64ReferenceScalarFieldEffectsProof";
    internal sealed record Shape(int ReferenceSource, int ReferenceDestination, int Boolean,
        int IntegerSource, int IntegerDestination, int Counter, ulong Barrier);
    private sealed record Evidence(NativeInstruction[] Body, Shape Native, FieldAnalysisContext ReferenceSource,
        FieldAnalysisContext ReferenceDestination, FieldAnalysisContext Boolean, FieldAnalysisContext IntegerSource,
        FieldAnalysisContext IntegerDestination, FieldAnalysisContext Counter);

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        if (decoded.Count < 16 || TryProveShape(decoded.Take(16).ToArray()) is not { } candidate ||
            Find(method) is not { } evidence || evidence.Native != candidate ||
            !decoded.Take(16).SequenceEqual(evidence.Body))
            return null;
        method.PutExtraData(EvidenceKey, evidence);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var owner = new Register(null, "rcx");
        var reference = new Register(null, "reference_transfer_value");
        var boolean = new Register(null, "scalar_boolean_capture");
        var negated = new Register(null, "scalar_boolean_inverse");
        var integer = new Register(null, "scalar_integer_capture");
        return
        [
            new(0, OpCode.Move, reference, Memory(evidence.ReferenceSource)) { NativeAddress = evidence.Body[2].IP },
            new(1, OpCode.Move, Memory(evidence.ReferenceDestination), reference) { NativeAddress = evidence.Body[5].IP },
            new(2, OpCode.Move, boolean, Memory(evidence.Boolean))
                { IntegerBitWidth = 8, NativeAddress = evidence.Body[7].IP },
            new(3, OpCode.CheckEqual, negated, boolean, new Immediate(0))
                { IntegerBitWidth = 8, NativeAddress = evidence.Body[7].IP },
            new(4, OpCode.Move, integer, Memory(evidence.IntegerSource)) { NativeAddress = evidence.Body[8].IP },
            new(5, OpCode.Move, Memory(evidence.IntegerDestination), integer) { NativeAddress = evidence.Body[9].IP },
            new(6, OpCode.Add, Memory(evidence.Counter), Memory(evidence.Counter), new Immediate(1))
                { IntegerBitWidth = 32, NativeAddress = evidence.Body[11].IP },
            new(7, OpCode.Move, Memory(evidence.Boolean), negated) { NativeAddress = evidence.Body[12].IP },
            new(8, OpCode.Return) { NativeAddress = evidence.Body[15].IP },
        ];

        ISIL.MemoryOperand Memory(FieldAnalysisContext field) => new(owner, null, field.Offset);
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
                method.GetExtraData<Evidence>(EvidenceKey) is not { } admitted || Find(method) is not { } current ||
                admitted.Native != current.Native || !admitted.Body.SequenceEqual(current.Body) ||
                !SameFields(admitted, current) || !NativeStraightLineGraph.TryGetBody(method, out var instructions) ||
                method.ParameterLocals.Where(local => local.IsThis).ToArray() is not [var thisLocal] ||
                thisLocal.IsMethodInfo || thisLocal.Register.Version != -1 ||
                method.ParameterOperands is not [Register ownerRegister, _] ||
                thisLocal.Register.Number != ownerRegister.Number || instructions is not
                    [var referenceRead, var referenceWrite, var booleanRead, var comparison, var integerRead,
                     var integerWrite, var increment, var booleanWrite, var returned])
                return false;
            var operations = new[] { referenceRead, referenceWrite, booleanRead, comparison, integerRead,
                integerWrite, increment, booleanWrite, returned };
            var nativeIndices = new[] { 2, 5, 7, 7, 8, 9, 11, 12, 15 };
            if (operations.Where((operation, index) => operation.NativeAddress != current.Body[nativeIndices[index]].IP ||
                    operation.CallSemantics != CallSemantics.Direct || operation.IntegerBitWidth !=
                    (index is 2 or 3 ? 8 : index == 6 ? 32 : 0)).Any() ||
                !Read(referenceRead, current.ReferenceSource, out var reference) ||
                !Write(referenceWrite, current.ReferenceDestination, reference) ||
                !Read(booleanRead, current.Boolean, out var boolean) ||
                comparison is not { OpCode: OpCode.CheckEqual,
                    Operands: [LocalVariable negated, LocalVariable compared, Immediate { Value: 0 }] } ||
                !ReferenceEquals(compared, boolean) || !ReferenceEquals(negated.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
                !Read(integerRead, current.IntegerSource, out var integer) ||
                !Write(integerWrite, current.IntegerDestination, integer) ||
                increment is not { OpCode: OpCode.Add, Operands: [FieldReference destination, FieldReference source,
                    Immediate { Value: 1 }] } || !Field(destination, current.Counter) || !Field(source, current.Counter) ||
                !Write(booleanWrite, current.Boolean, negated) ||
                returned.OpCode != OpCode.Return || returned.Operands.Count != 0 ||
                new[] { reference, boolean, negated, integer }.Distinct().Count() != 4 ||
                operations.Any(operation => operation.Operands.OfType<LocalVariable>().Any(local =>
                    method.ParameterLocals.Contains(local))))
                return false;
            return true;

            bool Field(FieldReference field, FieldAnalysisContext expected) =>
                ReferenceEquals(field.Field, expected) && field.Offset == expected.Offset &&
                ReferenceEquals(field.Local, thisLocal) &&
                ReferenceEquals(field.Local.Type, method.DeclaringType);

            bool Read(Instruction operation, FieldAnalysisContext field, out LocalVariable value)
            {
                value = null!;
                if (operation is not { OpCode: OpCode.Move, Operands: [LocalVariable result, FieldReference access] } ||
                    !Field(access, field) || !NullCheckedCall.SameOrdinaryType(result.Type, field.FieldType)) return false;
                value = result;
                return true;
            }

            bool Write(Instruction operation, FieldAnalysisContext field, LocalVariable value) =>
                operation is { OpCode: OpCode.Move, Operands: [FieldReference access, LocalVariable stored] } &&
                Field(access, field) && ReferenceEquals(stored, value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool SameFields(Evidence left, Evidence right) =>
        ReferenceEquals(left.ReferenceSource, right.ReferenceSource) &&
        ReferenceEquals(left.ReferenceDestination, right.ReferenceDestination) &&
        ReferenceEquals(left.Boolean, right.Boolean) && ReferenceEquals(left.IntegerSource, right.IntegerSource) &&
        ReferenceEquals(left.IntegerDestination, right.IntegerDestination) && ReferenceEquals(left.Counter, right.Counter);

    private static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!Signature(method) || X64NativeInstructionReader.ReadRootBody(method) is not { Length: 16 } body ||
                TryProveShape(body) is not { } shape || method.AppContext.Binary is not PE pe ||
                X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
                X64NativeInvocationValues.Create(body, new HashSet<ulong>()) is not { } values ||
                !X64NativeInvocationFrameProof.IsValid(method, body, values) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind, shape.Barrier))
                return null;
            var referenceSource = Field(shape.ReferenceSource, 64, false, true);
            var referenceDestination = Field(shape.ReferenceDestination, 64, true, true);
            var boolean = Field(shape.Boolean, 8, true);
            var integerSource = Field(shape.IntegerSource, 32, false);
            var integerDestination = Field(shape.IntegerDestination, 32, true);
            var counter = Field(shape.Counter, 32, true);
            if (referenceSource == null || referenceDestination == null || boolean == null || integerSource == null ||
                integerDestination == null || counter == null ||
                !NullCheckedCall.SameOrdinaryType(referenceSource.FieldType, referenceDestination.FieldType) ||
                !ReferenceEquals(boolean.FieldType, method.AppContext.SystemTypes.SystemBooleanType) ||
                new[] { integerSource, integerDestination, counter }.Any(field =>
                    !ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemInt32Type)) ||
                new[] { referenceSource, referenceDestination, boolean, integerSource, integerDestination, counter }
                    .Distinct().Count() != 6)
                return null;
            return new(body, shape, referenceSource, referenceDestination, boolean, integerSource, integerDestination, counter);

            FieldAnalysisContext? Field(int offset, int bits, bool written, bool reference = false)
            {
                var owner = method.DeclaringType!;
                var candidates = owner.Fields.Where(field => field.Offset == offset).ToArray();
                if (candidates is not [var field] || field.Name != field.DefaultName ||
                    written && (field.Attributes & FieldAttributes.InitOnly) != 0) return null;
                var access = new FieldReference(field, new LocalVariable("proved-owner", new Register(null, "rcx"), owner), offset);
                return reference
                    ? (NullCheckedCall.IsReferenceClass(field.FieldType) || NullCheckedCall.IsBoundedArrayReference(field.FieldType)) &&
                      NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access) ? field : null
                    : NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, bits) ? field : null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool Signature(MethodAnalysisContext method) =>
        X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) &&
        method.DeclaringType is { Definition: { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner &&
        NullCheckedCall.IsReferenceClass(owner) && owner.Attributes == owner.DefaultAttributes &&
        owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
        method.Definition is { GenericContainer: null, parameterCount: 0, IsUnmanagedCallersOnly: false,
            InternalParameterData: [], RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) && method.Parameters.Count == 0 &&
        !method.IsStatic && !method.IsVirtual && method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.GenericParameters.Count == 0 && method.IsVoid &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) && !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        new X64CallingConventionResolver().ResolveForParameters(method) is [Register { Name: "rcx" }, Register { Name: "rdx" }];

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 16 || body.Any(native => native.IsInvalid || native.CodeSize != CodeSize.Code64 ||
                native.HasLockPrefix || native.HasRepPrefix || native.HasRepnePrefix || native.SegmentPrefix != NativeRegister.None) ||
            Enumerable.Range(1, body.Count - 1).Any(index => body[index - 1].NextIP != body[index].IP) ||
            body[0] is not { Code: Code.Push_r64, Op0Register: NativeRegister.RBX } || !Stack(body[1], false) ||
            body[2].Code != Code.Mov_r64_rm64 || !NativeMemory(body[2], 1, NativeRegister.RCX, 8) ||
            body[2].Op0Register != NativeRegister.RDX || !Registers(body[3], Code.Mov_r64_rm64, NativeRegister.RBX, NativeRegister.RCX) ||
            body[4] is not { Code: Code.Add_rm64_imm8 or Code.Add_rm64_imm32, Op0Kind: OpKind.Register,
                Op0Register: NativeRegister.RCX, Op1Kind: OpKind.Immediate8to64 or OpKind.Immediate32to64 } ||
            body[4].GetImmediate(1) is < 16 or > int.MaxValue - 8 ||
            body[5].Code != Code.Mov_rm64_r64 || !NativeMemory(body[5], 0, NativeRegister.RCX, 8) ||
            body[5].MemoryDisplacement64 != 0 || body[5].Op1Kind != OpKind.Register || body[5].Op1Register != NativeRegister.RDX ||
            body[6] is not { Code: Code.Call_rel32_64, Op0Kind: OpKind.NearBranch64 } || body[6].NearBranchTarget == 0 ||
            body[7].Code != Code.Cmp_rm8_imm8 || !NativeMemory(body[7], 0, NativeRegister.RBX, 1) || body[7].Immediate8 != 0 ||
            body[8].Code != Code.Mov_r32_rm32 || body[8].Op0Register != NativeRegister.EAX ||
            !NativeMemory(body[8], 1, NativeRegister.RBX, 4) || body[9].Code != Code.Mov_rm32_r32 ||
            !NativeMemory(body[9], 0, NativeRegister.RBX, 4) || body[9].Op1Kind != OpKind.Register || body[9].Op1Register != NativeRegister.EAX ||
            body[10] is not { Code: Code.Sete_rm8, Op0Kind: OpKind.Register, Op0Register: NativeRegister.AL } ||
            body[11].Code != Code.Inc_rm32 || !NativeMemory(body[11], 0, NativeRegister.RBX, 4) ||
            body[12].Code != Code.Mov_rm8_r8 || !NativeMemory(body[12], 0, NativeRegister.RBX, 1) ||
            body[12].Op1Kind != OpKind.Register || body[12].Op1Register != NativeRegister.AL ||
            body[12].MemoryDisplacement64 != body[7].MemoryDisplacement64 || !Stack(body[13], true) ||
            body[14] is not { Code: Code.Pop_r64, Op0Register: NativeRegister.RBX } ||
            body[15] is not { Code: Code.Retnq, OpCount: 0 })
            return null;
        var offsets = new[] { body[2].MemoryDisplacement64, body[4].GetImmediate(1), body[7].MemoryDisplacement64,
            body[8].MemoryDisplacement64, body[9].MemoryDisplacement64, body[11].MemoryDisplacement64 };
        return offsets.All(offset => offset is >= 16 and <= int.MaxValue - 8) && offsets.Distinct().Count() == 6
            ? new((int)offsets[0], (int)offsets[1], (int)offsets[2], (int)offsets[3], (int)offsets[4], (int)offsets[5], body[6].NearBranchTarget)
            : null;
    }

    private static bool NativeMemory(NativeInstruction native, int operand, NativeRegister owner, int width) =>
        native.GetOpKind(operand) == OpKind.Memory && native.MemoryBase == owner && native.MemoryIndex == NativeRegister.None &&
        native.MemoryIndexScale == 1 && native.MemorySize.GetSize() == width;

    private static bool Registers(NativeInstruction native, Code code, NativeRegister destination, NativeRegister source) =>
        native.Code == code && native.Op0Kind == OpKind.Register && native.Op0Register == destination &&
        native.Op1Kind == OpKind.Register && native.Op1Register == source;

    private static bool Stack(NativeInstruction native, bool restore) =>
        native.Code == (restore ? Code.Add_rm64_imm8 : Code.Sub_rm64_imm8) && native.Op0Kind == OpKind.Register &&
        native.Op0Register == NativeRegister.RSP && native.Op1Kind == OpKind.Immediate8to64 && native.GetImmediate(1) == 32;
}
