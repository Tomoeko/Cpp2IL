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

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the complete exact-target body of a zero-argument instance method that
/// calls a proved inherited reference getter or class-cast lookup, reads an
/// inherited string field,
/// and tail-calls String.Concat with one metadata-backed literal. The runtime
/// null arm is replaced by the null check inherent in the field read.
/// </summary>
internal static class X64LiteralConcatProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(MethodAnalysisContext Lookup, FieldAnalysisContext ValueField,
        MethodAnalysisContext Concat, string Literal);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } proof)
            return null;

        var receiver = new ISIL.Register(null, "rcx");
        var node = new ISIL.Register(null, "literal_concat_node");
        var value = new ISIL.Register(null, "literal_concat_value");
        var result = new ISIL.Register(null, "literal_concat_result");
        return
        [
            new(0, ISIL.OpCode.Call, proof.Lookup, node, receiver, new ISIL.Immediate(0)),
            new(1, ISIL.OpCode.Move, value,
                new ISIL.MemoryOperand(node, null, proof.ValueField.Offset)),
            new(2, ISIL.OpCode.Call, proof.Concat, result, value,
                new ISIL.StringLiteral(proof.Literal), new ISIL.Immediate(0)),
            new(3, ISIL.OpCode.Return, result),
        ];
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !OrdinaryClass(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            method.IsStatic || !method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemStringType) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer == 0 || decoded.Count < 20 ||
            decoded[0].IP != method.UnderlyingPointer)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End - region.Start is < 78 or > 96 ||
            !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
            !FileBacked(pe, region.Start, region.End))
            return null;

        var withinRegion = decoded.TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var body = withinRegion.Take(20).ToArray();
        if (body.Length != 20 || body[^1].NextIP > region.End ||
            withinRegion.Skip(20).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
            !TryProveShape(body) ||
            X86CallerExceptionRegionProof.Check(method, body,
                new HashSet<ulong> { body[19].IP }) != null ||
            Enumerable.Range(1, checked((int)(region.End - region.Start) - 1)).Any(offset =>
                app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)))
            return null;

        var flag = body[2].IPRelativeMemoryAddress;
        var slot = body[5].IPRelativeMemoryAddress;
        if (!WritableFileBacked(pe, unwind, slot, 8) ||
            !ZeroInitialized(unwind, flag) ||
            slot <= flag && flag - slot < 8 ||
            app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot) is not
                { Type: MetadataUsageType.StringLiteral, IsValid: true } usage ||
            app.LibCpp2IlContext.GetLiteralByAddress(slot) is not { } literal ||
            literal != usage.AsLiteral() ||
            !X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe, unwind,
                body[6].NearBranchTarget) ||
            X86RuntimeNullThrowProof.TryIdentify(app, body[19].NearBranchTarget) == null)
            return null;

        if (!TryBindLookup(app, owner, pe, unwind, body[10].NearBranchTarget,
                out var lookup, out var returnedClass) ||
            !ProveInheritedStringField(returnedClass, body[15].MemoryDisplacement64,
                out var valueField) ||
            !UniqueMethod(app, body[18].NearBranchTarget, out var concat) ||
            !ProveConcat(concat, app))
            return null;

        return new Evidence(lookup, valueField, concat, literal);
    }

    internal static bool TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 20 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return false;

        return body[0].Code == Code.Push_r64 && RegisterOperand(body[0], NativeRegister.RBX) &&
               Stack(body[1], Mnemonic.Sub, 0x20) &&
               RipCompareZero(body[2]) &&
               Move(body[3], NativeRegister.RBX, NativeRegister.RCX) &&
               Branch(body[4], Mnemonic.Jne, body[8].IP) &&
               RipLea(body[5], NativeRegister.RCX) &&
               DirectCall(body[6]) &&
               RipStoreOne(body[7], body[2].IPRelativeMemoryAddress) &&
               Zero(body[8], NativeRegister.EDX) &&
               Move(body[9], NativeRegister.RCX, NativeRegister.RBX) &&
               DirectCall(body[10]) &&
               Test(body[11], NativeRegister.RAX) &&
               Branch(body[12], Mnemonic.Je, body[19].IP) &&
               RipLoad(body[13], NativeRegister.RDX,
                   body[5].IPRelativeMemoryAddress) &&
               Zero(body[14], NativeRegister.R8D) &&
               FieldLoad(body[15], NativeRegister.RCX, NativeRegister.RAX) &&
               Stack(body[16], Mnemonic.Add, 0x20) &&
               body[17].Code == Code.Pop_r64 &&
               RegisterOperand(body[17], NativeRegister.RBX) &&
               DirectJump(body[18]) &&
               DirectCall(body[19]);
    }

    private static bool ProveLookup(MethodAnalysisContext lookup,
        TypeAnalysisContext owner, PE pe,
        X64UnwindProof.Index unwind, out TypeAnalysisContext returnedClass)
    {
        returnedClass = null!;
        if (lookup.DeclaringType is not { Definition: { GenericContainer: null } } baseOwner ||
            !OrdinaryClass(baseOwner) || !Inherits(owner, baseOwner) ||
            !ReferenceEquals(baseOwner.DeclaringAssembly, owner.DeclaringAssembly) ||
            lookup.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, baseOwner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            lookup.IsStatic || lookup.IsVirtual || lookup.IsVoid ||
            lookup.Name is ".ctor" or ".cctor" || lookup.Name != lookup.DefaultName ||
            lookup.Parameters.Count != 0 || lookup.GenericParameters.Count != 0 ||
            lookup.OverrideReturnType != null ||
            lookup.Attributes != lookup.DefaultAttributes ||
            lookup.ImplAttributes != lookup.DefaultImplAttributes ||
            lookup.Visibility is not (MethodAttributes.Public or MethodAttributes.Family or
                MethodAttributes.FamORAssem) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(lookup) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(lookup,
                requireUniqueBinding: false) ||
            lookup.ReturnType is not { Definition: { GenericContainer: null } } box ||
            !OrdinaryClass(box) ||
            !ReferenceEquals(box.DeclaringAssembly, owner.DeclaringAssembly) ||
            lookup.UnderlyingPointer == 0 ||
            !FileBacked(pe, lookup.UnderlyingPointer, lookup.UnderlyingPointer + 5) ||
            unwind.ClassifySpan(lookup.UnderlyingPointer, lookup.UnderlyingPointer + 5) is not
                { Kind: X64UnwindProof.SpanKind.NoEntry })
            return false;

        lookup.EnsureRawBytes();
        var native = X86Utils.Iterate(lookup).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(lookup.UnderlyingPointer, false);
        if (lookup.RawBytes.Length is < 5 or > 20 || rawStart < 0 ||
            rawStart > pe.GetRawBinaryContent().Length - 5 ||
            !pe.GetRawBinaryContent().Slice((int)rawStart, 5)
                .SequenceEqual(lookup.RawBytes.AsSpan().Slice(0, 5)) ||
            Enumerable.Range(1, 4).Any(offset =>
                lookup.AppContext.MethodsByAddress.ContainsKey(lookup.UnderlyingPointer +
                    (ulong)offset)) ||
            native.Length is < 2 or > 16 || native[0].IP != lookup.UnderlyingPointer ||
            native[0].Code != Code.Mov_r64_rm64 ||
            !RegisterOperand(native[0], NativeRegister.RAX) ||
            native[0].Op1Kind != OpKind.Memory ||
            native[0].MemoryBase != NativeRegister.RCX ||
            native[0].MemoryIndex != NativeRegister.None ||
            native[0].MemoryDisplacement64 is < 16 or > int.MaxValue ||
            native[0].MemorySize.GetSize() != 8 ||
            native[1].Code != Code.Retnq || native[1].OpCount != 0 ||
            native[1].NextIP != lookup.UnderlyingPointer + 5 ||
            native.Skip(2).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[1].NextIP,
                lookup.UnderlyingPointer + (ulong)lookup.RawBytes.Length) ||
            X86CallerExceptionRegionProof.Check(lookup, native.Take(2).ToArray(),
                new HashSet<ulong>()) != null)
            return false;

        var candidates = baseOwner.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)native[0].MemoryDisplacement64).ToArray();
        if (candidates is not [{ } current] ||
            current.Name != current.DefaultName ||
            current.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(current.FieldType, box) ||
            !UniqueFieldAcrossChain(owner, current, native[0].MemoryDisplacement64) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(current,
                    new LocalVariable("proved-lookup-owner", new ISIL.Register(null,
                        "proved-lookup-owner"), baseOwner),
                    checked((int)native[0].MemoryDisplacement64))))
            return false;

        returnedClass = box;
        return true;
    }

    private static bool ProveClassCastLookup(MethodAnalysisContext lookup,
        TypeAnalysisContext owner, ulong address, out TypeAnalysisContext returnedClass)
    {
        returnedClass = null!;
        if (lookup.UnderlyingPointer != address ||
            lookup.DeclaringType is not { Definition: { GenericContainer: null } } baseOwner ||
            !OrdinaryClass(baseOwner) || !Inherits(owner, baseOwner) ||
            !ReferenceEquals(baseOwner.DeclaringAssembly, owner.DeclaringAssembly) ||
            !HasUnhiddenBaseMethod(owner, baseOwner, lookup.Name) ||
            lookup.Visibility is not (MethodAttributes.Public or MethodAttributes.Family or
                MethodAttributes.FamORAssem))
            return false;

        lookup.EnsureRawBytes();
        var proof = X64ClassCastLookupProof.Find(lookup, X86Utils.Iterate(lookup).ToArray());
        if (proof == null ||
            !ReferenceEquals(proof.SourceField.DeclaringType, baseOwner) &&
            (proof.SourceGetter == null ||
             !Inherits(baseOwner, proof.SourceField.DeclaringType)) ||
            !ReferenceEquals(proof.TargetType, lookup.ReturnType))
            return false;

        returnedClass = proof.TargetType;
        return true;
    }

    internal static bool HasUnhiddenBaseMethod(TypeAnalysisContext owner,
        TypeAnalysisContext baseOwner, string name)
    {
        for (var type = owner; type != null; type = type.BaseType)
        {
            if (ReferenceEquals(type, baseOwner))
                return true;
            if (type.Methods.Any(method => method.Name == name) ||
                type.Fields.Any(field => field.Name == name) ||
                type.Properties.Any(property => property.Name == name) ||
                type.Events.Any(eventContext => eventContext.Name == name) ||
                type.NestedTypes.Any(nested => nested.Name == name))
                return false;
        }
        return false;
    }

    internal static bool TryBindLookup(ApplicationAnalysisContext app,
        TypeAnalysisContext owner, PE pe, X64UnwindProof.Index unwind,
        ulong address, out MethodAnalysisContext lookup,
        out TypeAnalysisContext returnedClass)
    {
        lookup = null!;
        returnedClass = null!;
        if (address == 0 || !app.MethodsByAddress.TryGetValue(address, out var aliases))
            return false;
        foreach (var candidate in aliases)
        {
            if (!ProveLookup(candidate, owner, pe, unwind, out var valueType) &&
                !ProveClassCastLookup(candidate, owner, address, out valueType))
                continue;
            if (lookup != null)
                return false;
            lookup = candidate;
            returnedClass = valueType;
        }
        return lookup != null;
    }

    internal static bool ProveInheritedStringField(TypeAnalysisContext box,
        ulong offset, out FieldAnalysisContext field)
    {
        field = null!;
        if (offset is < 16 or > int.MaxValue)
            return false;
        var candidates = new List<FieldAnalysisContext>();
        var seen = new HashSet<TypeAnalysisContext>();
        var reachedObject = false;
        for (var type = box; type != null; type = type.BaseType)
        {
            if (!seen.Add(type))
                return false;
            if (ReferenceEquals(type, box.AppContext.SystemTypes.SystemObjectType))
            {
                reachedObject = true;
                break;
            }
            if (!OrdinaryClass(type))
                return false;
            candidates.AddRange(type.Fields.Where(candidate => !candidate.IsStatic &&
                candidate.Offset == (long)offset));
        }
        if (!reachedObject || candidates is not [{ } matched] ||
            matched.Visibility != FieldAttributes.Public ||
            matched.Name != matched.DefaultName ||
            matched.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(matched.FieldType, box.AppContext.SystemTypes.SystemStringType) ||
            !UniqueFieldAcrossChain(box, matched, offset) ||
            !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                new FieldReference(matched,
                    new LocalVariable("proved-text-owner", new ISIL.Register(null,
                        "proved-text-owner"), matched.DeclaringType), (int)offset)))
            return false;
        field = matched;
        return true;
    }

    internal static bool ProveConcat(MethodAnalysisContext concat,
        ApplicationAnalysisContext app)
    {
        var text = app.SystemTypes.SystemStringType;
        var corlib = app.SystemTypes.SystemObjectType.DeclaringAssembly;
        if (!ReferenceEquals(text.DeclaringAssembly, corlib) ||
            corlib.Definition == null || corlib.Name != corlib.DefaultName ||
            corlib.DefaultName != "mscorlib" || corlib.Version != corlib.DefaultVersion ||
            (corlib.Culture ?? "") != (corlib.DefaultCulture ?? "") ||
            corlib.Flags != corlib.DefaultFlags ||
            !(corlib.PublicKey ?? []).SequenceEqual(corlib.DefaultPublicKey ?? []) ||
            !(corlib.PublicKeyToken ?? []).SequenceEqual(corlib.DefaultPublicKeyToken ?? []) ||
            concat.DeclaringType != text || concat.Name != "Concat" ||
            concat.Name != concat.DefaultName || !concat.IsStatic || concat.IsVirtual ||
            concat.Parameters.Count != 2 || concat.GenericParameters.Count != 0 ||
            concat.OverrideReturnType != null || !ReferenceEquals(concat.ReturnType, text) ||
            concat.Attributes != concat.DefaultAttributes ||
            concat.ImplAttributes != concat.DefaultImplAttributes ||
            concat.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                    NumMods: 0, Byref: 0, Pinned: 0 }, InternalParameterData: [var first, var second] } ||
            first.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            second.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_STRING,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            concat.Parameters.Any(parameter => parameter.IsRef ||
                !ReferenceEquals(parameter.ParameterType, text) ||
                !ReferenceEquals(parameter.DefaultParameterType, text) ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null))
            return false;
        return true;
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) &&
        type.Definition is { GenericContainer: null, PackingSizeIsDefault: true,
            ClassSizeIsDefault: true, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } } &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Attributes == type.DefaultAttributes &&
        (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.ExplicitLayout;

    private static bool Inherits(TypeAnalysisContext child, TypeAnalysisContext parent)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        var found = false;
        for (var type = child; type != null; type = type.BaseType)
        {
            if (!seen.Add(type))
                return false;
            if (ReferenceEquals(type, child.AppContext.SystemTypes.SystemObjectType))
                return found;
            if (!OrdinaryClass(type))
                return false;
            if (ReferenceEquals(type, parent))
                found = true;
        }
        return false;
    }

    internal static bool UniqueFieldAcrossChain(TypeAnalysisContext child,
        FieldAnalysisContext expected, ulong offset)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        var matches = 0;
        var derived = true;
        var reachedObject = false;
        for (var type = child; type != null; type = type.BaseType)
        {
            if (!seen.Add(type))
                return false;
            if (ReferenceEquals(type, child.AppContext.SystemTypes.SystemObjectType))
            {
                reachedObject = true;
                break;
            }
            if (!OrdinaryClass(type))
                return false;
            if (ReferenceEquals(type, expected.DeclaringType))
                derived = false;
            foreach (var field in type.Fields.Where(candidate => !candidate.IsStatic))
            {
                if (field.Offset < 0 || field.Offset != field.DefaultOffset ||
                    field.Attributes != field.DefaultAttributes ||
                    field.OverrideFieldType != null ||
                    derived && field.Offset < (long)offset + 8)
                    return false;
                if (field.Offset == (long)offset)
                {
                    if (!ReferenceEquals(field, expected))
                        return false;
                    matches++;
                }
            }
        }
        return reachedObject && matches == 1;
    }

    private static bool UniqueMethod(ApplicationAnalysisContext app, ulong address,
        out MethodAnalysisContext method)
    {
        method = null!;
        if (address == 0 || !app.MethodsByAddress.TryGetValue(address, out var bindings) ||
            bindings is not [var unique] || unique.UnderlyingPointer != address)
            return false;
        method = unique;
        return true;
    }

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first && last < pe.GetRawBinaryContent().Length &&
               (ulong)(last - first) == end - start - 1;
    }

    private static bool WritableFileBacked(PE pe, X64UnwindProof.Index unwind,
        ulong address, uint length)
    {
        if (address < unwind.ImageBase || address > ulong.MaxValue - length ||
            address + length - 1 - unwind.ImageBase > uint.MaxValue ||
            !FileBacked(pe, address, address + length))
            return false;
        for (var offset = 0U; offset < length; offset++)
            if (!unwind.IsWritableFileBackedRva((uint)(address + offset - unwind.ImageBase)))
                return false;
        return true;
    }

    private static bool ZeroInitialized(X64UnwindProof.Index unwind, ulong address) =>
        address >= unwind.ImageBase && address - unwind.ImageBase <= uint.MaxValue &&
        unwind.IsWritableZeroInitializedRva((uint)(address - unwind.ImageBase));

    private static bool RegisterOperand(NativeInstruction instruction, NativeRegister register) =>
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic,
        ulong amount) => instruction.Mnemonic == mnemonic &&
        RegisterOperand(instruction, NativeRegister.RSP) &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == amount;

    private static bool RipCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Mnemonic == Mnemonic.Lea && RegisterOperand(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool RipStoreOne(NativeInstruction instruction, ulong address) =>
        instruction.Code == Code.Mov_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == address &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool RipLoad(NativeInstruction instruction, NativeRegister destination,
        ulong address) => instruction.Code == Code.Mov_r64_rm64 &&
        RegisterOperand(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;

    private static bool FieldLoad(NativeInstruction instruction,
        NativeRegister destination, NativeRegister basis) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        RegisterOperand(instruction, destination) &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == basis &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.MemoryDisplacement64 is >= 16 and <= int.MaxValue;

    private static bool Move(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Mnemonic == Mnemonic.Mov &&
        RegisterOperand(instruction, destination) &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Zero(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Xor && RegisterOperand(instruction, register) &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register;

    private static bool Test(NativeInstruction instruction, NativeRegister register) =>
        instruction.Mnemonic == Mnemonic.Test && RegisterOperand(instruction, register) &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register;

    private static bool Branch(NativeInstruction instruction, Mnemonic mnemonic,
        ulong target) => instruction.Mnemonic == mnemonic &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;

    private static bool DirectJump(NativeInstruction instruction) =>
        instruction.Code is Code.Jmp_rel32_64 or Code.Jmp_rel8_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;
}
