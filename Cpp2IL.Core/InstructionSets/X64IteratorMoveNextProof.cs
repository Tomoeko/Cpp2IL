using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves one complete generated iterator MoveNext state machine. In particular,
/// state becomes -1 before the captured owner's field can raise a null fault.
/// </summary>
internal static class X64IteratorMoveNextProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(FieldAnalysisContext State, FieldAnalysisContext Current,
        FieldAnalysisContext CapturedOwner, FieldAnalysisContext YieldedField);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        var state = new ISIL.Register(null, "iterator_state");
        var nonzero = new ISIL.Register(null, "iterator_nonzero");
        var one = new ISIL.Register(null, "iterator_one");
        var owner = new ISIL.Register(null, "iterator_owner");
        var value = new ISIL.Register(null, "iterator_value");
        var returnedTrue = new ISIL.Register(null, "iterator_return_true");
        var returnedFalse = new ISIL.Register(null, "iterator_return_false");
        var returnedAfterOne = new ISIL.Register(null, "iterator_return_after_one");
        var instructions = new List<ISIL.Instruction>
        {
            new(0, ISIL.OpCode.Move, state,
                new ISIL.MemoryOperand(receiver, null, proof.State.Offset)),
            new(1, ISIL.OpCode.CheckNotEqual, nonzero, state, new ISIL.Immediate(0))
                { IntegerBitWidth = 32 },
            new(2, ISIL.OpCode.ConditionalJump, new ISIL.Immediate(0), nonzero),
            new(3, ISIL.OpCode.Move, owner,
                new ISIL.MemoryOperand(receiver, null, proof.CapturedOwner.Offset)),
            new(4, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.State.Offset),
                new ISIL.Immediate(-1)),
            new(5, ISIL.OpCode.Move, value,
                new ISIL.MemoryOperand(owner, null, proof.YieldedField.Offset)),
            new(6, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.Current.Offset), value),
            new(7, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.State.Offset),
                new ISIL.Immediate(1)),
            new(8, ISIL.OpCode.Move, returnedTrue, new ISIL.Immediate(1)),
            new(9, ISIL.OpCode.Return, returnedTrue),
            new(10, ISIL.OpCode.CheckEqual, one, state, new ISIL.Immediate(1))
                { IntegerBitWidth = 32 },
            new(11, ISIL.OpCode.ConditionalJump, new ISIL.Immediate(0), one),
            new(12, ISIL.OpCode.Move, returnedFalse, new ISIL.Immediate(0)),
            new(13, ISIL.OpCode.Return, returnedFalse),
            new(14, ISIL.OpCode.Move,
                new ISIL.MemoryOperand(receiver, null, proof.State.Offset),
                new ISIL.Immediate(-1)),
            new(15, ISIL.OpCode.Move, returnedAfterOne, new ISIL.Immediate(0)),
            new(16, ISIL.OpCode.Return, returnedAfterOne),
        };
        instructions[2].SetOperand(0, instructions[10]);
        instructions[11].SetOperand(0, instructions[14]);
        return instructions;
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { DeclaringType: { } owner } iterator ||
            !EligibleMethod(method, iterator, owner, decoded))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End - region.Start != 0x59 ||
            !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
            !FileBacked(pe, region.Start, region.End))
            return null;

        var withinRegion = decoded.TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var body = withinRegion.Take(27).ToArray();
        if (body.Length != 27 || body[^1].NextIP >= region.End ||
            withinRegion.Skip(27).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
            !TryProveShape(body) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[26].IP }) != null ||
            Enumerable.Range(1, checked((int)(region.End - region.Start) - 1)).Any(offset =>
                app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)) ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[26].NearBranchTarget) == null ||
            !X64ReferenceWriteBarrierProof.TryIdentify(pe, unwind,
                body[13].NearBranchTarget))
            return null;

        var stateOffset = body[2].MemoryDisplacement64;
        var ownerOffset = body[6].MemoryDisplacement64;
        var currentOffset = body[11].GetImmediate(1);
        var yieldedOffset = body[10].MemoryDisplacement64;
        if (!DistinctOffsets(stateOffset, ownerOffset, currentOffset) ||
            !UniqueField(iterator, stateOffset, app.SystemTypes.SystemInt32Type,
                Il2CppTypeEnum.IL2CPP_TYPE_I4, FieldAttributes.Private, 32,
                out var state) ||
            !UniqueField(iterator, ownerOffset, owner, Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                FieldAttributes.Public, 64, out var capturedOwner) ||
            !UniqueField(iterator, currentOffset, app.SystemTypes.SystemObjectType,
                Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, FieldAttributes.Private, 64,
                out var current) ||
            !UniqueField(owner, yieldedOffset, app.SystemTypes.SystemObjectType,
                Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, FieldAttributes.Public, 64,
                out var yieldedField))
            return null;
        return new Evidence(state, current, capturedOwner, yieldedField);
    }

    private static bool EligibleMethod(MethodAnalysisContext method,
        TypeAnalysisContext iterator, TypeAnalysisContext owner,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        var corlib = app.SystemTypes.SystemObjectType.DeclaringAssembly;
        return method.Definition is { GenericContainer: null, parameterCount: 0,
                   RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                       NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               ReferenceEquals(definition.DeclaringType, iterator.Definition) &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               method.Name == "MoveNext" && method.Name == method.DefaultName &&
               !method.IsStatic && method.IsVirtual && method.IsFinal && !method.IsVoid &&
               method.Visibility == MethodAttributes.Private &&
               method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
               method.OverrideReturnType == null &&
               ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) &&
               method.Attributes == method.DefaultAttributes &&
               method.ImplAttributes == method.DefaultImplAttributes &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
               RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
               iterator.Definition is { GenericContainer: null, HasCctor: false,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               iterator.Name == iterator.DefaultName &&
               iterator.Namespace == iterator.DefaultNamespace &&
               iterator.Attributes == iterator.DefaultAttributes &&
               iterator.Visibility == TypeAttributes.NestedPrivate &&
               (iterator.Attributes & TypeAttributes.Sealed) != 0 &&
               ReferenceEquals(iterator.BaseType, app.SystemTypes.SystemObjectType) &&
               iterator.GenericParameters.Count == 0 && !iterator.IsGenericInstance &&
               iterator.InterfaceContexts.Any(type => type.FullName ==
                   "System.Collections.IEnumerator" &&
                   ReferenceEquals(type.DeclaringAssembly, corlib)) &&
               owner.Definition is { GenericContainer: null,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 } } &&
               owner.Name == owner.DefaultName && owner.Namespace == owner.DefaultNamespace &&
               owner.Attributes == owner.DefaultAttributes &&
               ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
               (owner.Attributes & TypeAttributes.Sealed) != 0 &&
               ReferenceEquals(owner.DeclaringAssembly, iterator.DeclaringAssembly) &&
               method.UnderlyingPointer != 0 && decoded.Count >= 27 &&
               decoded[0].IP == method.UnderlyingPointer;
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 27 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !Add(body[11], NativeRegister.RCX))
            return false;

        var stateOffset = body[2].MemoryDisplacement64;
        var ownerOffset = body[6].MemoryDisplacement64;
        var yieldedOffset = body[10].MemoryDisplacement64;
        var currentOffset = body[11].GetImmediate(1);
        return Push(body[0], NativeRegister.RBX) && Stack(body[1], Mnemonic.Sub, 0x20) &&
               Load(body[2], NativeRegister.EAX, NativeRegister.RCX, 4) &&
               Move(body[3], NativeRegister.RBX, NativeRegister.RCX) &&
               Test(body[4], NativeRegister.EAX) &&
               Branch(body[5], Code.Jne_rel8_64, body[19].IP) &&
               Load(body[6], NativeRegister.RDX, NativeRegister.RCX, 8) &&
               StoreInt32(body[7], NativeRegister.RCX, stateOffset, uint.MaxValue) &&
               Test(body[8], NativeRegister.RDX) &&
               Branch(body[9], Code.Je_rel8_64, body[26].IP) &&
               Load(body[10], NativeRegister.RDX, NativeRegister.RDX, 8) &&
               Add(body[11], NativeRegister.RCX) &&
               StoreReference(body[12], NativeRegister.RCX, NativeRegister.RDX) &&
               Call(body[13]) && ImmediateByte(body[14], NativeRegister.AL, 1) &&
               StoreInt32(body[15], NativeRegister.RBX, stateOffset, 1) &&
               Stack(body[16], Mnemonic.Add, 0x20) && Pop(body[17], NativeRegister.RBX) &&
               Return(body[18]) &&
               CompareOne(body[19], NativeRegister.EAX) &&
               Branch(body[20], Code.Jne_rel8_64, body[22].IP) &&
               StoreInt32(body[21], NativeRegister.RCX, stateOffset, uint.MaxValue) &&
               Zero(body[22], NativeRegister.AL) &&
               Stack(body[23], Mnemonic.Add, 0x20) && Pop(body[24], NativeRegister.RBX) &&
               Return(body[25]) && Call(body[26]) &&
               stateOffset == body[7].MemoryDisplacement64 &&
               stateOffset == body[15].MemoryDisplacement64 &&
               stateOffset == body[21].MemoryDisplacement64 &&
               ownerOffset is >= 16 and <= int.MaxValue &&
               yieldedOffset is >= 16 and <= int.MaxValue &&
               currentOffset is >= 16 and <= int.MaxValue &&
               DistinctOffsets(stateOffset, ownerOffset, currentOffset);
    }

    private static bool UniqueField(TypeAnalysisContext type, ulong offset,
        TypeAnalysisContext expectedType, Il2CppTypeEnum rawType,
        FieldAttributes visibility, int width, out FieldAnalysisContext field)
    {
        field = null!;
        if (offset is < 16 or > int.MaxValue || offset % (ulong)(width / 8) != 0 ||
            type.Fields.Where(candidate => !candidate.IsStatic &&
                candidate.Offset == (long)offset).ToArray() is not [{ } candidate] ||
            candidate.Name != candidate.DefaultName ||
            candidate.Offset != candidate.DefaultOffset ||
            candidate.Attributes != candidate.DefaultAttributes ||
            candidate.Visibility != visibility ||
            (candidate.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0 ||
            candidate.OverrideFieldType != null ||
            !ReferenceEquals(candidate.FieldType, expectedType) ||
            candidate.BackingData?.Field.RawFieldType is not
                { NumMods: 0, Byref: 0, Pinned: 0 } raw || raw.Type != rawType)
            return false;

        var receiver = new ISIL.LocalVariable("proved-field-owner",
            new ISIL.Register(null, "proved-field-owner"), type);
        var access = new ISIL.FieldReference(candidate, receiver, (int)offset);
        if (width == 32 ? !NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32) :
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access))
            return false;
        field = candidate;
        return true;
    }

    private static bool DistinctOffsets(ulong state, ulong owner, ulong current) =>
        state is >= 16 and <= int.MaxValue &&
        owner is >= 16 and <= int.MaxValue &&
        current is >= 16 and <= int.MaxValue &&
        state != owner && state != current && owner != current &&
        state + 4 <= current && current + 8 <= owner;

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
    }

    private static bool Push(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Push_r64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register;

    private static bool Pop(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Pop_r64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic, ulong amount) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool Load(NativeInstruction instruction, NativeRegister destination,
        NativeRegister basis, int width) =>
        instruction.Code == (width == 4 ? Code.Mov_r32_rm32 : Code.Mov_r64_rm64) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == width;

    private static bool Move(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) => instruction.Mnemonic == Mnemonic.Mov &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool StoreInt32(NativeInstruction instruction, NativeRegister basis,
        ulong offset, uint value) => instruction.Code == Code.Mov_rm32_imm32 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 4 && instruction.Op1Kind == OpKind.Immediate32 &&
        instruction.Immediate32 == value;

    private static bool Add(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Add_rm64_imm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Immediate8to64;

    private static bool StoreReference(NativeInstruction instruction, NativeRegister basis,
        NativeRegister value) => instruction.Code == Code.Mov_rm64_r64 &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == 0 &&
        instruction.MemorySize.GetSize() == 8 && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == value;

    private static bool Call(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool ImmediateByte(NativeInstruction instruction,
        NativeRegister destination, byte value) => instruction.Code == Code.Mov_r8_imm8 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == value;

    private static bool CompareOne(NativeInstruction instruction,
        NativeRegister register) => instruction.Code == Code.Cmp_rm32_imm8 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Immediate8to32 && instruction.GetImmediate(1) == 1;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Return(NativeInstruction instruction) =>
        instruction.Code == Code.Retnq && instruction.OpCount == 0;
}
