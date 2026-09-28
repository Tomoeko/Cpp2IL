using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete three-null-arm body that reads a string from the first
/// getter result, tests a Boolean from a nested second result, and tail-calls
/// the three-string Concat overload with two metadata-backed choices.
/// </summary>
internal static class X64TripleLiteralGuardProof
{
    private static readonly byte[] SavedRbxRdiFrame =
        [0x44, 0x74, 0x06, 0x00, 0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(MethodAnalysisContext Getter,
        MethodAnalysisContext NestedGetter, FieldAnalysisContext TextField,
        FieldAnalysisContext FlagField, MethodAnalysisContext Concat,
        string FalseLiteral, string TrueLiteral, string Suffix);

    internal sealed record Shape(ulong FirstCall, ulong SecondCall,
        ulong NestedCall, ulong ConcatCall, ulong NullCall, ulong NullHelper,
        ulong MetadataHelper, ulong MetadataFlag, ulong[] InitializedSlots,
        ulong FalseSlot, ulong TrueSlot, ulong SuffixSlot,
        int TextOffset, int FlagOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryCaller(method) ||
                ReadBody(method, pe, unwind) is not { } body ||
                TryProveShape(body) is not { } shape ||
                !MetadataAndExceptionClosure(method, pe, unwind, body, shape,
                    out var literals))
                return null;

            var owner = method.DeclaringType!;
            if (shape.FirstCall != shape.SecondCall ||
                !BindGetter(app, shape.FirstCall, owner, out var getter) ||
                getter.ReturnType is not { } node ||
                !X64LiteralConcatProof.ProveInheritedStringField(node,
                    (ulong)shape.TextOffset, out var textField) ||
                !BindGetter(app, shape.NestedCall, node, out var nestedGetter) ||
                nestedGetter.ReturnType is not { } choice ||
                !BindBooleanField(choice, shape.FlagOffset, out var flagField) ||
                !app.MethodsByAddress.TryGetValue(shape.ConcatCall,
                    out var concatBindings) ||
                concatBindings is not [{ } concat] ||
                concat.UnderlyingPointer != shape.ConcatCall ||
                !X64LiteralConcatProof.ProveConcat(concat, app, 3))
                return null;

            return new Evidence(getter, nestedGetter, textField, flagField,
                concat, literals[shape.FalseSlot], literals[shape.TrueSlot],
                literals[shape.SuffixSlot]);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or
                                          OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryCaller(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !OrdinaryClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.IsStatic || !method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemStringType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                                  MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                out var bindings) ||
            bindings is not [{ } unique] || !ReferenceEquals(unique, method))
            return false;
        return true;
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) &&
        type.Definition is { GenericContainer: null,
            PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        type.Name == type.DefaultName &&
        type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        type.GenericParameters.Count == 0 && !type.IsGenericInstance &&
        ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        (type.Attributes & TypeAttributes.LayoutMask) !=
            TypeAttributes.ExplicitLayout;

    private static bool BindGetter(ApplicationAnalysisContext app, ulong address,
        TypeAnalysisContext receiver, out MethodAnalysisContext getter)
    {
        getter = null!;
        if (!OrdinaryClass(receiver) || address == 0 ||
            !app.MethodsByAddress.TryGetValue(address, out var bindings) ||
            bindings.Count == 0)
            return false;
        var candidates = bindings.Where(candidate =>
            candidate.UnderlyingPointer == address &&
            OrdinaryGetter(candidate, app) &&
            CallResultNullGuardProof.HasUnambiguousTarget(candidate,
                receiver)).ToArray();
        if (candidates is not [{ } selected])
            return false;
        getter = selected;
        return true;
    }

    private static bool OrdinaryGetter(MethodAnalysisContext method,
        ApplicationAnalysisContext app) =>
        ReferenceEquals(method.AppContext, app) &&
        method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        method.Definition is { GenericContainer: null, parameterCount: 0,
            RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
        ReferenceEquals(definition.DeclaringType, owner.Definition) &&
        (definition.InternalParameterData?.Length ?? 0) == 0 &&
        !method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName &&
        method.Parameters.Count == 0 && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        method.ReturnType is { } result && OrdinaryClass(result) &&
        ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract |
                              MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall)) == 0 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
            requireUniqueBinding: false);

    private static bool BindBooleanField(TypeAnalysisContext receiver,
        int offset, out FieldAnalysisContext field)
    {
        field = null!;
        if (!OrdinaryClass(receiver) || offset < 16)
            return false;
        var matches = new List<FieldAnalysisContext>();
        var visited = new HashSet<TypeAnalysisContext>();
        var reachedObject = false;
        for (var type = receiver; type != null; type = type.BaseType)
        {
            if (!visited.Add(type))
                return false;
            if (ReferenceEquals(type,
                receiver.AppContext.SystemTypes.SystemObjectType))
            {
                reachedObject = true;
                break;
            }
            if (!OrdinaryClass(type))
                return false;
            matches.AddRange(type.Fields.Where(candidate =>
                !candidate.IsStatic && candidate.Offset == offset));
        }
        if (!reachedObject || matches is not [{ } selected] ||
            selected.Visibility != FieldAttributes.Public ||
            selected.Name != selected.DefaultName ||
            selected.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(selected.FieldType,
                receiver.AppContext.SystemTypes.SystemBooleanType) ||
            !NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(
                new FieldReference(selected,
                    new LocalVariable("proved-choice", new ManagedRegister(null,
                        "proved-choice"), receiver), offset)))
            return false;
        field = selected;
        return true;
    }

    private static NativeInstruction[]? ReadBody(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind)
    {
        var start = method.UnderlyingPointer;
        if (start > ulong.MaxValue - 171)
            return null;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != start || region.RootStart != start ||
            region.End != start + 171 ||
            !unwind.MatchesUnwind(start, region.End, 68, 0,
                SavedRbxRdiFrame) ||
            method.AppContext.MethodsByAddress.Keys.Any(address =>
                address > start && address < region.End))
            return null;

        method.EnsureRawBytes();
        if (method.RawBytes.Length != 170 ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                unwind, method.RawBytes.AsSpan(), start) ||
            !X64NativePaddingProof.HasInt3Padding(pe, start + 170,
                region.End))
            return null;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - 171 ||
            Enumerable.Range(0, 171).Any(offset =>
                pe.MapVirtualAddressToRaw(start + (ulong)offset,
                    false) != first + offset ||
                !unwind.IsExecutableRva(checked((uint)(start +
                    (ulong)offset - unwind.ImageBase)))))
            return null;
        var body = X86Utils.Iterate(method).ToArray();
        return body.Length == 40 && body[0].IP == start &&
               body[^1].NextIP == start + 170 ? body : null;
    }

    private static bool MetadataAndExceptionClosure(MethodAnalysisContext method,
        PE pe, X64UnwindProof.Index unwind,
        IReadOnlyList<NativeInstruction> body, Shape shape,
        out Dictionary<ulong, string> literals)
    {
        literals = new Dictionary<ulong, string>();
        var app = method.AppContext;
        if (!WritableZero(unwind, shape.MetadataFlag) ||
            !X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(
                app, pe, unwind, shape.MetadataHelper) ||
            X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { shape.NullCall }) != null ||
            shape.InitializedSlots.Distinct().Count() != 3 ||
            !shape.InitializedSlots.Contains(shape.FalseSlot) ||
            !shape.InitializedSlots.Contains(shape.TrueSlot) ||
            !shape.InitializedSlots.Contains(shape.SuffixSlot) ||
            shape.FalseSlot == shape.TrueSlot ||
            shape.FalseSlot == shape.SuffixSlot ||
            shape.TrueSlot == shape.SuffixSlot)
            return false;

        foreach (var slot in shape.InitializedSlots)
        {
            if (!WritablePointer(pe, unwind, slot) ||
                slot <= shape.MetadataFlag &&
                shape.MetadataFlag - slot < 8 ||
                app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot) is not
                    { Type: MetadataUsageType.StringLiteral,
                        IsValid: true } usage ||
                app.LibCpp2IlContext.GetLiteralByAddress(slot) is not
                    { } literal || literal != usage.AsLiteral())
                return false;
            literals.Add(slot, literal);
        }
        return true;
    }

    private static bool WritableZero(X64UnwindProof.Index unwind,
        ulong address) =>
        address >= unwind.ImageBase &&
        address - unwind.ImageBase <= uint.MaxValue &&
        unwind.IsWritableZeroInitializedRva(
            (uint)(address - unwind.ImageBase));

    private static bool WritablePointer(PE pe,
        X64UnwindProof.Index unwind, ulong address)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - 8 ||
            address + 7 - unwind.ImageBase > uint.MaxValue)
            return false;
        var raw = pe.MapVirtualAddressToRaw(address, false);
        var image = pe.GetRawBinaryContent();
        if (raw < 0 || raw > image.Length - 8)
            return false;
        return Enumerable.Range(0, 8).All(offset =>
            pe.MapVirtualAddressToRaw(address + (ulong)offset,
                false) == raw + offset &&
            unwind.IsWritableFileBackedRva((uint)(address +
                (ulong)offset - unwind.ImageBase)));
    }

    // Decoder-only shape test; Find also binds PE bytes, unwind, metadata,
    // call targets, field layouts, helpers, and exception regions.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 40 || body[0].IP > ulong.MaxValue - 170 ||
            body[^1].NextIP != body[0].IP + 170 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        if (!Push(body[0], NativeRegister.RBX) ||
            !Stack(body[1], Mnemonic.Sub, 0x20) ||
            !RipCompareZero(body[2]) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[4], Code.Jne_rel8_64, body[12].IP) ||
            !RipLea(body[5], NativeRegister.RCX) ||
            !DirectCall(body[6]) ||
            !RipLea(body[7], NativeRegister.RCX) ||
            !DirectCall(body[8]) ||
            !RipLea(body[9], NativeRegister.RCX) ||
            !DirectCall(body[10]) ||
            body[6].NearBranchTarget != body[8].NearBranchTarget ||
            body[8].NearBranchTarget != body[10].NearBranchTarget ||
            !RipStoreOne(body[11], body[2].IPRelativeMemoryAddress) ||
            !Zero(body[12], NativeRegister.EDX) ||
            !StackStore(body[13], 0x30, NativeRegister.RDI) ||
            !Move(body[14], NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectCall(body[15]) ||
            !Test(body[16], NativeRegister.RAX) ||
            !Branch(body[17], Code.Je_rel8_64, body[39].IP) ||
            !ObjectLoad(body[18], NativeRegister.RDI,
                NativeRegister.RAX, 8, out var textOffset) ||
            !Zero(body[19], NativeRegister.EDX) ||
            !Move(body[20], NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectCall(body[21]) ||
            !Test(body[22], NativeRegister.RAX) ||
            !Branch(body[23], Code.Je_rel8_64, body[39].IP) ||
            !Zero(body[24], NativeRegister.EDX) ||
            !Move(body[25], NativeRegister.RCX, NativeRegister.RAX) ||
            !DirectCall(body[26]) ||
            !Test(body[27], NativeRegister.RAX) ||
            !Branch(body[28], Code.Je_rel8_64, body[39].IP) ||
            !ObjectCompareZero(body[29], NativeRegister.RAX,
                out var flagOffset) ||
            !RipLoad(body[30], NativeRegister.RDX) ||
            !RipConditionalLoad(body[31], NativeRegister.RDX) ||
            !RipLoad(body[32], NativeRegister.R8) ||
            !Zero(body[33], NativeRegister.R9D) ||
            !Move(body[34], NativeRegister.RCX, NativeRegister.RDI) ||
            !StackLoad(body[35], NativeRegister.RDI, 0x30) ||
            !Stack(body[36], Mnemonic.Add, 0x20) ||
            !Pop(body[37], NativeRegister.RBX) ||
            !DirectJump(body[38]) || !DirectCall(body[39]) ||
            textOffset < 16 || textOffset >= 128 ||
            flagOffset < 128 || flagOffset > 0x1000 ||
            body[15].NearBranchTarget != body[21].NearBranchTarget ||
            body[15].NearBranchTarget == body[26].NearBranchTarget)
            return null;

        return new Shape(body[15].NearBranchTarget,
            body[21].NearBranchTarget, body[26].NearBranchTarget,
            body[38].NearBranchTarget, body[39].IP,
            body[39].NearBranchTarget, body[6].NearBranchTarget,
            body[2].IPRelativeMemoryAddress,
            [body[5].IPRelativeMemoryAddress,
             body[7].IPRelativeMemoryAddress,
             body[9].IPRelativeMemoryAddress],
            body[30].IPRelativeMemoryAddress,
            body[31].IPRelativeMemoryAddress,
            body[32].IPRelativeMemoryAddress,
            textOffset, flagOffset);
    }

    private static bool Register(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == register;

    private static bool Push(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Code == Code.Push_r64 && Register(instruction, register);

    private static bool Pop(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Code == Code.Pop_r64 && Register(instruction, register);

    private static bool Stack(NativeInstruction instruction,
        Mnemonic mnemonic, ulong amount) =>
        instruction.Mnemonic == mnemonic &&
        Register(instruction, NativeRegister.RSP) &&
        instruction.Op1Kind is OpKind.Immediate8to64 or
            OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool Move(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool Zero(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Xor_r32_rm32 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == destination;

    private static bool Test(NativeInstruction instruction,
        NativeRegister register) =>
        instruction.Code == Code.Test_rm64_r64 &&
        Register(instruction, register) &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == register;

    private static bool Branch(NativeInstruction instruction,
        Code code, ulong target) =>
        instruction.Code == code &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool DirectJump(NativeInstruction instruction) =>
        instruction.Code == Code.Jmp_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool RipCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 0;

    private static bool RipLea(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool RipStoreOne(NativeInstruction instruction,
        ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == address &&
        instruction.Op1Kind == OpKind.Immediate8 &&
        instruction.Immediate8 == 1;

    private static bool RipLoad(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8;

    private static bool RipConditionalLoad(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Cmovne_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8;

    private static bool StackStore(NativeInstruction instruction,
        ulong offset, NativeRegister source) =>
        instruction.Code == Code.Mov_rm64_r64 &&
        instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RSP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool StackLoad(NativeInstruction instruction,
        NativeRegister destination, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        Register(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RSP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8;

    private static bool ObjectLoad(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source, int size,
        out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Mov_r64_rm64 ||
            !Register(instruction, destination) ||
            instruction.Op1Kind != OpKind.Memory ||
            instruction.MemoryBase != source ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemorySize.GetSize() != size ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = checked((int)instruction.MemoryDisplacement64);
        return true;
    }

    private static bool ObjectCompareZero(NativeInstruction instruction,
        NativeRegister source, out int offset)
    {
        offset = 0;
        if (instruction.Code != Code.Cmp_rm8_imm8 ||
            instruction.Op0Kind != OpKind.Memory ||
            instruction.MemoryBase != source ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemorySize.GetSize() != 1 ||
            instruction.Op1Kind != OpKind.Immediate8 ||
            instruction.Immediate8 != 0 ||
            instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = checked((int)instruction.MemoryDisplacement64);
        return true;
    }
}
